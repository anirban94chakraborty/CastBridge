namespace CastBridge.Core.Cast;

/// <summary>A single playable item, described by the library or by the live capture session.</summary>
public sealed record MediaItem
{
    public required string Path { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Embedded cover art, when the file has any.</summary>
    public byte[]? AlbumArt { get; init; }

    public string? AlbumArtMimeType { get; init; }

    /// <summary>Art already published by the library scanner, so cover art is not re-registered.</summary>
    public string? ArtId { get; init; }

    /// <summary>True for live streams such as the WASAPI loopback capture.</summary>
    public bool IsLive { get; init; }

    public string? Id { get; init; }
}

/// <summary>
/// Turns a <see cref="MediaItem"/> into something a Cast device can fetch: it registers the file
/// with the local media server, transcodes unsupported formats, and publishes cover art.
/// </summary>
public interface IMediaUrlProvider
{
    /// <summary>
    /// Publishes an item and returns the request to send. <paramref name="targetDeviceAddress"/> is
    /// the speaker that will fetch it: the URL must contain an address that speaker can reach, which
    /// is not always this PC's first adapter.
    /// </summary>
    Task<CastMediaRequest> PrepareAsync(
        MediaItem item,
        string? targetDeviceAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Called when an item is no longer needed so temporary artifacts can be released.</summary>
    void Release(string itemId);
}
