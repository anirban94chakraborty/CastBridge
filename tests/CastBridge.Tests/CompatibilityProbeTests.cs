using CastBridge.Core.Cast;
using CastBridge.Media.Compatibility;
using Xunit;

namespace CastBridge.Tests;

/// <summary>
/// The probe exists to give one honest answer per device, so what it reports is worth pinning down.
/// The parts that touch the network are verified against real hardware separately; these cover the
/// wording and the cheap early-exit that keeps a "no" from waking the speaker.
/// </summary>
public class CompatibilityProbeTests
{
    [Fact]
    public void Every_verdict_has_a_human_readable_description()
    {
        foreach (var verdict in Enum.GetValues<PlaybackVerdict>())
            Assert.False(string.IsNullOrWhiteSpace(CompatibilityResult.Describe(verdict)));
    }

    [Fact]
    public void Only_Ready_counts_as_able_to_play()
    {
        Assert.True(new CompatibilityResult("a", "A", PlaybackVerdict.Ready, "", default).CanPlay);

        foreach (var verdict in Enum.GetValues<PlaybackVerdict>().Where(v => v != PlaybackVerdict.Ready))
            Assert.False(new CompatibilityResult("a", "A", verdict, "", default).CanPlay);
    }

    [Fact]
    public void The_summary_names_the_device_and_the_verdict()
    {
        var result = new CompatibilityResult("id", "Speaker New", PlaybackVerdict.NotSupported, "opencast=false", default);

        Assert.Contains("Speaker New", result.Summary);
        Assert.Contains("cannot play", result.Summary);
        Assert.Contains("opencast=false", result.Summary);
    }

    [Fact]
    public void The_probe_audio_is_a_valid_wave_header()
    {
        var wave = CompatibilityProbe.CreateNearSilenceWave();

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wave, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wave, 36, 4));
    }

    [Fact]
    public void The_probe_audio_declares_the_length_the_header_claims()
    {
        var wave = CompatibilityProbe.CreateNearSilenceWave();

        var riffSize = BitConverter.ToUInt32(wave, 4);
        var dataSize = BitConverter.ToUInt32(wave, 40);

        Assert.Equal((uint)(wave.Length - 8), riffSize);
        Assert.Equal((uint)(wave.Length - 44), dataSize);
        Assert.Equal(44100u, BitConverter.ToUInt32(wave, 24));   // sample rate
        Assert.Equal((ushort)1, BitConverter.ToUInt16(wave, 20)); // mono PCM
        Assert.Equal((ushort)16, BitConverter.ToUInt16(wave, 34)); // 16-bit
    }

    [Fact]
    public void The_probe_tone_is_inaudible()
    {
        // A connection test that plays a tone on someone's speaker is an unwelcome surprise.
        var wave = CompatibilityProbe.CreateNearSilenceWave();

        var peak = 0;
        for (var i = 44; i + 1 < wave.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(wave, i)));

        Assert.True(peak < 100, $"probe audio peak was {peak}, which could be audible");
    }

    [Fact]
    public void An_opencast_false_device_is_reported_as_unsupported_before_any_traffic()
    {
        // DevicePlaybackSupport is what the probe consults first; it must agree with the verdict.
        var device = new CastDevice
        {
            Id = "x",
            Name = "Mi Smart Speaker",
            IpAddress = "192.168.1.9",
            ExtraInfo = new Dictionary<string, string> { ["opencast"] = "false" },
        };

        Assert.False(DevicePlaybackSupport.FromDevice(device).CanCastAnything);
    }
}