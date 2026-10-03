using CastBridge.Core.Cast;

namespace CastBridge.Tests;

/// <summary>
/// The transport buttons can only work while a media session exists. Chromium's tab-audio cast is a
/// mirroring session with no media session behind it, so the app has to recognise it and say so
/// instead of offering buttons that cannot do anything.
/// </summary>
public class ReceiverStateTests
{
    [Theory]
    [InlineData(CastReceiverSnapshot.MirroringAppId)]
    [InlineData(CastReceiverSnapshot.ChromeMirroringAppId)]
    public void A_mirroring_receiver_is_recognised_by_its_app_id(string appId)
    {
        var receiver = new CastReceiverSnapshot
        {
            RunningAppId = appId,
            RunningAppName = "something localised",
        };

        Assert.True(receiver.IsMirroring);
    }

    [Fact]
    public void The_ids_are_matched_whatever_their_case()
    {
        var receiver = new CastReceiverSnapshot { RunningAppId = "85cdb22f" };

        Assert.True(receiver.IsMirroring);
    }

    [Fact]
    public void The_mirroring_receiver_is_recognised_by_its_name()
    {
        // What the speaker actually reports for a Chromium tab cast, with an id we might not know.
        var receiver = new CastReceiverSnapshot
        {
            RunningAppId = "ABCD1234",
            RunningAppName = "Chrome Audio Mirroring",
        };

        Assert.True(receiver.IsMirroring);
    }

    [Theory]
    [InlineData("CC1AD845", "Default Media Receiver")]
    [InlineData("233637DE", "YouTube")]
    [InlineData(null, null)]
    public void A_media_receiver_is_not_mirroring(string? appId, string? appName)
    {
        var receiver = new CastReceiverSnapshot { RunningAppId = appId, RunningAppName = appName };

        Assert.False(receiver.IsMirroring);
    }

    [Fact]
    public void An_idle_device_is_not_mirroring()
    {
        Assert.False(new CastReceiverSnapshot().IsMirroring);
        Assert.True(new CastReceiverSnapshot().IsIdle);
    }
}
