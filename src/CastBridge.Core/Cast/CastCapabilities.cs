namespace CastBridge.Core.Cast;

/// <summary>
/// The Cast device capability bitmask as published in the mDNS TXT record ("ca") and in the
/// cast channel DeviceInfo message. Values match Chromium's CastDeviceCapability enum.
/// </summary>
[Flags]
public enum CastDeviceCapabilities
{
    None = 0,
    VideoOut = 1 << 0,
    VideoIn = 1 << 1,
    AudioOut = 1 << 2,
    AudioIn = 1 << 3,
    MultiZoneGroup = 1 << 4,
    MultichannelAudioGroup = 1 << 5,
}

public static class CastCapabilities
{
    /// <summary>Parses the "ca" TXT value. Returns null when absent or unparsable.</summary>
    public static CastDeviceCapabilities? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.Trim(), out var value) ? (CastDeviceCapabilities)value : null;
    }

    public static bool IsAudioOnly(CastDeviceCapabilities? capabilities) =>
        capabilities is { } c && !c.HasFlag(CastDeviceCapabilities.VideoOut);

    public static bool IsGroup(CastDeviceCapabilities? capabilities) =>
        capabilities is { } c && (c.HasFlag(CastDeviceCapabilities.MultiZoneGroup) || c.HasFlag(CastDeviceCapabilities.MultichannelAudioGroup));

    /// <summary>
    /// Classifies a device from the mDNS model string ("md") and capability bitmask. Both are
    /// optional, so an unknown result is normal for devices found only by the subnet probe.
    /// </summary>
    public static CastDeviceKind Classify(string? model, CastDeviceCapabilities? capabilities)
    {
        if (IsGroup(capabilities))
            return CastDeviceKind.Group;

        var m = model?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(m))
        {
            if (m.Contains("group"))
                return CastDeviceKind.Group;
            if (m.Contains("chromecast audio") || m.Contains("speaker") || m.Contains("home") || m.Contains("soundbar") || m.Contains("audio"))
                return CastDeviceKind.Speaker;
            if (m.Contains("chromecast") || m.Contains("tv") || m.Contains("display") || m.Contains("streamer"))
                return CastDeviceKind.Display;
        }

        if (capabilities is { } c)
        {
            if (!c.HasFlag(CastDeviceCapabilities.VideoOut))
                return CastDeviceKind.Speaker;
            return CastDeviceKind.Display;
        }

        return CastDeviceKind.Unknown;
    }

    /// <summary>Human readable summary used by the diagnostics panel and CLI.</summary>
    public static string Describe(CastDeviceCapabilities? capabilities) =>
        capabilities is null ? "unknown" : capabilities.Value.ToString();
}

/// <summary>
/// What a device will actually let us do, derived from what it advertises. This exists so the UI can
/// disable controls that cannot work instead of letting the user hit a protocol-level failure.
/// </summary>
public sealed record DevicePlaybackSupport(
    bool CanPlayLocalFiles,
    bool CanStreamFromPc,
    bool CanControlVolume,
    string? Reason)
{
    public bool CanCastAnything => CanPlayLocalFiles || CanStreamFromPc;

    public static DevicePlaybackSupport FromDevice(CastDevice device)
    {
        var openCast = device.ExtraInfo.TryGetValue("opencast", out var raw) && raw == "true";
        var explicitOpenCast = device.ExtraInfo.ContainsKey("opencast");

        // Groups advertise their receiver in the mDNS "rs" field, which is a positive signal.
        var advertisesReceiver = device.ExtraInfo.TryGetValue("rs", out var receiver) &&
                                 !string.IsNullOrWhiteSpace(receiver);

        var canPlay = !explicitOpenCast || openCast || advertisesReceiver;

        return new DevicePlaybackSupport(
            CanPlayLocalFiles: canPlay,
            CanStreamFromPc: canPlay,
            CanControlVolume: true,
            Reason: canPlay
                ? null
                : "This device reports opencast=false, so it will not run Google's Default Media " +
                  "receiver. It accepts volume, playback and status control, but it cannot play " +
                  "audio files from this PC. Use the Google Home app, or a Chromecast that has " +
                  "the Default Media Receiver enabled.");
    }
}
