using CastBridge.Core;
using Microsoft.Extensions.Logging;

namespace CastBridge.Cli;

/// <summary>
/// Drives playback on a device somebody else is casting to - the same path the desktop app uses.
/// It exists because "the buttons do nothing" is not a useful bug report: this prints what the
/// speaker itself says about the session.
/// </summary>
internal sealed partial class Cli
{
    private async Task<int> TransportAsync()
    {
        if (_options.Args.Length < 2)
        {
            Console.WriteLine("usage: transport <device> <play|pause|stop|next|prev|toggle>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        var action = _options.Args[1].ToLowerInvariant();

        try
        {
            // Connect first: every media command needs the media session id that the running
            // receiver published, and that only arrives over a live connection.
            await _devices.GetClientAsync(device.Id);

            switch (action)
            {
                case "play":
                    await _devices.PlayAsync(device.Id);
                    break;
                case "pause":
                    await _devices.PauseAsync(device.Id);
                    break;
                case "stop":
                    // Leave the receiver app running: it belongs to whoever started the cast.
                    await _devices.StopAsync(device.Id, stopReceiverApp: false);
                    break;
                case "next":
                    await _devices.NextAsync(device.Id);
                    break;
                case "prev" or "previous":
                    await _devices.PreviousAsync(device.Id);
                    break;
                case "toggle":
                    var before = _devices.GetState(device.Id);
                    if (before?.Media.IsPlaying == true)
                        await _devices.PauseAsync(device.Id);
                    else
                        await _devices.PlayAsync(device.Id);
                    break;
                default:
                    Console.WriteLine($"unknown action '{action}'");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (_options.Verbose)
                Console.Error.WriteLine(ex);
            return 1;
        }

        await Task.Delay(700);
        var state = _devices.GetState(device.Id);
        PrintMedia(device.Name, state);
        return 0;
    }

    /// <summary>
    /// Follows a device the way the desktop app does: connect once, then report every change.
    /// Useful for watching a browser's cast session from the outside.
    /// </summary>
    private async Task<int> WatchAsync()
    {
        var seconds = 30;
        var nameOrSeconds = _options.Args.LastOrDefault();
        if (nameOrSeconds is not null && int.TryParse(nameOrSeconds, out var parsed) && parsed > 0)
            seconds = parsed;

        var deviceName = _options.Args.Length > 0 && !int.TryParse(_options.Args[0], out _)
            ? _options.Args[0]
            : null;

        if (deviceName is null)
        {
            var fallback = _devices.Devices.FirstOrDefault();
            if (fallback is null)
            {
                Console.WriteLine("No devices found.");
                return 1;
            }

            deviceName = fallback.Name;
        }

        var device = Resolve(deviceName);
        if (device is null)
            return 1;

        Console.WriteLine($"watching {device.Name} for {seconds}s…");

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        _devices.DeviceStateChanged += (_, state) =>
        {
            if (state.Device.Id.Equals(device.Id, StringComparison.OrdinalIgnoreCase))
                PrintMedia(device.Name, state);
        };

        await _devices.WatchAsync(device.Id, done.Token);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, done.Token);
        }
        catch (OperationCanceledException)
        {
            // The watch window ended.
        }

        return 0;
    }

    private static void PrintMedia(string name, DeviceRuntimeState? state)
    {
        if (state is null)
        {
            Console.WriteLine($"{name}: no state yet");
            return;
        }

        var media = state.Media;
        var duration = media.Duration is { } d ? d.ToString(@"m\:ss") : "-";
        var app = state.Device.Name;
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] {app}: {state.Connection} volume {state.Volume.Percent}% " +
            $"{(state.Volume.Muted ? "(muted) " : string.Empty)}" +
            $"{media.PlayerState}: {media.Title ?? "(nothing)"}" +
            $"{(media.Artist is null ? string.Empty : $" - {media.Artist}")} " +
            $"{media.Position.ToString(@"m\:ss")}/{duration}" +
            $"{(media.IdleReason is null ? string.Empty : $" idle={media.IdleReason}")}");

        if (!string.IsNullOrEmpty(state.LastError))
            Console.WriteLine($"             error: {state.LastError}");
    }
}
