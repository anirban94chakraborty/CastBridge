using System.Text;
using Microsoft.Extensions.Logging;
using CastBridge.Media.Server;

namespace CastBridge.Media.Library;

public sealed record ScanProgress(int FilesSeen, int FilesIndexed, int FilesSkipped, int Errors, string? CurrentFile)
{
    public string Describe() =>
        $"{FilesIndexed:N0} indexed, {FilesSkipped:N0} unchanged, {Errors:N0} unreadable" +
        (CurrentFile is null ? string.Empty : $" — {System.IO.Path.GetFileName(CurrentFile)}");
}

public sealed record ScanResult(int TotalFiles, int Indexed, int Skipped, int Removed, int Errors, TimeSpan Duration)
{
    public string Describe() =>
        $"{TotalFiles:N0} files: {Indexed:N0} indexed, {Skipped:N0} unchanged, {Removed:N0} removed, {Errors:N0} unreadable " +
        $"in {Duration.TotalSeconds:F1}s";
}

/// <summary>
/// Walks the music folders, reads tags with TagLib# and keeps the SQLite index in step. Unchanged
/// files (same size and timestamp) are skipped, so rescanning a large library is cheap.
/// </summary>
public sealed class LibraryScanner
{
    private static bool _encodingRegistered;

    private readonly LibraryIndex _index;
    private readonly MediaCatalog _catalog;
    private readonly ILogger<LibraryScanner>? _logger;

    public LibraryScanner(LibraryIndex index, MediaCatalog catalog, ILoggerFactory? loggerFactory = null)
    {
        _index = index;
        _catalog = catalog;
        _logger = loggerFactory?.CreateLogger<LibraryScanner>();
    }

    public async Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        EnsureEncodings();
        var started = DateTimeOffset.UtcNow;
        await _index.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var folders = await _index.GetFoldersAsync(cancellationToken).ConfigureAwait(false);
        var existing = force
            ? new Dictionary<string, TrackRecord>(StringComparer.OrdinalIgnoreCase)
            : (IReadOnlyDictionary<string, TrackRecord>)await _index.GetSignaturesAsync(cancellationToken).ConfigureAwait(false);

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<TrackRecord>(256);
        var indexed = 0;
        var skipped = 0;
        var errors = 0;
        var total = 0;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (var file in EnumerateAudioFiles(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Cannot stat {File}", file);
                    errors++;
                    continue;
                }

                seenPaths.Add(info.FullName);

                if (existing.TryGetValue(info.FullName, out var known) &&
                    known.FileSize == info.Length &&
                    known.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                {
                    skipped++;
                    if (total % 25 == 0)
                        progress?.Report(new ScanProgress(total, indexed, skipped, errors, info.FullName));
                    continue;
                }

                try
                {
                    batch.Add(BuildTrack(info));
                    indexed++;

                    if (batch.Count >= 200)
                    {
                        await _index.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Skipping unreadable file {File}", file);
                    errors++;
                }

                if (total % 25 == 0)
                    progress?.Report(new ScanProgress(total, indexed, skipped, errors, info.FullName));
            }
        }

        if (batch.Count > 0)
            await _index.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);

        var missing = existing.Keys
            .Where(p => !seenPaths.Contains(p))
            .Where(p => folders.Any(f => p.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (missing.Length > 0)
            await _index.DeleteAsync(missing, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ScanProgress(total, indexed, skipped, errors, null));

        var result = new ScanResult(total, indexed, skipped, missing.Length, errors, DateTimeOffset.UtcNow - started);
        _logger?.LogInformation("Library scan finished: {Result}", result.Describe());
        return result;
    }

    private static IEnumerable<string> EnumerateAudioFiles(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                MaxRecursionDepth = 12,
            });
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (AudioFormats.IsSupportedFile(file))
                yield return file;
        }
    }

    private TrackRecord BuildTrack(FileInfo info)
    {
        var format = AudioFormats.Resolve(info.FullName);

        string? title = null, artist = null, album = null, albumArtist = null;
        int? track = null, disc = null, year = null;
        long? durationMs = null;
        string? artId = null;
        var codec = format.Codec;

        try
        {
            using var file = TagLib.File.Create(info.FullName);
            var tag = file.Tag;

            title = NullIfEmpty(tag.Title);
            artist = NullIfEmpty(tag.FirstPerformer) ?? NullIfEmpty(tag.FirstComposer);
            album = NullIfEmpty(tag.Album);
            albumArtist = NullIfEmpty(tag.FirstAlbumArtist) ?? artist;
            track = tag.Track > 0 ? (int)tag.Track : null;
            disc = tag.Disc > 0 ? (int)tag.Disc : null;
            year = tag.Year > 0 ? (int)tag.Year : null;

            if (file.Properties.Duration > TimeSpan.Zero)
                durationMs = (long)file.Properties.Duration.TotalMilliseconds;

            var codecDescription = file.Properties.Codecs?
                .Select(c => c?.Description)
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

            if (!format.IsNative && !string.IsNullOrWhiteSpace(codecDescription))
                codec = codecDescription;

            // Additional safety net: some m4a files only reveal ALAC through TagLib.
            if (format.IsNative &&
                codecDescription?.Contains("lossless", StringComparison.OrdinalIgnoreCase) == true &&
                info.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
            {
                format = new AudioFormatInfo
                {
                    ContentType = format.ContentType,
                    IsNative = false,
                    Reason = "Apple Lossless is not decodable by Cast devices; it will be converted to FLAC.",
                    Codec = "ALAC",
                };
                codec = "ALAC";
            }

            var picture = tag.Pictures?.FirstOrDefault();
            var pictureBytes = picture?.Data?.Data;
            if (pictureBytes is { Length: > 0 })
            {
                var mime = string.IsNullOrWhiteSpace(picture!.MimeType) ? "image/jpeg" : picture.MimeType;
                artId = _catalog.RegisterArt(info.FullName, pictureBytes, mime);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "TagLib could not read {File}", info.FullName);
        }

        return new TrackRecord
        {
            Id = info.FullName,
            Path = info.FullName,
            Title = title ?? System.IO.Path.GetFileNameWithoutExtension(info.FullName),
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            TrackNumber = track,
            DiscNumber = disc,
            Year = year,
            DurationMs = durationMs,
            Codec = codec,
            ContentType = format.ContentType,
            RequiresTranscode = !format.IsNative,
            ArtId = artId,
            FileSize = info.Length,
            ModifiedTicks = info.LastWriteTimeUtc.Ticks,
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>ID3v2 files from the 90s use code pages that .NET Core does not ship by default.</summary>
    private static void EnsureEncodings()
    {
        if (_encodingRegistered)
            return;

        _encodingRegistered = true;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Not fatal: only affects exotic tags.
        }
    }
}
