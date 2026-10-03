using Microsoft.Extensions.Logging;
using CastBridge.Core.Cast;
using CastBridge.Media.Library;
using CastBridge.Media.Live;
using CastBridge.Media.Server;
using CastBridge.Media.Transcode;

namespace CastBridge.Media;

public sealed record TranscodeProgress(string Path, double Percent, string? Error);

/// <summary>
/// The single place that knows how to turn "a file (or the system audio) on this PC" into a URL a
/// speaker can fetch. Everything else (UI, manager, CLI) only deals in <see cref="MediaItem"/>.
/// </summary>
public sealed class MediaPipeline : IMediaUrlProvider, IAsyncDisposable
{
    private readonly ILogger<MediaPipeline>? _logger;
    private readonly FfmpegLocator _locator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MediaPipeline(
        MediaCatalog catalog,
        MediaServer server,
        LibraryIndex index,
        TranscodeCache transcode,
        LiveCaptureService live,
        FfmpegLocator locator,
        ILoggerFactory? loggerFactory = null)
    {
        Catalog = catalog;
        Server = server;
        Index = index;
        Transcode = transcode;
        Live = live;
        _locator = locator;
        _logger = loggerFactory?.CreateLogger<MediaPipeline>();
    }

    public MediaCatalog Catalog { get; }

    public MediaServer Server { get; }

    public LibraryIndex Index { get; }

    public TranscodeCache Transcode { get; }

    public LiveCaptureService Live { get; }

    public string? FfmpegPath { get; set; }

    /// <summary>Raised while a file is being converted, for the progress area of the UI.</summary>
    public event EventHandler<TranscodeProgress>? TranscodeProgressChanged;

    public async Task StartAsync(int preferredPort = 45455, CancellationToken cancellationToken = default)
    {
        Server.SetPreferredPort(preferredPort);
        await Server.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CastMediaRequest> PrepareAsync(
        MediaItem item,
        string? targetDeviceAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (item.IsLive)
            return await PrepareLiveAsync(targetDeviceAddress, cancellationToken).ConfigureAwait(false);

        var path = item.Path;
        if (!File.Exists(path))
            throw new FileNotFoundException($"The file is gone: {path}", path);

        var artUrl = ResolveArtUrl(item, targetDeviceAddress);
        var format = AudioFormats.Resolve(path);
        var duration = item.Duration;

        if (duration is null)
        {
            var known = await LookupTrackAsync(path, cancellationToken).ConfigureAwait(false);
            duration = known?.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        }

        var contentUrl = string.Empty;
        var contentType = format.ContentType;

        if (format.IsNative)
        {
            var id = Catalog.RegisterFile(path, format.ContentType);
            contentUrl = Catalog.BuildUrl(id, targetDeviceAddress);
        }
        else
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cachePath = Transcode.GetCachePath(path);
                var progress = new Progress<double>(percent =>
                    TranscodeProgressChanged?.Invoke(this, new TranscodeProgress(path, percent, null)));

                var result = await Transcode
                    .EnsureFlacAsync(path, duration, FfmpegPath, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Success || result.Path is null)
                {
                    TranscodeProgressChanged?.Invoke(this, new TranscodeProgress(path, 0, result.Error));
                    throw new InvalidOperationException(
                        $"This file cannot be sent to the speaker yet: {result.Error}");
                }

                Transcode.MarkInUse(result.Path);
                var id = Catalog.RegisterFile(result.Path, "audio/flac");
                contentUrl = Catalog.BuildUrl(id, targetDeviceAddress);
                contentType = "audio/flac";
                _logger?.LogInformation("Serving converted FLAC for {File}", Path.GetFileName(path));
            }
            finally
            {
                _gate.Release();
            }
        }

        return new CastMediaRequest
        {
            ContentUrl = contentUrl,
            ContentType = contentType,
            Title = item.Title ?? Path.GetFileNameWithoutExtension(path),
            Artist = item.Artist,
            Album = item.Album,
            AlbumArtist = item.AlbumArtist,
            AlbumArtUrl = artUrl,
            Duration = duration,
            IsLive = false,
            LocalPath = path,
        };
    }

    /// <summary>Prepares the live "whatever this PC is playing" stream.</summary>
    public async Task<CastMediaRequest> PrepareLiveAsync(
        string? targetDeviceAddress = null,
        CancellationToken cancellationToken = default)
    {
        var started = await Live.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started)
            throw new InvalidOperationException(Live.LastError ?? "Live capture could not start.");

        var id = Catalog.RegisterLive(LiveCaptureService.DefaultSessionId, Live.GetContentType(LiveCaptureService.DefaultSessionId));
        var url = Catalog.BuildUrl(id, targetDeviceAddress);

        return new CastMediaRequest
        {
            ContentUrl = url,
            ContentType = Live.GetContentType(LiveCaptureService.DefaultSessionId),
            Title = "PC audio (live)",
            Artist = Live.CaptureDeviceName,
            Album = "CastBridge live",
            IsLive = true,
        };
    }

    public void Release(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        try
        {
            var cachePath = Transcode.GetCachePath(itemId);
            Transcode.ReleaseInUse(cachePath);
        }
        catch
        {
            // The file may have been deleted; nothing to release.
        }
    }

    private string? ResolveArtUrl(MediaItem item, string? targetDeviceAddress)
    {
        if (item.AlbumArt is { Length: > 0 } bytes)
        {
            var artId = Catalog.RegisterArt(item.Path, bytes, item.AlbumArtMimeType ?? "image/jpeg");
            return Catalog.BuildArtUrl(artId, targetDeviceAddress);
        }

        if (!string.IsNullOrWhiteSpace(item.ArtId))
            return Catalog.BuildArtUrl(item.ArtId!, targetDeviceAddress);

        return null;
    }

    private async Task<TrackRecord?> LookupTrackAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var tracks = await Index.GetTracksByPathAsync(new[] { path }, cancellationToken).ConfigureAwait(false);
            return tracks.Count > 0 ? tracks[0] : null;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Live.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await Index.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
