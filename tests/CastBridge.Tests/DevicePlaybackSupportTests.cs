using CastBridge.Core.Cast;
using Xunit;

namespace CastBridge.Tests;

/// <summary>
/// The Xiaomi Mi Smart Speakers on this network report opencast=false and refuse to launch the
/// Default Media Receiver. These tests pin that behaviour down so the UI keeps telling the truth
/// instead of offering a Cast button that fails with a protocol error.
/// </summary>
public class DevicePlaybackSupportTests
{
    [Fact]
    public void A_device_that_disables_opencast_cannot_play_local_files()
    {
        var device = Device(extra: new Dictionary<string, string> { ["opencast"] = "false" });

        var support = DevicePlaybackSupport.FromDevice(device);

        Assert.False(support.CanPlayLocalFiles);
        Assert.False(support.CanCastAnything);
        Assert.False(string.IsNullOrWhiteSpace(support.Reason));
    }

    [Fact]
    public void A_device_that_allows_opencast_can_play_local_files()
    {
        var device = Device(extra: new Dictionary<string, string> { ["opencast"] = "true" });

        var support = DevicePlaybackSupport.FromDevice(device);

        Assert.True(support.CanPlayLocalFiles);
        Assert.Null(support.Reason);
    }

    [Fact]
    public void A_group_that_advertises_a_receiver_is_allowed_despite_opencast_being_false()
    {
        // The stereo group on this network reports both opencast=false and rs=Default Media
        // Receiver. It does accept a load, so refusing up front would be wrong.
        var device = Device(extra: new Dictionary<string, string>
        {
            ["opencast"] = "false",
            ["rs"] = "Default Media Receiver",
        });

        Assert.True(DevicePlaybackSupport.FromDevice(device).CanPlayLocalFiles);
    }

    [Fact]
    public void A_device_that_says_nothing_is_assumed_capable()
    {
        // Being wrong in this direction means a cast is attempted and fails visibly, which is
        // better than hiding the control for a device that would have worked.
        Assert.True(DevicePlaybackSupport.FromDevice(Device()).CanPlayLocalFiles);
    }

    [Fact]
    public void Volume_control_is_always_available()
    {
        var support = DevicePlaybackSupport.FromDevice(Device(extra: new Dictionary<string, string> { ["opencast"] = "false" }));
        Assert.True(support.CanControlVolume);
    }

    [Fact]
    public void Capability_bits_identify_audio_only_devices_and_groups()
    {
        const int audioOut = 1 << 2;
        const int multiZone = 1 << 4;

        Assert.True(CastCapabilities.IsAudioOnly(CastCapabilities.Parse(audioOut.ToString())));
        Assert.True(CastCapabilities.IsGroup(CastCapabilities.Parse((audioOut | multiZone).ToString())));
        Assert.False(CastCapabilities.IsGroup(CastCapabilities.Parse(audioOut.ToString())));
    }

    [Fact]
    public void A_missing_capability_value_is_unknown_rather_than_a_guess()
    {
        Assert.Null(CastCapabilities.Parse(null));
        Assert.Null(CastCapabilities.Parse("not-a-number"));
    }

    private static CastDevice Device(IReadOnlyDictionary<string, string>? extra = null) => new()
    {
        Id = "abc",
        Name = "Test speaker",
        IpAddress = "192.168.1.50",
        ExtraInfo = extra ?? new Dictionary<string, string>(),
    };
}
