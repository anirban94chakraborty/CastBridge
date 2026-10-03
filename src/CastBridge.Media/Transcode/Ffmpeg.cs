using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using CastBridge.Core.Discovery;

namespace CastBridge.Media.Transcode;

/// <summary>
/// Finds or fetches ffmpeg. The app never bundles it: the binary is large and its licence (LGPL/GPL
/// depending on the build) is the user's to accept, so it is downloaded on request into the user's
/// own profile and can be pointed at an existing install instead.
/// </summary>
public sealed class FfmpegLocator
{
    /// <summary>Official Windows "essentials" build, the smallest one that carries the codecs we need.</summary>
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private readonly ILogger<FfmpegLocator>? _logger;

    public FfmpegLocator(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<FfmpegLocator>();
    }

    public string? ResolvedPath { get; private set; }

    public string? Version { get; private set; }

    /// <summary>Looks for ffmpeg next to the app, in the user profile, then on PATH.</summary>
    public string? Resolve(string? configuredPath = null)
    {
        if (ResolvedPath is not null && File.Exists(ResolvedPath))
            return ResolvedPath;

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(configuredPath);

        if (Environment.GetEnvironmentVariable("CASTBRIDGE_FFMPEG") is { Length: > 0 } fromEnvironment)
            candidates.Add(fromEnvironment);

        candidates.Add(Path.Combine(AppPaths.FfmpegDirectory, "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
        candidates.Add("ffmpeg.exe");

        foreach (var candidate in candidates)
        {
            var resolved = ResolveCandidate(candidate);
            if (resolved is null)
                continue;

            ResolvedPath = resolved;
            Version = ReadVersion(resolved);
            _logger?.LogInformation("Using ffmpeg at {Path} ({Version})", resolved, Version);
            return resolved;
        }

        return null;
    }

    public bool IsAvailable(string? configuredPath = null) => Resolve(configuredPath) is not null;

    private static string? ResolveCandidate(string candidate)
    {
        try
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            if (!candidate.Contains(Path.DirectorySeparatorChar) && !candidate.Contains('/'))
            {
                // Bare name: let Windows search PATH.
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full))
                        return full;
                }
            }
        }
        catch
        {
            // Ignore malformed paths.
        }

        return null;
    }

    public string? ReadVersion(string path)
    {
        try
        {
            using var process = Start(path, "-version");
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not read the ffmpeg version");
            return null;
        }
    }

    /// <summary>Downloads and unpacks ffmpeg into the user's profile. Returns the executable path.</summary>
    public async Task<string?> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(AppPaths.FfmpegDirectory, "ffmpeg.exe");
        var archive = Path.Combine(Path.GetTempPath(), $"castbridge-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(AppPaths.FfmpegDirectory);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(archive))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;

                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)copied / total));
                }
            }

            progress?.Report(0.95);

            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    _logger?.LogWarning("The downloaded archive did not contain bin/ffmpeg.exe");
                    return null;
                }

                entry.ExtractToFile(target, overwrite: true);
            }

            var version = ReadVersion(target);
            if (version is null)
            {
                _logger?.LogWarning("The downloaded ffmpeg could not be executed");
                return null;
            }

            ResolvedPath = target;
            Version = version;
            progress?.Report(1.0);
            _logger?.LogInformation("Downloaded ffmpeg: {Version}", version);
            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Downloading ffmpeg failed");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(archive))
                    File.Delete(archive);
            }
            catch
            {
                // Temporary file; the OS will clean it up eventually.
            }
        }
    }

    internal static Process Start(string fileName, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}");
    }

    public string Describe() =>
        ResolvedPath is null
            ? "ffmpeg was not found. Non-native formats cannot be converted without it."
            : $"{Version ?? "ffmpeg"}\n{ResolvedPath}";
}

public sealed record TranscodeResult(bool Success, string? Path, string? Error)
{
    public static TranscodeResult Ok(string path) => new(true, path, null);

    public static TranscodeResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Converts files Cast cannot decode into FLAC in a local cache, then serves the cached file.
///
/// Pre-transcoding (instead of streaming a live transcode) is deliberate: the receiver gets a normal
/// file with a known length, so seeking, resume and the queue all keep working. Encoding a 4 minute
/// track takes a moment, and the result is reused on every later play.
/// </summary>
public sealed class TranscodeCache
{
    private readonly FfmpegLocator _locator;
    private readonly ILogger<TranscodeCache>? _logger;
    private readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _concurrency;

    public TranscodeCache(
        FfmpegLocator locator,
        string? cacheDirectory = null,
        long maxCacheBytes = 3L * 1024 * 1024 * 1024,
        int maxParallelJobs = 2,
        ILoggerFactory? loggerFactory = null)
    {
        _locator = locator;
        CacheDirectory = cacheDirectory ?? AppPaths.TranscodeDirectory;
        MaxCacheBytes = maxCacheBytes;
        _concurrency = new SemaphoreSlim(Math.Max(1, maxParallelJobs));
        _logger = loggerFactory?.CreateLogger<TranscodeCache>();
        Directory.CreateDirectory(CacheDirectory);
    }

    public string CacheDirectory { get; }

    public long MaxCacheBytes { get; }

    public async Task<TranscodeResult> EnsureFlacAsync(
        string sourcePath,
        TimeSpan? duration,
        string? configuredFfmpegPath = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            return TranscodeResult.Failed($"File not found: {sourcePath}");

        var ffmpeg = _locator.Resolve(configuredFfmpegPath);
        if (ffmpeg is null)
            return TranscodeResult.Failed("ffmpeg is required to convert this format. Install it from Settings.");

        var target = GetCachePath(sourcePath);
        if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(sourcePath))
        {
            _logger?.LogDebug("Transcode cache hit for {File}", Path.GetFileName(sourcePath));
            return TranscodeResult.Ok(target);
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = target + ".part";
            var arguments = $"-hide_banner -nostdin -loglevel error -y -i \"{sourcePath}\" -vn -map_metadata -1 " +
                            $"-c:a flac -compression_level 5 -progress pipe:1 -nostats -f flac \"{temporary}\"";

            using var process = FfmpegLocator.Start(ffmpeg, arguments);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var totalSeconds = duration?.TotalSeconds ?? 0;
            var readTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (progress is null || totalSeconds <= 0)
                        continue;

                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        double.TryParse(line["out_time_us=".Length..], out var microseconds) && microseconds > 0)
                    {
                        progress.Report(Math.Min(0.99, microseconds / 1_000_000.0 / totalSeconds));
                    }
                }
            }, cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await readTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(temporary))
            {
                TryDelete(temporary);
                var message = string.IsNullOrWhiteSpace(error) ? $"ffmpeg exited with code {process.ExitCode}" : error.Trim();
                _logger?.LogWarning("Transcoding {File} failed: {Error}", Path.GetFileName(sourcePath), message);
                return TranscodeResult.Failed(message);
            }

            File.Move(temporary, target, overwrite: true);
            progress?.Report(1.0);
            _logger?.LogInformation("Transcoded {File} to FLAC ({Size:N0} bytes)", Path.GetFileName(sourcePath), new FileInfo(target).Length);

            await EnforceLimitAsync().ConfigureAwait(false);
            return TranscodeResult.Ok(target);
        }
        catch (OperationCanceledException)
        {
            return TranscodeResult.Failed("Conversion was cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Transcoding {File} failed", sourcePath);
            return TranscodeResult.Failed(ex.Message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public string GetCachePath(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash + ".flac");
    }

    /// <summary>Marks a cache file as in use so eviction never deletes something being streamed.</summary>
    public void MarkInUse(string path)
    {
        lock (_sync)
            _inUse.Add(path);
    }

    public void ReleaseInUse(string path)
    {
        lock (_sync)
            _inUse.Remove(path);
    }

    /// <summary>Deletes the oldest cache entries until the cache is back under its size limit.</summary>
    public Task<int> EnforceLimitAsync()
    {
        var removed = 0;
        try
        {
            var files = new DirectoryInfo(CacheDirectory)
                .GetFiles("*.flac")
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToArray();

            var total = files.Sum(f => f.Length);
            foreach (var file in files.Reverse())
            {
                if (total <= MaxCacheBytes)
                    break;

                lock (_sync)
                {
                    if (_inUse.Contains(file.FullName))
                        continue;
                }

                try
                {
                    total -= file.Length;
                    file.Delete();
                    removed++;
                }
                catch
                {
                    // Locked by a running read; try again next time.
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Cache cleanup failed");
        }

        return Task.FromResult(removed);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
