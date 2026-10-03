using System.Text.Json;
using Microsoft.Extensions.Logging;
using CastBridge.Core.Cast;

namespace CastBridge.Core.Discovery;

/// <summary>
/// Remembers devices between runs so the app shows speakers immediately at startup instead of
/// waiting for a scan, and so a device on a flaky network is still reachable by address.
/// Manual entries survive pruning.
/// </summary>
public sealed class CastDeviceCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<CastDeviceCache>? _logger;
    private readonly List<CachedDevice> _entries = new();

    public CastDeviceCache(string? path = null, ILoggerFactory? loggerFactory = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDirectory, "devices.json");
        _logger = loggerFactory?.CreateLogger<CastDeviceCache>();
        Load();
    }

    public IReadOnlyList<CachedDevice> Entries => _entries;

    /// <summary>
    /// Rebuilds the cache from the merged, currently visible devices. Working from the merged list
    /// (instead of every raw discovery result) is what keeps one physical device to one entry: the
    /// same speaker reports a dashed UUID over mDNS and a bare hex id over its setup endpoint.
    /// </summary>
    public void Sync(IEnumerable<CastDevice> liveDevices)
    {
        var manual = _entries.Where(e => e.IsManual).ToList();
        var rebuilt = new List<CachedDevice>();

        foreach (var device in liveDevices.Where(d => !d.IsStale))
        {
            var cached = CachedDevice.FromDevice(device);

            var manualEntry = manual.FirstOrDefault(m => IdsMatch(m.Id, device.Id));
            if (manualEntry is not null)
            {
                cached.IsManual = true;
                manual.Remove(manualEntry);
            }

            var duplicate = rebuilt.FirstOrDefault(e => IdsMatch(e.Id, cached.Id));
            if (duplicate is not null)
                rebuilt.Remove(duplicate);

            rebuilt.Add(cached);
        }

        // Devices the user typed in by hand always stay, even while they are unreachable.
        rebuilt.AddRange(manual);

        _entries.Clear();
        _entries.AddRange(rebuilt);
    }

    private static bool IdsMatch(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(a.Replace("-", string.Empty, StringComparison.Ordinal), b.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    public void Remember(CastDevice device)
    {
        if (device.IsStale)
            return;

        var existing = _entries.FirstOrDefault(e => IdsMatch(e.Id, device.Id));
        if (existing is null)
        {
            _entries.Add(CachedDevice.FromDevice(device));
        }
        else
        {
            existing.Name = device.Name;
            existing.IpAddress = device.IpAddress;
            existing.Port = device.Port;
            existing.Model = device.Model;
            existing.Kind = device.Kind.ToString();
            existing.IsAudioOnly = device.IsAudioOnly;
            existing.LastSeenUtc = device.LastSeen.UtcDateTime;
            existing.IsManual |= device.IsManual;
        }
    }

    public void RememberAll(IEnumerable<CastDevice> devices)
    {
        foreach (var device in devices)
            Remember(device);
    }

    public void Forget(string deviceId)
    {
        _entries.RemoveAll(e => e.Id == deviceId);
        Save();
    }

    /// <summary>Cached devices as device models, flagged stale so the UI can mark them as "not seen".</summary>
    public IReadOnlyList<CastDevice> ToDevices() =>
        _entries.Select(e => new CastDevice
        {
            Id = e.Id,
            Name = e.Name,
            IpAddress = e.IpAddress,
            Port = e.Port <= 0 ? 8009 : e.Port,
            Model = e.Model,
            Kind = Enum.TryParse<CastDeviceKind>(e.Kind, out var kind) ? kind : CastDeviceKind.Unknown,
            IsAudioOnly = e.IsAudioOnly,
            IsManual = e.IsManual,
            IsStale = true,
            FirstSeen = e.LastSeenUtc,
            LastSeen = e.LastSeenUtc,
            DiscoverySource = "cache",
        }).ToArray();

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save the device cache to {Path}", _path);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<CachedDevice>>(json, JsonOptions);
            if (entries is not null)
                _entries.AddRange(entries);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the device cache from {Path}", _path);
        }
    }

    public sealed class CachedDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; } = 8009;
        public string? Model { get; set; }
        public string Kind { get; set; } = nameof(CastDeviceKind.Unknown);
        public bool IsAudioOnly { get; set; }
        public bool IsManual { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        public static CachedDevice FromDevice(CastDevice device) => new()
        {
            Id = device.Id,
            Name = device.Name,
            IpAddress = device.IpAddress,
            Port = device.Port,
            Model = device.Model,
            Kind = device.Kind.ToString(),
            IsAudioOnly = device.IsAudioOnly,
            IsManual = device.IsManual,
            LastSeenUtc = device.LastSeen.UtcDateTime,
        };
    }
}

/// <summary>Where the app keeps its per-user state. Kept in one place so the CLI and the GUI agree.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CastBridge");

    public static string ArtDirectory => Path.Combine(DataDirectory, "art");

    public static string TranscodeDirectory => Path.Combine(DataDirectory, "transcode");

    public static string FfmpegDirectory => Path.Combine(DataDirectory, "ffmpeg");

    public static string LibraryDatabase => Path.Combine(DataDirectory, "library.db");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => Path.Combine(DataDirectory, "logs", "castbridge.log");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ArtDirectory);
        Directory.CreateDirectory(TranscodeDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
    }
}
