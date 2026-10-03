using CastBridge.Core.Cast;
using CastBridge.Media;
using CastBridge.Media.Library;
using CastBridge.Media.Live;
using Microsoft.Extensions.Logging;

namespace CastBridge.Cli;

internal sealed partial class Cli
{
    private async Task<int> PlayAsync()
    {
        if (_options.Args.Length < 2)
        {
            Console.WriteLine("usage: play <device> <file|url|live>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        if (device is null)
            return 1;

        // Check this before doing any work: a device that will not run the media receiver would
        // otherwise fail deep in the protocol with "Application with id CC1AD845 could not be started".
        var support = DevicePlaybackSupport.FromDevice(device);
        if (!support.CanCastAnything && !_options.Force)
        {
            Console.Error.WriteLine($"error: {device.Name} cannot play audio from this PC.");
            Console.Error.WriteLine($"  {support.Reason}");
            return 1;
        }

        var target = _options.Args[1];

        // A remote URL is handed to the speaker untouched: the receiver fetches it itself, so there is
        // no reason to proxy it through this PC.
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return await PlayUrlAsync(device, uri);
        }

        if (target.Equals("live", StringComparison.OrdinalIgnoreCase))
            return await CastLiveAsync(device);

        var file = new FileInfo(target);
        if (!file.Exists)
        {
            Console.Error.WriteLine($"error: no such file '{target}'");
            return 1;
        }

        var item = await ToMediaItemAsync(file);
        var format = AudioFormats.Resolve(file.FullName);
        if (!format.IsNative)
            Console.WriteLine($"{file.Name}: {format.Reason} Converting to FLAC first, this takes a few seconds...");

        _media.TranscodeProgressChanged += OnTranscodeProgress;
        try
        {
            await _devices.CastQueueAsync(device.Id, new[] { item });
        }
        finally
        {
            _media.TranscodeProgressChanged -= OnTranscodeProgress;
        }

        Console.WriteLine($"{device.Name}: playing {item.Title}");
        Console.WriteLine($"  served from {catalogUrl} by CastBridge on this PC");

        // The speaker streams from this process, so it has to keep running for playback to continue.
        return await HoldAndReportAsync(device, TimeSpan.FromSeconds(_options.Hold));
    }

    /// <summary>Keeps the media server alive and prints what the speaker is actually doing.</summary>
    private async Task<int> HoldAndReportAsync(CastDevice device, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return 0;

        var deadline = DateTimeOffset.UtcNow + duration;
        Console.WriteLine($"  holding the media server for {duration.TotalSeconds:F0}s; Ctrl+C to stop early");

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            try
            {
                await _devices.RefreshAsync(device.Id);
                var state = _devices.GetState(device.Id);
                if (state is null)
                    continue;

                Console.WriteLine(
                    $"\r  {_server.BytesServed,9:N0} bytes served | {state.Media.PlayerState} | " +
                    $"{state.Media.Position:m\\:ss} / {state.Media.Duration?.ToString(@"m\:ss") ?? "?"}" +
                    (state.Media.Title is null ? string.Empty : $" | {state.Media.Title}"));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Refresh failed while holding");
            }
        }

        Console.WriteLine();
        return 0;
    }

    private async Task<int> PlayUrlAsync(CastDevice device, Uri uri)
    {
        var client = await _devices.GetClientAsync(device.Id);
        await client.LoadAsync(new CastMediaRequest
        {
            ContentUrl = uri.ToString(),
            ContentType = ContentTypeFor(uri),
            Title = uri.Host,
            Artist = uri.Host,
            // Casters play an unknown-length URL as a live stream; a fixed duration would stall it.
            IsLive = true,
        });

        Console.WriteLine($"{device.Name}: playing {uri}");
        return 0;
    }

    private async Task<int> CastLiveAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: cast-live <device>");
            return 1;
        }

        var device = Resolve(_options.Args[0]);
        return device is null ? 1 : await CastLiveAsync(device);
    }

    private async Task<int> CastLiveAsync(CastDevice device)
    {
        if (_ffmpeg.Resolve(_options.Ffmpeg) is null)
        {
            Console.Error.WriteLine("error: FFmpeg is required for live capture. Run 'ffmpeg --download' first.");
            return 1;
        }

        Console.WriteLine($"Starting system audio capture for {device.Name}...");
        await _devices.CastQueueAsync(device.Id, new[]
        {
            new MediaItem { Path = LiveCaptureService.DefaultSessionId, Title = "This PC's audio", IsLive = true },
        });

        Console.WriteLine($"{device.Name}: streaming this PC's audio (a second or two of latency is normal).");
        Console.WriteLine("  Press Ctrl+C to stop.");
        await WaitForCancellationAsync();
        _live.Stop();
        return 0;
    }

    private async Task<int> LiveAsync()
    {
        // "live <device>" starts capture and streams to that device; bare "live" is a diagnostic toggle.
        if (_options.Args.Length > 0)
            return await CastLiveAsync();

        if (_live.IsRunning)
        {
            _live.Stop();
            Console.WriteLine("Live capture stopped.");
            return 0;
        }

        var started = await _live.StartAsync();
        if (!started)
        {
            Console.Error.WriteLine($"error: {_live.LastError}");
            return 1;
        }

        Console.WriteLine($"Capturing '{_live.CaptureDeviceName}' at {(int)_live.Quality} kbps.");
        Console.WriteLine("Nothing is sent anywhere until a speaker is told to play it. Press Ctrl+C to stop.");
        await WaitForCancellationAsync();
        _live.Stop();
        return 0;
    }

    private async Task<int> ScanAsync()
    {
        if (_options.Args.Length == 0)
        {
            Console.WriteLine("usage: scan <folder> [folder...]");
            return 1;
        }

        var added = new List<string>();
        foreach (var folder in _options.Args)
        {
            if (!Directory.Exists(folder))
            {
                Console.Error.WriteLine($"error: not a folder '{folder}'");
                continue;
            }

            var full = Path.GetFullPath(folder);
            await _library.AddFolderAsync(full);
            added.Add(full);
        }

        if (added.Count == 0)
            return 1;

        Console.WriteLine($"Scanning {added.Count} folder(s)...");
        var progress = new Progress<ScanProgress>(p => Console.Write($"\r  {p.Describe()}          "));

        _media.TranscodeProgressChanged += OnTranscodeProgress;
        var result = await _scanner.ScanAsync(progress);
        Console.WriteLine($"\r  {result.Describe()}".PadRight(60));
        Console.WriteLine($"Library holds {await _library.CountAsync()} track(s).");
        return 0;
    }

    private async Task<int> LibraryAsync()
    {
        var search = _options.Args.FirstOrDefault();
        var tracks = await _library.QueryAsync(search, limit: 100);
        var total = await _library.CountAsync();

        if (tracks.Count == 0)
        {
            Console.WriteLine(search is null
                ? "The library is empty. Add a folder with: scan <folder>"
                : $"Nothing matched '{search}'.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{"#",-4} {"Title",-38} {"Artist",-24} {"Album",-22} {"Length",-7} Format");
        Console.WriteLine(new string('-', 116));

        var row = 1;
        foreach (var track in tracks)
        {
            Console.WriteLine(
                $"{row++,-4} {Trim(track.DisplayTitle, 38),-38} {Trim(track.DisplayArtist, 24),-24} " +
                $"{Trim(track.Album, 22),-22} {track.DisplayDuration,-7} {track.FormatBadge}");
        }

        Console.WriteLine();
        Console.WriteLine($"Showing {tracks.Count} of {total} track(s).");
        return 0;
    }

    private async Task<int> FfmpegAsync()
    {
        if (_options.Args.Contains("--download"))
        {
            if (_ffmpeg.Resolve() is not null)
            {
                Console.WriteLine("FFmpeg is already available:");
                Console.WriteLine(_ffmpeg.Describe());
                return 0;
            }

            Console.WriteLine("Downloading FFmpeg (about 80 MB, once)...");
            var progress = new Progress<double>(p => Console.Write($"\r  {p:P0}"));

            var path = await _ffmpeg.DownloadAsync(progress);
            Console.WriteLine();
            if (path is null)
            {
                Console.Error.WriteLine("error: the download failed. Install FFmpeg manually and pass --ffmpeg=PATH.");
                return 1;
            }
        }

        var resolved = _ffmpeg.Resolve(_options.Ffmpeg);
        if (resolved is null)
        {
            Console.WriteLine("FFmpeg is not installed.");
            Console.WriteLine("It is only needed to convert formats Cast cannot play (ALAC, WMA, AIFF) and for live capture.");
            Console.WriteLine("Run 'ffmpeg --download' to fetch it, or pass --ffmpeg=PATH to use your own build.");
            return 1;
        }

        Console.WriteLine(_ffmpeg.Describe());
        Console.WriteLine($"Transcode cache: {_transcode.CacheDirectory}");
        return 0;
    }

    /// <summary>
    /// Starts the media server and keeps it alive. On its own this proves whether a speaker can
    /// reach this PC: if the file downloads here but not there, the firewall or the network is at fault.
    /// </summary>
    private async Task<int> ServeAsync()
    {
        var baseUrl = $"http://{_catalog.ResolveHost()}:{_server.Port}";
        Console.WriteLine($"Media server running at {baseUrl}");
        Console.WriteLine("Press Ctrl+C to stop.");

        var file = _options.Args.FirstOrDefault();
        if (file is not null && File.Exists(file))
        {
            var format = AudioFormats.Resolve(file);
            var id = _catalog.RegisterFile(Path.GetFullPath(file), format.ContentType);
            Console.WriteLine($"  {Path.GetFileName(file)}: {_catalog.BuildUrl(id)}");
        }

        await WaitForCancellationAsync();
        return 0;
    }

    private string catalogUrl => $"http://{_catalog.ResolveHost()}:{_server.Port}";

    /// <summary>Builds a media item, pulling tags out of the library when the track is already indexed.</summary>
    private async Task<MediaItem> ToMediaItemAsync(FileInfo file)
    {
        var indexed = await _library.GetTracksByPathAsync(new[] { file.FullName });
        if (indexed.Count == 0)
        {
            return new MediaItem
            {
                Path = file.FullName,
                Title = Path.GetFileNameWithoutExtension(file.Name),
            };
        }

        var track = indexed[0];
        return new MediaItem
        {
            Path = track.Path,
            Id = track.Id,
            Title = track.DisplayTitle,
            Artist = track.Artist,
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            Duration = track.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            ArtId = track.ArtId,
        };
    }

    private void OnTranscodeProgress(object? sender, TranscodeProgress progress)
    {
        if (progress.Error is not null)
            Console.Error.WriteLine($"error: {progress.Error}");
        else
            Console.Write($"\r  converting {Path.GetFileName(progress.Path)}: {progress.Percent:P0}   ");
    }

    private static string ContentTypeFor(Uri uri) =>
        uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/x-mpegURL" : "audio/mpeg";

    private static async Task WaitForCancellationAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is the normal way out of a long running capture.
        }
    }
}
