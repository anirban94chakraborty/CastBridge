using CastBridge.Media.Server;
using Xunit;

namespace CastBridge.Tests;

/// <summary>
/// Range parsing is the part of the media server that decides whether a speaker can seek. A wrong
/// answer here shows up as a track that plays but cannot be scrubbed, so it gets tested hard.
/// </summary>
public class RangeHeaderParserTests
{
    [Theory]
    [InlineData("bytes=0-1023", 0, 1024)]
    [InlineData("bytes=1024-", 1024, 4096 - 1024)]
    [InlineData("bytes=-500", 4096 - 500, 500)]
    [InlineData("bytes=0-0", 0, 1)]
    public void Parses_the_common_range_shapes(string header, long start, long length)
    {
        var result = RangeHeaderParser.TryParse(header, 4096, out var range, out var reason);

        Assert.Equal(RangeParseResult.Partial, result);
        Assert.Null(reason);
        Assert.Equal(start, range.Start);
        Assert.Equal(length, range.Length);
    }

    [Fact]
    public void A_range_past_the_end_is_clamped_rather_than_rejected()
    {
        // Speakers routinely ask for more than the file has; the RFC answer is to clamp, and a
        // rejected range here shows up on the device as a stalled track.
        var result = RangeHeaderParser.TryParse("bytes=4000-99999", 4096, out var range, out _);

        Assert.Equal(RangeParseResult.Partial, result);
        Assert.Equal(4000, range.Start);
        Assert.Equal(96, range.Length);
        Assert.Equal(4095, range.End);
    }

    [Fact]
    public void A_whole_file_range_is_clamped_to_the_file()
    {
        var result = RangeHeaderParser.TryParse("bytes=0-999999", 4096, out var range, out _);

        Assert.Equal(RangeParseResult.Partial, result);
        Assert.Equal(0, range.Start);
        Assert.Equal(4096, range.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_range_header_means_serve_the_whole_file(string? header)
    {
        var result = RangeHeaderParser.TryParse(header, 4096, out _, out var reason);

        Assert.Equal(RangeParseResult.WholeFile, result);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("bytes=abc-def")]
    [InlineData("items=0-10")]
    [InlineData("bytes")]
    [InlineData("bytes=2000-1000")]
    public void A_malformed_range_falls_back_to_the_whole_file_rather_than_failing(string header)
    {
        // A speaker must never lose a track because it sent a header we do not understand, so a bad
        // Range degrades to a 200 with the full body, and records why.
        var result = RangeHeaderParser.TryParse(header, 4096, out _, out var reason);

        Assert.Equal(RangeParseResult.WholeFile, result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void An_unsatisfiable_range_is_distinct_from_a_malformed_one()
    {
        // The server answers these differently: 416 versus a 200 with the full body, and speakers care.
        Assert.Equal(RangeParseResult.Unsatisfiable, RangeHeaderParser.TryParse("bytes=9999-", 4096, out _, out _));
    }

    [Fact]
    public void ContentRange_header_is_well_formed()
    {
        var range = new ByteRange(1024, 2048);
        Assert.Equal("bytes 1024-3071/10000", range.ToContentRange(10000));
    }
}
