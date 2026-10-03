using CastBridge.Core.Volume;
using CastBridge.Media;
using Xunit;

namespace CastBridge.Tests;

/// <summary>
/// Volume is written on every step of a slider drag. The coalescer is what keeps that from
/// flooding the speaker with messages, so its "only the last value wins" promise is worth testing.
/// </summary>
public class VolumeCoalescerTests
{
    [Fact]
    public async Task Many_rapid_writes_result_in_one_write_of_the_final_value()
    {
        var applied = new List<double>();
        await using var coalescer = new VolumeCoalescer(level =>
        {
            applied.Add(level);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(80));

        for (var i = 0; i <= 100; i += 5)
            coalescer.SetLevel(i / 100.0);

        await coalescer.FlushAsync();

        Assert.Equal([1.0], applied);
    }

    [Fact]
    public async Task The_target_reflects_the_latest_value_immediately()
    {
        await using var coalescer = new VolumeCoalescer(_ => Task.CompletedTask, TimeSpan.FromSeconds(5));

        coalescer.SetLevel(0.25);
        Assert.Equal(0.25, coalescer.Target, 3);

        coalescer.SetLevel(0.75);
        Assert.Equal(0.75, coalescer.Target, 3);

        await coalescer.FlushAsync();
    }

    [Fact]
    public async Task A_pending_user_change_survives_a_stale_device_report()
    {
        var applied = new List<double>();
        await using var coalescer = new VolumeCoalescer(level =>
        {
            applied.Add(level);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(50));

        coalescer.SetLevel(0.9);

        // The device reports 0.4 because our change has not reached it yet. Dropping the pending
        // write here would silently discard what the user just asked for.
        coalescer.ResetFromDevice(0.4);
        await coalescer.FlushAsync();

        Assert.Equal([0.9], applied);
    }

    [Fact]
    public async Task A_device_report_becomes_the_baseline_when_nothing_is_pending()
    {
        var applied = new List<double>();
        await using var coalescer = new VolumeCoalescer(level =>
        {
            applied.Add(level);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(50));

        coalescer.ResetFromDevice(0.4);
        await coalescer.FlushAsync();

        // Nothing was requested locally, so the app must not push a value back at the device.
        Assert.Empty(applied);
        Assert.Equal(0.4, coalescer.Target, 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task Boundary_levels_are_passed_through(double level)
    {
        var applied = new List<double>();
        await using var coalescer = new VolumeCoalescer(l =>
        {
            applied.Add(l);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(10));

        coalescer.SetLevel(level);
        await coalescer.FlushAsync();

        Assert.Equal([level], applied);
    }
}

/// <summary>Format classification decides whether a track plays directly or needs FFmpeg first.</summary>
public class AudioFormatsTests
{
    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.flac", true)]
    [InlineData("song.aac", true)]
    [InlineData("song.ogg", true)]
    [InlineData("song.opus", true)]
    [InlineData("song.wav", true)]
    [InlineData("song.m4a", true)]
    [InlineData("song.wma", false)]
    [InlineData("song.aiff", false)]
    public void Google_Cast_codecs_play_directly(string fileName, bool native)
    {
        Assert.Equal(native, AudioFormats.Resolve(fileName).IsNative);
    }

    [Theory]
    [InlineData("song.wma")]
    [InlineData("song.aiff")]
    public void Unsupported_formats_explain_themselves(string fileName)
    {
        var format = AudioFormats.Resolve(fileName);

        Assert.False(format.IsNative);
        Assert.False(string.IsNullOrWhiteSpace(format.Reason));
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("notes.txt", false)]
    [InlineData("cover.jpg", false)]
    [InlineData("video.mkv", false)]
    public void Only_known_audio_extensions_are_indexed(string fileName, bool supported)
    {
        Assert.Equal(supported, AudioFormats.IsSupportedFile(fileName));
    }
}
