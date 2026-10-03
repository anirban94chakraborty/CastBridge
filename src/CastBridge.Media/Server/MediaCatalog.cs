using System.Security.Cryptography;
using System.Text;
using CastBridge.Core.Discovery;

namespace CastBridge.Media.Server;

public enum MediaEntryKind
{
    File,
    Live,
}

public sealed record MediaEntry(
    string Id,
    MediaEntryKind Kind,
    string? Path,
    string ContentType,
    long? Length,
    string? LiveSessionId);

/// <summary>
/// Maps short, stable ids to media on this machine. Ids are derived from the file path (or the live
/// session name) so a URL handed to a speaker stays valid across restarts, and so a device that
/// keeps buffering an old URL still gets an answer.
/// </summary>
public sealed class MediaCatalog
{
    private readonly Dictionary<string, MediaEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _artPaths = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly string _artDirectory;

    public MediaCatalog(string? artDirectory = null)
    {
        _artDirectory = artDirectory ?? AppPaths.ArtDirectory;
        Directory.CreateDirectory(_artDirectory);
    }

    /// <summary>The TCP port the media server is listening on.</summary>
    public int Port { get; set; } = 45455;

    /// <summary>Address to build URLs with when the target device's address is unknown.</summary>
    public string? FallbackAddress { get; set; }

    public IReadOnlyCollection<MediaEntry> Entries
    {
        get
        {
            lock (_sync)
                return _entries.Values.ToArray();
        }
    }

    public string RegisterFile(string path, string contentType)
    {
        var full = System.IO.Path.GetFullPath(path);
        var id = Hash($"{full}|{contentType}");
        long? length = null;

        try
        {
            length = new FileInfo(full).Length;
        }
        catch
        {
            // The device will find out the hard way; the entry is still registered for diagnostics.
        }

        lock (_sync)
            _entries[id] = new MediaEntry(id, MediaEntryKind.File, full, contentType, length, null);

        return id;
    }

    public string RegisterLive(string sessionId, string contentType)
    {
        var id = Hash($"live|{sessionId}");
        lock (_sync)
            _entries[id] = new MediaEntry(id, MediaEntryKind.Live, null, contentType, null, sessionId);

        return id;
    }

    public MediaEntry? Get(string id)
    {
        lock (_sync)
            return _entries.TryGetValue(id, out var entry) ? entry : null;
    }

    /// <summary>Publishes cover art and returns its id. Identical art is written once.</summary>
    public string RegisterArt(string cacheKey, byte[] bytes, string mimeType)
    {
        var extension = mimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png"
            : mimeType.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
            : mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
            : ".jpg";

        var id = Hash($"art|{cacheKey}|{bytes.Length}");
        var path = System.IO.Path.Combine(_artDirectory, id + extension);

        lock (_sync)
        {
            _artPaths[id] = path;
            if (File.Exists(path))
                return id;
        }

        try
        {
            Directory.CreateDirectory(_artDirectory);
            File.WriteAllBytes(path, bytes);
        }
        catch
        {
            // Art is cosmetic: if it cannot be cached, the speaker just shows no image.
        }

        return id;
    }

    public string? GetArtPath(string id)
    {
        lock (_sync)
            return _artPaths.TryGetValue(id, out var path) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Builds the URL a speaker should fetch. When we know which speaker will get it, the address is
    /// chosen from the adapter that shares its subnet - picking a VPN or virtual adapter here is a
    /// classic "the track never plays" bug.
    /// </summary>
    public string BuildUrl(string id, string? targetDeviceAddress = null)
    {
        var host = ResolveHost(targetDeviceAddress);
        return $"http://{host}:{Port}/media/{id}";
    }

    public string BuildArtUrl(string artId, string? targetDeviceAddress = null)
    {
        var host = ResolveHost(targetDeviceAddress);
        return $"http://{host}:{Port}/art/{artId}";
    }

    public string ResolveHost(string? targetDeviceAddress = null)
    {
        if (!string.IsNullOrWhiteSpace(targetDeviceAddress) &&
            System.Net.IPAddress.TryParse(targetDeviceAddress, out var deviceIp))
        {
            var local = CastBridge.Core.Discovery.LocalNetwork.FindLocalAddressFor(deviceIp);
            if (local is not null)
                return local.ToString();
        }

        if (!string.IsNullOrWhiteSpace(FallbackAddress))
            return FallbackAddress!;

        var best = CastBridge.Core.Discovery.LocalNetwork
            .GetSubnets()
            .OrderByDescending(s => s.PrefixLength)
            .FirstOrDefault();

        return best?.LocalAddress.ToString() ?? "127.0.0.1";
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
