using System.Net;
using Microsoft.Extensions.Logging;
using CastBridge.Core.Cast;

namespace CastBridge.Core.Discovery;

/// <summary>
/// Merges every discovery provider plus the on-disk cache into one device list, refreshes it on a
/// timer, and marks devices that stopped answering as stale instead of dropping them.
/// </summary>
public sealed class CastDiscoveryService : IAsyncDisposable
{
    private readonly IReadOnlyList<ICastDiscovery> _providers;
    private readonly CastDeviceCache _cache;
    private readonly ILogger<CastDiscoveryService>? _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _stalePruneAge;
    private readonly List<CastDevice> _devices = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public CastDiscoveryService(
        IReadOnlyList<ICastDiscovery> providers,
        CastDeviceCache? cache = null,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? refreshInterval = null,
        TimeSpan? stalePruneAge = null)
    {
        _providers = providers;
        _cache = cache ?? new CastDeviceCache(loggerFactory: loggerFactory);
        _logger = loggerFactory?.CreateLogger<CastDiscoveryService>();
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(30);
        _stalePruneAge = stalePruneAge ?? TimeSpan.FromMinutes(15);
    }

    public IReadOnlyList<CastDevice> Devices
    {
        get
        {
            lock (_devices)
                return _devices.ToArray();
        }
    }

    public event EventHandler<IReadOnlyList<CastDevice>>? DevicesChanged;

    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Loads the cache, scans once, then keeps scanning in the background.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_devices)
        {
            _devices.Clear();
            _devices.AddRange(_cache.ToDevices());
        }

        RaiseChanged();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token), CancellationToken.None);
    }

    public async Task<IReadOnlyList<CastDevice>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            return Devices;

        try
        {
            var results = await Task.WhenAll(_providers.Select(p => p.DiscoverAsync(ScanTimeout, cancellationToken)))
                .ConfigureAwait(false);

            var discovered = results.SelectMany(r => r).ToList();
            Merge(discovered);
            _cache.Sync(Devices);
            _cache.Save();
            RaiseChanged();
            return Devices;
        }
        catch (OperationCanceledException)
        {
            return Devices;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Device refresh failed");
            return Devices;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Adds a device the user typed in. Validates that something Cast-like answers at that address
    /// and keeps it in the cache even when it is later unreachable.
    /// </summary>
    public async Task<CastDevice?> AddManualAsync(string hostOrAddress, CancellationToken cancellationToken = default)
    {
        var host = hostOrAddress.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return null;

        // Accept "name", "ip" and "ip:port".
        var port = 8009;
        if (host.Contains(':', StringComparison.Ordinal))
        {
            var parts = host.Split(':', 2);
            host = parts[0];
            if (int.TryParse(parts[1], out var parsedPort))
                port = parsedPort;
        }

        var address = await ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            _logger?.LogWarning("Could not resolve {Host}", host);
            return null;
        }

        var eureka = await SubnetProbeDiscovery.ReadEurekaInfoAsync(address, cancellationToken).ConfigureAwait(false);
        var device = eureka is not null
            ? eureka with { IsManual = true, Port = port, DiscoverySource = "manual" }
            : new CastDevice
            {
                Id = $"{address}:{port}",
                Name = host,
                IpAddress = address,
                Port = port,
                IsManual = true,
                DiscoverySource = "manual",
            };

        Merge(new[] { device });
        _cache.Remember(device);
        _cache.Save();
        RaiseChanged();
        return device;
    }

    public void Remove(string deviceId)
    {
        lock (_devices)
            _devices.RemoveAll(d => d.Id == deviceId);

        _cache.Forget(deviceId);
        RaiseChanged();
    }

    private static async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed.ToString();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void Merge(IReadOnlyList<CastDevice> discovered)
    {
        lock (_devices)
        {
            foreach (var device in discovered)
            {
                var index = FindMergeTarget(device);

                if (index < 0)
                {
                    _devices.Add(device);
                    continue;
                }

                var existing = _devices[index];
                _devices[index] = MergeDevice(existing, device);
            }

            var now = DateTimeOffset.UtcNow;
            for (var i = _devices.Count - 1; i >= 0; i--)
            {
                var device = _devices[i];
                if (discovered.Any(d =>
                        string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(d.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase) && d.Port == device.Port &&
                         string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase))))
                    continue;

                if (now - device.LastSeen > _stalePruneAge && !device.IsManual)
                {
                    _devices.RemoveAt(i);
                    continue;
                }

                _devices[i] = device with { IsStale = true };
            }

            _devices.Sort((a, b) =>
            {
                var stale = a.IsStale.CompareTo(b.IsStale);
                return stale != 0 ? stale : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }
    }

    /// <summary>
    /// Finds the entry a freshly discovered device belongs to. Identity is the Cast device id where
    /// possible: a multi-room group and the speaker hosting it share an IP address (and differ only
    /// by port), so matching on IP alone would silently merge two different targets.
    /// </summary>
    private int FindMergeTarget(CastDevice incoming)
    {
        // The same physical device reports its id as a dashed UUID over mDNS and as a bare hex id in
        // the setup endpoint, so compare with the separators removed.
        var byId = _devices.FindIndex(d => IdsMatch(d.Id, incoming.Id));
        if (byId >= 0)
            return byId;

        var sameAddress = _devices
            .Select((d, i) => (Device: d, Index: i))
            .Where(x => string.Equals(x.Device.IpAddress, incoming.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                        x.Device.Port == incoming.Port)
            .ToArray();

        if (incoming.DiscoverySource == "mdns")
        {
            // The probe or the cache saw this endpoint earlier; upgrade that entry to the mDNS view.
            var upgrade = sameAddress.FirstOrDefault(x => x.Device.DiscoverySource != "mdns");
            return upgrade.Device is null ? -1 : upgrade.Index;
        }

        // Prefer a live entry over a cached one: after a group is renamed or re-paired, the cache can
        // describe an endpoint that no longer exists.
        var live = sameAddress.Where(x => x.Device.DiscoverySource is "mdns" or "subnet").ToArray();
        if (live.Length == 1)
            return live[0].Index;

        if (sameAddress.Length == 1)
            return sameAddress[0].Index;

        var candidates = live.Length > 0 ? live : sameAddress;
        if (candidates.Length > 1)
        {
            var byName = candidates.FirstOrDefault(x => string.Equals(x.Device.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
            if (byName.Device is not null)
                return byName.Index;
        }

        // Nothing on that exact endpoint: fall back to a name match on the same host (a device whose
        // port we do not know yet), but never hijack a different Cast target.
        return _devices.FindIndex(d =>
            string.Equals(d.IpAddress, incoming.IpAddress, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Name, incoming.Name, StringComparison.OrdinalIgnoreCase) &&
            d.DiscoverySource != "mdns");
    }

    /// <summary>Compares Cast device ids, ignoring UUID formatting differences.</summary>
    internal static bool IdsMatch(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(NormalizeId(a), NormalizeId(b), StringComparison.OrdinalIgnoreCase);

        static string NormalizeId(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static CastDevice MergeDevice(CastDevice existing, CastDevice incoming)
    {
        // Prefer the richer mDNS identity, but take every extra detail the probe knows about.
        // mDNS carries the identity the Cast protocol itself uses, so its id wins over the probe's.
        var preferIncomingId = incoming.DiscoverySource == "mdns" || existing.DiscoverySource is "cache" or "manual";

        return existing with
        {
            Id = preferIncomingId ? incoming.Id : existing.Id,
            Name = incoming.Name.Length > 0 ? incoming.Name : existing.Name,
            IpAddress = incoming.IpAddress,
            Port = incoming.Port,
            Model = incoming.Model ?? existing.Model,
            FirmwareVersion = incoming.FirmwareVersion ?? existing.FirmwareVersion,
            Kind = incoming.Kind != CastDeviceKind.Unknown ? incoming.Kind : existing.Kind,
            IsAudioOnly = incoming.IsAudioOnly || existing.IsAudioOnly,
            MacAddress = incoming.MacAddress ?? existing.MacAddress,
            NetworkName = incoming.NetworkName ?? existing.NetworkName,
            UptimeSeconds = incoming.UptimeSeconds ?? existing.UptimeSeconds,
            IsManual = existing.IsManual || incoming.IsManual,
            IsStale = false,
            FirstSeen = existing.FirstSeen,
            LastSeen = incoming.LastSeen,
            // mDNS stays the reported source when it contributed, because it is the identity the Cast protocol uses.
            DiscoverySource = incoming.DiscoverySource == "mdns" || existing.DiscoverySource == "mdns"
                ? "mdns"
                : incoming.DiscoverySource ?? existing.DiscoverySource,
            ExtraInfo = incoming.ExtraInfo.Count > 0 ? incoming.ExtraInfo : existing.ExtraInfo,
        };
    }

    private void RaiseChanged() => DevicesChanged?.Invoke(this, Devices);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Background refresh failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_loopCts is not null)
                await _loopCts.CancelAsync().ConfigureAwait(false);

            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Shutting down.
        }

        _loopCts?.Dispose();
        _refreshGate.Dispose();
    }
}
