namespace CastBridge.Media;

public sealed record AudioFormatInfo
{
    public required string ContentType { get; init; }

    /// <summary>True when a Cast device can decode this file as-is.</summary>
    public required bool IsNative { get; init; }

    /// <summary>Why transcoding is (or is not) required; shown in the library UI.</summary>
    public required string Reason { get; init; }

    public string? Codec { get; init; }
}

/// <summary>
/// What Cast devices actually decode: FLAC (up to 96kHz/24-bit), HE-AAC, LC-AAC, MP3, Opus,
/// Vorbis and WAV/LPCM. Everything else (ALAC, WMA, AIFF, APE, DSF) has to be transcoded first.
/// </summary>
public static class AudioFormats
{
    /// <summary>Extensions the library will index, native or not.</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".adts", ".wav", ".ogg", ".oga", ".opus", ".webm",
        ".wma", ".aif", ".aiff", ".ape", ".dsf", ".dff", ".mka",
    };

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static AudioFormatInfo Resolve(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        switch (ext)
        {
            case ".mp3":
                return Native("audio/mpeg", "MP3", "MP3");
            case ".flac":
                return Native("audio/flac", "FLAC", "FLAC");
            case ".wav":
                return Native("audio/wav", "WAV (uncompressed)", "LPCM");
            case ".ogg":
            case ".oga":
                return Native("audio/ogg", "Ogg Vorbis", "Vorbis");
            case ".opus":
                return Native("audio/ogg", "Opus", "Opus");
            case ".webm":
                return Native("audio/webm", "WebM audio", "Vorbis");
            case ".aac":
            case ".adts":
                return Native("audio/aac", "AAC", "AAC");
            case ".m4a":
            case ".mp4":
                // AAC in an MP4 container is supported; ALAC in the same container is not, and only a
                // container peek can tell them apart.
                return ContainsAlac(path)
                    ? Transcode("audio/mp4", "Apple Lossless", "ALAC")
                    : Native("audio/mp4", "AAC (MP4)", "AAC");

            case ".wma":
                return Transcode("audio/x-ms-wma", "Windows Media Audio", "WMA");
            case ".aif":
            case ".aiff":
                return Transcode("audio/aiff", "AIFF", "PCM (AIFF)");
            case ".ape":
                return Transcode("audio/ape", "Monkey's Audio", "APE");
            case ".dsf":
            case ".dff":
                return Transcode("audio/x-dsf", "DSD", "DSD");
            case ".mka":
                return Transcode("audio/x-matroska", "Matroska audio", null);
            default:
                return Transcode("application/octet-stream", "Unknown format", null);
        }
    }

    private static AudioFormatInfo Native(string contentType, string description, string codec) =>
        new() { ContentType = contentType, IsNative = true, Reason = $"Cast decodes {description} directly.", Codec = codec };

    private static AudioFormatInfo Transcode(string contentType, string description, string? codec) =>
        new()
        {
            ContentType = contentType,
            IsNative = false,
            Reason = $"Cast devices cannot decode {description}; it will be converted to FLAC.",
            Codec = codec,
        };

    /// <summary>
    /// Detects Apple Lossless inside an MP4/M4A container by looking for the "alac" box near the
    /// start of the file. Cheap, allocation-free, and good enough to decide native versus transcode.
    /// </summary>
    public static bool ContainsAlac(string path, int peekBytes = 256 * 1024)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(peekBytes, stream.Length);
            if (length <= 0)
                return false;

            var buffer = new byte[length];
            var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
            var span = buffer.AsSpan(0, read);

            for (var i = 0; i + 4 <= span.Length; i++)
            {
                if (span[i] == (byte)'a' && span[i + 1] == (byte)'l' && span[i + 2] == (byte)'a' && span[i + 3] == (byte)'c')
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Picks a sensible transcoding target: FLAC for lossless sources, otherwise FLAC too (small LAN, no loss).</summary>
    public static string TranscodeTargetExtension => ".flac";
}
