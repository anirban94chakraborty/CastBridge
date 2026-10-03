using Microsoft.Extensions.Logging;
using CastBridge.Core;
using CastBridge.Core.Cast;
using CastBridge.Core.Discovery;
using CastBridge.Media.Compatibility;
using CastBridge.Media.Library;
using CastBridge.Media.Live;
using CastBridge.Media.Server;
using CastBridge.Media.Transcode;

namespace CastBridge.Media;

public sealed class CastBridgeOptions
{
    /// <summary>
    /// When false, no local HTTP server is started and no port is bound. The desktop app runs in
    /// this mode on purpose: it only drives volume and transport, and casting happens elsewhere.
    /// </summary>
    public bool EnableMediaServer { get; init; }

    public int MediaServerPort { get; init; } = 45455;

    public string? FfmpegPath { get; init; }

    public bool EnableMdns { get; init; } = true;

    public bool EnableSubnetProbe { get; init; } = true;

    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    public long TranscodeCacheBytes { get; init; } = 3L * 1024 * 1024 * 1024;

    public LiveQuality LiveQuality { get; init; } = LiveQuality.Mp3_256;

    public string? LiveCaptureDeviceId { get; init; }
}

/// <summary>
/// Builds every service the app needs, in one place, so the GUI and the CLI behave identically.
/// Nothing here touches the UI, which is also what makes the CLI a usable debugging tool.
/// </summary>
public sealed class CastBridgeServices : IAsyncDisposable
{
    private CastBridgeServices(CastBridgeOptions options, ILoggerFactory? loggerFactory)
    {
        Options = options;
        LoggerFactory = loggerFactory;

        Ffmpeg = new FfmpegLocator(loggerFactory);
        Catalog = new MediaCatalog();

        if (options.EnableMediaServer)
        {
            Library = new LibraryIndex();
            Transcode = new TranscodeCache(Ffmpeg, maxCacheBytes: options.TranscodeCacheBytes, loggerFactory: loggerFactory);
            Live = new LiveCaptureService(Ffmpeg, loggerFactory)
            {
                Quality = options.LiveQuality,
                CaptureDeviceId = options.LiveCaptureDeviceId,
                FfmpegPath = options.FfmpegPath,
            };

            Server = new MediaServer(
                Catalog,
                Live,
                new MediaServerOptions { PreferredPort = options.MediaServerPort },
                loggerFactory);

            Media = new MediaPipeline(Catalog, Server, Library, Transcode, Live, Ffmpeg, loggerFactory)
            {
                FfmpegPath = options.FfmpegPath,
            };

            Scanner = new LibraryScanner(Library, Catalog, loggerFactory);
        }

        var providers = new List<ICastDiscovery>();
        if (options.EnableMdns)
            providers.Add(new MdnsCastDiscovery(loggerFactory));
        if (options.EnableSubnetProbe)
            providers.Add(new SubnetProbeDiscovery(loggerFactory));

        Discovery = new CastDiscoveryService(providers, loggerFactory: loggerFactory, refreshInterval: options.RefreshInterval)
        {
            ScanTimeout = options.ScanTimeout,
        };

        // A control-only app has no media provider, and the manager is happy without one: every
        // volume, mute and transport call works, and only casting throws.
        Devices = new CastDeviceManager(Discovery, Media, loggerFactory);

        if (options.EnableMediaServer)
            Probe = new CompatibilityProbe(Devices, Media!, Server!, loggerFactory);
    }

    public CastBridgeOptions Options { get; }

    public ILoggerFactory? LoggerFactory { get; }

    public FfmpegLocator Ffmpeg { get; }

    public MediaCatalog Catalog { get; }

    /// <summary>Null when <see cref="CastBridgeOptions.EnableMediaServer"/> is off.</summary>
    public MediaServer? Server { get; }

    /// <summary>Null when the media server is off.</summary>
    public LibraryIndex? Library { get; }

    public LibraryScanner? Scanner { get; }

    public TranscodeCache? Transcode { get; }

    public LiveCaptureService? Live { get; }

    public MediaPipeline? Media { get; }

    /// <summary>Null when the media server is off; casting is then unavailable by design.</summary>
    public CompatibilityProbe? Probe { get; }

    public CastDiscoveryService Discovery { get; }

    public CastDeviceManager Devices { get; }

    public static CastBridgeServices Create(CastBridgeOptions? options = null, ILoggerFactory? loggerFactory = null) =>
        new(options ?? new CastBridgeOptions(), loggerFactory);

    /// <summary>
    /// Starts discovery, and the media server only when it is enabled. Returns the bound media port,
    /// or -1 when no server was started.
    /// </summary>
    public async Task<int> StartAsync(bool scanForDevices = true, CancellationToken cancellationToken = default)
    {
        var port = -1;

        if (Server is not null)
        {
            port = await Server.StartAsync(cancellationToken).ConfigureAwait(false);

            // The server can be pushed off its preferred port; the catalog builds every URL from its
            // own copy of the port, so it has to be told which one actually bound.
            Catalog.Port = port;
            Catalog.FallbackAddress = Catalog.ResolveHost();
        }

        if (scanForDevices)
            await Discovery.StartAsync(cancellationToken).ConfigureAwait(false);

        return port;
    }

    /// <summary>Turns a library row into something the pipeline can publish.</summary>
    public static MediaItem ToMediaItem(TrackRecord track) => new()
    {
        Path = track.Path,
        Id = track.Id,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        AlbumArtist = track.AlbumArtist,
        Duration = track.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        ArtId = track.ArtId,
        IsLive = false,
    };

    public async ValueTask DisposeAsync()
    {
        await Devices.DisposeAsync().ConfigureAwait(false);
        await Discovery.DisposeAsync().ConfigureAwait(false);

        if (Media is not null)
            await Media.DisposeAsync().ConfigureAwait(false);
    }
}
