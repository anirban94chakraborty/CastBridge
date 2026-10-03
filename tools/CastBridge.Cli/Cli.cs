using CastBridge.Core;
using CastBridge.Core.Cast;
using CastBridge.Core.Discovery;
using CastBridge.Media;
using CastBridge.Media.Library;
using CastBridge.Media.Live;
using CastBridge.Media.Server;
using CastBridge.Media.Transcode;
using Microsoft.Extensions.Logging;

namespace CastBridge.Cli;

/// <summary>
/// The terminal front end. It builds the same services the desktop app builds, which is what makes
/// the CLI useful: anything reproducible here is reproducible in the app, and vice versa.
/// </summary>
internal sealed partial class Cli : IAsyncDisposable
{
    private readonly CliOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Cli> _logger;

    private CastDiscoveryService _discovery = null!;
    private FfmpegLocator _ffmpeg = null!;
    private MediaCatalog _catalog = null!;
    private MediaServer _server = null!;
    private LibraryIndex _library = null!;
    private TranscodeCache _transcode = null!;
    private LiveCaptureService _live = null!;
    private MediaPipeline _media = null!;
    private LibraryScanner _scanner = null!;
    private CastDeviceManager _devices = null!;
    private CastBridge.Media.Compatibility.CompatibilityProbe _probe = null!;

    private Cli(CliOptions options)
    {
        _options = options;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
            builder.AddSimpleConsole(c =>
            {
                c.SingleLine = true;
                c.TimestampFormat = "HH:mm:ss ";
            });
        });
        _logger = _loggerFactory.CreateLogger<Cli>();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        await using var cli = new Cli(options);
        return await cli.ExecuteAsync();
    }

    private async Task<int> ExecuteAsync()
    {
        try
        {
            if (_options.Command is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            Compose();
            await _discovery.StartAsync();
            await _server.StartAsync();

            // The server may have been pushed to a different port, and the catalog builds every URL,
            // so it has to learn the real one or the speaker is handed a dead address.
            _catalog.Port = _server.Port;

            return _options.Command switch
            {
                "devices" or "list" => await ListDevicesAsync(),
                "volume" => await VolumeAsync(),
                "status" => await StatusAsync(),
                "groups" => await GroupsAsync(),
                "stop" => await StopAsync(),
                "transport" => await TransportAsync(),
                "watch" => await WatchAsync(),
                "diag" => DiagnosticsAsync(),
                "check" => await CheckAsync(),
                "receivers" => await ReceiversAsync(),
                "launch" => await LaunchAsync(),
                "play" or "cast" => await PlayAsync(),
                "scan" => await ScanAsync(),
                "library" => await LibraryAsync(),
                "ffmpeg" => await FfmpegAsync(),
                "live" => await LiveAsync(),
                "cast-live" => await CastLiveAsync(),
                "serve" => await ServeAsync(),
                _ => UnknownCommand(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (_options.Verbose)
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>Wires the object graph. Nothing here touches the network; the callers do.</summary>
    private void Compose()
    {
        var providers = new List<ICastDiscovery>();
        if (!_options.NoMdns)
            providers.Add(new MdnsCastDiscovery(_loggerFactory));
        if (!_options.NoSubnet)
            providers.Add(new SubnetProbeDiscovery(_loggerFactory));

        _discovery = new CastDiscoveryService(providers, loggerFactory: _loggerFactory)
        {
            ScanTimeout = TimeSpan.FromSeconds(_options.ScanSeconds),
        };

        _ffmpeg = new FfmpegLocator(_loggerFactory);
        _catalog = new MediaCatalog();
        _live = new LiveCaptureService(_ffmpeg, _loggerFactory)
        {
            FfmpegPath = _options.Ffmpeg,
            CaptureDeviceId = _options.LiveDevice,
        };

        if (_options.LiveQuality is { } quality && Enum.TryParse<LiveQuality>(quality, true, out var parsed))
            _live.Quality = parsed;

        _server = new MediaServer(
            _catalog,
            _live,
            new MediaServerOptions { PreferredPort = _options.MediaPort },
            _loggerFactory);

        _library = new LibraryIndex();
        _transcode = new TranscodeCache(_ffmpeg, loggerFactory: _loggerFactory);
        _media = new MediaPipeline(_catalog, _server, _library, _transcode, _live, _ffmpeg, _loggerFactory)
        {
            FfmpegPath = _options.Ffmpeg,
        };
        _scanner = new LibraryScanner(_library, _catalog, _loggerFactory);
        _devices = new CastDeviceManager(_discovery, _media, _loggerFactory);
        _probe = new CastBridge.Media.Compatibility.CompatibilityProbe(_devices, _media, _server, _loggerFactory);
    }

    private int UnknownCommand()
    {
        Console.Error.WriteLine($"unknown command '{_options.Command}'");
        PrintHelp();
        return 1;
    }

    /// <summary>Resolves a device from a name fragment, an IP address, an id, or a 1-based row number.</summary>
    private CastDevice? Resolve(string token)
    {
        var known = _devices.Devices;
        if (known.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return null;
        }

        if (int.TryParse(token, out var row) && row >= 1 && row <= known.Count)
            return known[row - 1];

        var matches = known
            .Where(d => d.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                        d.IpAddress.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                        d.Id.Equals(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => Null($"No device matched '{token}'. Found: {string.Join(", ", known.Select(d => d.Name))}"),
            1 => matches[0],
            _ => Null($"'{token}' is ambiguous: {string.Join(", ", matches.Select(d => d.Name))}"),
        };

        static CastDevice? Null(string message)
        {
            Console.WriteLine(message);
            return null;
        }
    }

    private async Task<DeviceRuntimeState?> ReadStateAsync(string deviceId)
    {
        try
        {
            await _devices.GetClientAsync(deviceId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not connect: {ex.Message}");
            return null;
        }

        await _devices.RefreshAsync(deviceId);
        return _devices.GetState(deviceId);
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        if (_devices is not null)
            await _devices.DisposeAsync();
        if (_discovery is not null)
            await _discovery.DisposeAsync();
        if (_media is not null)
            await _media.DisposeAsync();
        _loggerFactory.Dispose();
    }

    private void PrintHelp()
    {
        Console.WriteLine("""
            CastBridge CLI - control Cast speakers from a Windows PC

              devices [--mdns|--subnet]           list Cast devices on the LAN
              volume <device> [0-100] [--mute|--unmute]
                                                read or set volume
              status <device>                    connection, volume and now playing
              groups <device>                    list multi-room members of a group
              play <device> <file|url|live>      cast a local file, a web URL, or system audio
              stop <device>                      stop playback and return the device to idle
              transport <device> <play|pause|stop|next|prev|toggle>
                                                drive playback in a session that is already casting
              watch [device] [seconds]            follow volume and now playing, as the app does
              live [device]                      start or stop capturing this PC's audio
              scan <folder> [folder...]           index a music folder
              library [search]                   list the indexed library
              ffmpeg [--download]                locate, report, or fetch FFmpeg
              serve [file]                        run the media server and print its URLs
              diag                               network and firewall diagnostics
              check [device] [--force]           test whether a speaker can play audio from this PC
              receivers [device]                  find which receiver apps a speaker will launch
              launch <device> <app-id>            start one receiver app by id

            Global options:
              --port=N            media server port (default 45455)
              --scan-timeout=N    seconds to wait for discovery (default 6)
              --ffmpeg=PATH       use this FFmpeg executable
              --live-device=ID    capture a specific output device
              --live-quality=Q    Mp3_192, Mp3_256 or Mp3_320
              --no-mdns / --no-subnet
                                disable one discovery method
              --verbose           protocol level logging

            <device> accepts a name fragment, an IP address, or the row number from 'devices'.
            """);
    }
}
