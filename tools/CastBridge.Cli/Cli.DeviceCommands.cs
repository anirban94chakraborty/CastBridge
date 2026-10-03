using System.Text.Json;
using CastBridge.Core.Cast;
using CastBridge.Core.Diagnostics;
using CastBridge.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace CastBridge.Cli;

internal sealed partial class Cli
{
    private async Task<int> ListDevicesAsync()
    {
        IReadOnlyList<CastDevice> found;

        if (_options.MdnsOnly || _options.SubnetOnly)
        {
            ICastDiscovery provider = _options.MdnsOnly
                ? new MdnsCastDiscovery(_loggerFactory)
                : new SubnetProbeDiscovery(_loggerFactory);
            found = await provider.DiscoverAsync(TimeSpan.FromSeconds(_options.ScanSeconds), CancellationToken.None);
        }
        else
        {
            found = _devices.Devices;
        }

        if (found.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{"#",-3} {"Name",-24} {"Address",-22} {"Kind",-14} {"Source",-9} State");
        Console.WriteLine(new string('-', 88));

        var row = 1;
        foreach (var device in found)
        {
            var address = device.Port == 8009 ? device.IpAddress : $"{device.IpAddress}:{device.Port}";
            Console.WriteLine(
                $"{row++,-3} {Trim(device.Name, 24),-24} {address,-22} {device.DisplayKind,-14} " +
                $"{device.DiscoverySource ?? "-",-9} {(device.IsStale ? "not seen now" : "online")}");

            if (_options.Verbose)
            {
                Console.WriteLine($"      model {device.Model ?? "-"}   firmware {device.FirmwareVersion ?? "-"}   " +
                                  $"audio-only {device.IsAudioOnly}   mac {device.MacAddress ?? "-"}");
                if (device.ExtraInfo.Count > 0)
                    Console.WriteLine("      " + string.Join(", ", device.ExtraInfo.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }

        return 0;
    }

    private async Task<int> VolumeAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: volume <device> [0-100] [--mute|--unmute]");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        if (_options.Args.Contains("--mute") || _options.Args.Contains("--unmute"))
        {
            var muted = _options.Args.Contains("--mute");
            await _devices.SetMutedAsync(device.Id, muted);
            Console.WriteLine($"{device.Name}: muted = {muted}");
        }

        var level = _options.Args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (level is not null && double.TryParse(level, out var percent))
        {
            await _devices.SetVolumeAsync(device.Id, percent / 100.0);

            // The write is coalesced and the receiver applies it asynchronously, so reading straight
            // back reports the value the device had *before* the change. Give it time to land.
            Console.WriteLine($"{device.Name}: volume set to {Math.Clamp(percent, 0, 100):F0}%…");
            await Task.Delay(1500);
        }

        var state = await ReadStateAsync(device.Id);
        if (state is not null)
            Console.WriteLine($"{device.Name}: volume {state.Volume.Percent}%  muted {state.Volume.Muted}");

        return 0;
    }

    private async Task<int> StatusAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: status <device>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        var state = await ReadStateAsync(device.Id);
        if (state is null)
            return 1;

        // The running receiver application decides what is even possible here: media commands need a
        // media session, and a mirroring session simply does not have one.
        var client = await _devices.GetClientAsync(device.Id);
        var receiver = client.Receiver;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            device = state.Device.Name,
            runningApp = receiver.RunningAppName ?? receiver.RunningAppId,
            runningAppId = receiver.RunningAppId,
            mirroring = receiver.IsMirroring,
            standby = receiver.IsStandBy,
            address = state.Device.IpAddress,
            kind = state.Device.DisplayKind,
            connection = state.Connection.ToString(),
            volume = state.Volume.Percent,
            muted = state.Volume.Muted,
            app = state.Media.Title,
            artist = state.Media.Artist,
            album = state.Media.Album,
            playerState = state.Media.PlayerState.ToString(),
            position = state.Media.Position.ToString(@"m\:ss"),
            duration = state.Media.Duration?.ToString(@"m\:ss"),
            isLive = state.Media.IsLive,
            item = state.Media.CurrentItemId,
            items = state.Media.ItemCount,
            repeat = state.Media.RepeatMode,
            shuffle = state.Media.Shuffle,
            idleReason = state.Media.IdleReason,
            error = state.LastError,
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    private async Task<int> GroupsAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: groups <device>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        var members = await _devices.GetGroupMembersAsync(device.Id);
        if (members.Count == 0)
        {
            // A single-device group reports only itself, so "no members" is the normal answer.
            Console.WriteLine($"{device.Name}: not a multi-room group, or the group has no other members.");
            return 0;
        }

        Console.WriteLine($"{device.Name}: {members.Count} member(s)");
        foreach (var member in members)
            Console.WriteLine($"  - {member.Name} ({member.DeviceId})  volume {member.Volume.Percent}%");

        return 0;
    }

    private async Task<int> StopAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: stop <device>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        await _devices.StopAsync(device.Id);
        Console.WriteLine($"{device.Name}: stopped.");
        return 0;
    }

    private int DiagnosticsAsync()
    {
        IReadOnlyList<DiagnosticItem> items;
        try
        {
            items = NetworkDiagnostics.Run(_devices.Devices, _server.IsRunning ? _server.Port : null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnostics failed");
            items = Array.Empty<DiagnosticItem>();
        }

        Console.WriteLine();
        Console.WriteLine(NetworkDiagnostics.Format(items));
        return items.Any(i => i.Severity == DiagnosticSeverity.Problem) ? 1 : 0;
    }
}
