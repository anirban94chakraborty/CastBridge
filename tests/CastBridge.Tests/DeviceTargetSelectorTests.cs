using CastBridge.Core.Cast;
using CastBridge.Core.Discovery;
using Xunit;

namespace CastBridge.Tests;

/// <summary>
/// A stereo pair shows up as three devices, and the group is what the user actually wants to reach
/// for: it is the only one whose volume moves both speakers. These tests pin that preference down.
/// </summary>
public class DeviceTargetSelectorTests
{
    [Fact]
    public void The_group_is_preferred_over_its_own_members()
    {
        var devices = new[]
        {
            Device("speaker-a", "Speaker New"),
            Device("group", "Smart Speaker Stereo", CastDeviceKind.Group),
            Device("speaker-b", "Speaker Old"),
        };

        Assert.Equal("group", DeviceTargetSelector.ChooseDefault(devices)!.Id);
    }

    [Fact]
    public void The_group_wins_even_when_the_members_come_first()
    {
        var devices = new[]
        {
            Device("group", "Smart Speaker Stereo", CastDeviceKind.Group),
            Device("speaker-a", "Speaker New"),
        };

        Assert.Equal("group", DeviceTargetSelector.ChooseDefault(devices)!.Id);
    }

    [Fact]
    public void A_speaker_is_preferred_over_a_display_when_there_is_no_group()
    {
        var devices = new[]
        {
            Device("tv", "Living Room TV", CastDeviceKind.Display),
            Device("speaker", "Kitchen Speaker", CastDeviceKind.Speaker),
        };

        Assert.Equal("speaker", DeviceTargetSelector.ChooseDefault(devices)!.Id);
    }

    [Fact]
    public void Something_is_better_than_nothing()
    {
        var devices = new[] { Device("only", "Only Device", CastDeviceKind.Unknown) };
        Assert.Equal("only", DeviceTargetSelector.ChooseDefault(devices)!.Id);
    }

    [Fact]
    public void No_devices_means_no_target()
    {
        Assert.Null(DeviceTargetSelector.ChooseDefault(Array.Empty<CastDevice>()));
    }

    [Fact]
    public void A_device_advertising_itself_as_a_group_counts_even_if_unclassified()
    {
        // Some devices only say "Google Cast Group" in their mDNS model string.
        var device = Device("group", "Smart Speaker Stereo");
        device = device with { ExtraInfo = new Dictionary<string, string> { ["md"] = "Google Cast Group" } };

        Assert.True(DeviceTargetSelector.IsGroup(device));
        Assert.Equal("group", DeviceTargetSelector.ChooseDefault([device, Device("s", "Speaker New")])!.Id);
    }

    [Fact]
    public void Members_of_a_stereo_pair_are_found_by_shared_address()
    {
        var group = Device("group", "Smart Speaker Stereo", CastDeviceKind.Group, "192.168.29.231", 32000);
        var memberA = Device("a", "Speaker New", CastDeviceKind.Speaker, "192.168.29.231", 8009);
        var memberB = Device("b", "Speaker Old", CastDeviceKind.Speaker, "192.168.29.246", 8009);

        var members = DeviceTargetSelector.MembersOf(group, new[] { group, memberA, memberB });

        Assert.Equal(["a"], members.Select(m => m.Id));
    }

    [Fact]
    public void An_individual_speaker_has_no_members()
    {
        var speaker = Device("a", "Speaker New");
        Assert.Empty(DeviceTargetSelector.MembersOf(speaker, new[] { speaker }));
    }

    private static CastDevice Device(
        string id,
        string name,
        CastDeviceKind kind = CastDeviceKind.Speaker,
        string ip = "192.168.29.231",
        int port = 8009) => new()
    {
        Id = id,
        Name = name,
        IpAddress = ip,
        Port = port,
        Kind = kind,
    };
}