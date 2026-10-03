namespace CastBridge.Media.Server;

public readonly record struct ByteRange(long Start, long Length)
{
    public long End => Start + Length - 1;

    public string ToContentRange(long totalLength) => $"bytes {Start}-{End}/{totalLength}";
}

public enum RangeParseResult
{
    /// <summary>No Range header, or a form we do not honour: serve the whole file with 200.</summary>
    WholeFile,

    /// <summary>A valid, satisfiable range: serve 206 with Content-Range.</summary>
    Partial,

    /// <summary>Well-formed but not satisfiable: answer 416 with "bytes */length".</summary>
    Unsatisfiable,
}

/// <summary>
/// Cast devices are picky about byte ranges: they use them to seek and to resume after a buffering
/// stall, and a server that ignores them makes the seek bar dead. Only the first range of a
/// multi-range request is honoured (which is what every media player actually uses).
/// </summary>
public static class RangeHeaderParser
{
    public static RangeParseResult TryParse(string? header, long totalLength, out ByteRange range, out string? reason)
    {
        range = default;
        reason = null;

        if (string.IsNullOrWhiteSpace(header))
            return RangeParseResult.WholeFile;

        var value = header.Trim();
        if (!value.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Only byte ranges are supported.";
            return RangeParseResult.WholeFile;
        }

        var equalsIndex = value.IndexOf('=');
        if (equalsIndex < 0)
        {
            reason = "Malformed Range header.";
            return RangeParseResult.WholeFile;
        }

        var spec = value[(equalsIndex + 1)..].Trim();

        // Multi-range requests are legal but pointless for audio; take the first part.
        var commaIndex = spec.IndexOf(',');
        if (commaIndex >= 0)
            spec = spec[..commaIndex].Trim();

        var dashIndex = spec.IndexOf('-');
        if (dashIndex < 0)
        {
            reason = "Malformed range specification.";
            return RangeParseResult.WholeFile;
        }

        var startText = spec[..dashIndex].Trim();
        var endText = spec[(dashIndex + 1)..].Trim();

        if (totalLength <= 0)
            return RangeParseResult.Unsatisfiable;

        // "bytes=-N": the last N bytes.
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffixLength) || suffixLength <= 0)
            {
                reason = "Malformed suffix range.";
                return RangeParseResult.WholeFile;
            }

            var start = Math.Max(0, totalLength - suffixLength);
            range = new ByteRange(start, totalLength - start);
            return RangeParseResult.Partial;
        }

        if (!long.TryParse(startText, out var rangeStart) || rangeStart < 0)
        {
            reason = "Malformed range start.";
            return RangeParseResult.WholeFile;
        }

        if (rangeStart >= totalLength)
            return RangeParseResult.Unsatisfiable;

        if (endText.Length == 0)
        {
            range = new ByteRange(rangeStart, totalLength - rangeStart);
            return RangeParseResult.Partial;
        }

        if (!long.TryParse(endText, out var rangeEnd) || rangeEnd < rangeStart)
        {
            reason = "Malformed range end.";
            return RangeParseResult.WholeFile;
        }

        // Clients may ask for more than exists; clamp instead of failing.
        rangeEnd = Math.Min(rangeEnd, totalLength - 1);
        range = new ByteRange(rangeStart, rangeEnd - rangeStart + 1);
        return RangeParseResult.Partial;
    }
}
