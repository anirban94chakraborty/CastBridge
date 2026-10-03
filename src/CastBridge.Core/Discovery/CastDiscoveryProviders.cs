using System.Text.Json;
using Microsoft.Extensions.Logging;
using CastBridge.Core.Cast;

namespace CastBridge.Core.Discovery;

public interface ICastDiscovery
{
    string Name { get; }

    Task<IReadOnlyList<CastDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// mDNS discovery (_googlecast._tcp.local) via SharpCaster's locator. This is the primary path,
/// but Windows firewall or a router that drops multicast makes it unreliable, which is why the
/// subnet probe exists as a fallback.
/// </summary>
public sealed class MdnsCastDiscovery : ICastDiscovery
{
    private readonly ILogger<MdnsCastDiscovery>? _logger;
    private readonly ILoggerFactory? _loggerFactory;

    public MdnsCastDiscovery(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<MdnsCastDiscovery>();
    }

    public string Name => "mdns";

    public async Task<IReadOnlyList<CastDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var locator = new Sharpcaster.ChromecastLocator(_loggerFactory?.CreateLogger<Sharpcaster.ChromecastLocator>());
            var receivers = await locator.FindReceiversAsync(timeout, timeout, timeout).ConfigureAwait(false);

            var devices = new List<CastDevice>();
            foreach (var receiver in receivers)
            {
                var ip = receiver.DeviceUri?.Host;
                if (string.IsNullOrWhiteSpace(ip))
                    continue;

                devices.Add(DeviceMapper.FromMdns(receiver, ip, receiver.Port > 0 ? receiver.Port : 8009));
            }

            _logger?.LogDebug("mDNS found {Count} device(s)", devices.Count);
            return devices;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "mDNS discovery failed; falling back to the subnet probe");
            return Array.Empty<CastDevice>();
        }
    }
}

/// <summary>
/// Finds Cast devices by sweeping the local /24 for the device's local HTTP endpoint
/// (port 8008, /setup/eureka_info) and reading the JSON it returns. Works even when multicast
/// is blocked, and it also enriches devices with firmware, network and uptime details.
/// </summary>
public sealed class SubnetProbeDiscovery : ICastDiscovery
{
    private const int HttpPort = 8008;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly ILogger<SubnetProbeDiscovery>? _logger;
    private readonly int _concurrency;
    private readonly int _maxHostsPerSubnet;

    public SubnetProbeDiscovery(ILoggerFactory? loggerFactory = null, int concurrency = 32, int maxHostsPerSubnet = 254)
    {
        _logger = loggerFactory?.CreateLogger<SubnetProbeDiscovery>();
        _concurrency = Math.Clamp(concurrency, 4, 128);
        _maxHostsPerSubnet = maxHostsPerSubnet;
    }

    public string Name => "subnet";

    public async Task<IReadOnlyList<CastDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var subnets = LocalNetwork.GetSubnets();
        if (subnets.Count == 0)
            return Array.Empty<CastDevice>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var hosts = subnets
            .SelectMany(s => s.HostAddresses(_maxHostsPerSubnet))
            .Distinct()
            .ToArray();

        var found = new List<CastDevice>();
        using var gate = new SemaphoreSlim(_concurrency);

        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                var device = await ProbeAsync(host, timeoutCts.Token).ConfigureAwait(false);
                if (device is not null)
                {
                    lock (found)
                        found.Add(device);
                }
            }
            catch (OperationCanceledException)
            {
                // Scan budget exhausted, or the caller cancelled.
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Probe of {Host} failed", host);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Partial results are still useful.
        }

        _logger?.LogDebug("Subnet probe found {Count} device(s) in {Subnets} subnet(s)", found.Count, subnets.Count);
        return found;
    }

    private async Task<CastDevice?> ProbeAsync(System.Net.IPAddress host, CancellationToken cancellationToken)
    {
        var endpoint = host.ToString();

        if (!await IsPortOpenAsync(endpoint, HttpPort, cancellationToken).ConfigureAwait(false))
            return null;

        return await ReadEurekaInfoAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(350));
            await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the Cast device's unauthenticated setup endpoint and maps it to a device.</summary>
    public static async Task<CastDevice?> ReadEurekaInfoAsync(string host, CancellationToken cancellationToken, int port = HttpPort)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://{host}:{port}/setup/eureka_info?options=detail", cancellationToken).ConfigureAwait(false);
            return FromEurekaJson(json, host);
        }
        catch
        {
            return null;
        }
    }

    public static CastDevice? FromEurekaJson(string json, string fallbackHost)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? Get(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        long? GetLong(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (long)value.GetDouble() : null;

        var name = Get("name");
        var ip = Get("ip_address") ?? fallbackHost;
        var mac = Get("mac_address");
        var ssdp = Get("ssdp_udn");
        var firmware = Get("cast_build_revision") ?? Get("build_version");

        return new CastDevice
        {
            Id = ssdp ?? mac ?? ip,
            Name = string.IsNullOrWhiteSpace(name) ? $"Cast device ({ip})" : name!,
            IpAddress = ip,
            MacAddress = mac,
            FirmwareVersion = firmware,
            NetworkName = Get("ssid"),
            UptimeSeconds = GetLong("uptime"),
            DiscoverySource = "subnet",
            ExtraInfo = BuildEurekaInfo(root, Get),
        };
    }

    /// <summary>
    /// Pulls the setup fields that matter for deciding what this device can actually do.
    ///
    /// "opencast" is the important one: it is Google's opt-in flag for running the cloud-hosted
    /// media receivers. Xiaomi's Mi Smart Speaker reports false, which is why it accepts volume and
    /// status commands but refuses to launch the Default Media Receiver (CC1AD845). Knowing this
    /// up front is the difference between an honest "this speaker cannot play local files" and a
    /// mysterious "could not be started" error.
    /// </summary>
    private static Dictionary<string, string> BuildEurekaInfo(
        JsonElement root,
        Func<string, string?> get)
    {
        var extra = new Dictionary<string, string>
        {
            ["source"] = "eureka_info",
            ["build_version"] = get("build_version") ?? string.Empty,
            ["release_track"] = get("release_track") ?? string.Empty,
        };

        if (root.TryGetProperty("opt_in", out var optIn) && optIn.ValueKind == JsonValueKind.Object &&
            optIn.TryGetProperty("opencast", out var openCast))
        {
            extra["opencast"] = openCast.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => openCast.ToString(),
            };
        }

        return extra;
    }
}

internal static class DeviceMapper
{
    /// <summary>Maps a SharpCaster mDNS receiver, including its TXT records, onto our device model.</summary>
    public static CastDevice FromMdns(Sharpcaster.Models.ChromecastReceiver receiver, string ip, int port)
    {
        var extra = (IReadOnlyDictionary<string, string>?)receiver.ExtraInformation ?? new Dictionary<string, string>();

        string? TxT(string key) => extra.TryGetValue(key, out var value) ? value : null;

        var capabilities = CastCapabilities.Parse(TxT("ca"));
        var model = TxT("md") ?? receiver.Model;

        // "ve" in the TXT record is the Cast protocol version, not the device firmware - the firmware
        // revision only comes from the device's setup endpoint, so leave it for the probe to fill in.
        var extraInfo = new Dictionary<string, string>(extra)
        {
            ["source"] = "mdns",
        };

        if (TxT("ve") is { } protocolVersion)
            extraInfo["protocol_version"] = protocolVersion;

        return new CastDevice
        {
            Id = TxT("id") ?? $"{ip}:{port}",
            Name = string.IsNullOrWhiteSpace(receiver.Name) ? TxT("fn") ?? ip : receiver.Name,
            IpAddress = ip,
            Port = port,
            Model = model,
            Kind = CastCapabilities.Classify(model, capabilities),
            IsAudioOnly = CastCapabilities.IsAudioOnly(capabilities),
            DiscoverySource = "mdns",
            ExtraInfo = extraInfo,
        };
    }
}
