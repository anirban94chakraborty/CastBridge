namespace CastBridge.Core.Cast;

/// <summary>What kind of Cast target a discovered device is.</summary>
public enum CastDeviceKind
{
    /// <summary>Discovered but not yet classified (for example via the subnet probe).</summary>
    Unknown,

    /// <summary>Audio-only device such as Chromecast Audio, Google Home, a smart speaker or a soundbar.</summary>
    Speaker,

    /// <summary>Device with a video output, such as a Chromecast or a TV.</summary>
    Display,

    /// <summary>Multi-room group defined in the Google Home app.</summary>
    Group,
}

public enum CastConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public enum CastErrorKind
{
    Network,
    Protocol,
    MediaLoad,
    Timeout,
    Unsupported,
    Other,
}

/// <summary>A discovered Cast target. Immutable; discovery produces new instances.</summary>
public sealed record CastDevice
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>IPv4 address the device is reachable on.</summary>
    public required string IpAddress { get; init; }

    public int Port { get; init; } = 8009;

    public string? Model { get; init; }

    public string? FirmwareVersion { get; init; }

    public CastDeviceKind Kind { get; init; } = CastDeviceKind.Unknown;

    public bool IsAudioOnly { get; init; }

    public string? MacAddress { get; init; }

    public string? NetworkName { get; init; }

    public long? UptimeSeconds { get; init; }

    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the user entered the address by hand instead of it being discovered.</summary>
    public bool IsManual { get; init; }

    /// <summary>How this device was found: mdns, subnet, cache or manual.</summary>
    public string? DiscoverySource { get; init; }

    public IReadOnlyDictionary<string, string> ExtraInfo { get; init; } = new Dictionary<string, string>();

    public Uri DeviceUri => new($"http://{IpAddress}");

    /// <summary>True when the device has not answered the most recent scan.</summary>
    public bool IsStale { get; init; }

    public string DisplayKind => Kind switch
    {
        CastDeviceKind.Speaker => "Speaker",
        CastDeviceKind.Display => "Display",
        CastDeviceKind.Group => "Speaker group",
        _ => "Cast device",
    };

    public override string ToString() => $"{Name} ({IpAddress}{(IsStale ? ", stale" : "")})";
}

public sealed record VolumeState(double Level, bool Muted)
{
    public static readonly VolumeState Unknown = new(double.NaN, false);

    public bool IsKnown => !double.IsNaN(Level);

    public int Percent => IsKnown ? (int)Math.Round(Math.Clamp(Level, 0, 1) * 100) : 0;
}

public enum CastPlayerState
{
    Unknown,
    Idle,
    Loading,
    Buffering,
    Playing,
    Paused,
}

/// <summary>Snapshot of what a device is currently playing.</summary>
public sealed record CastMediaSnapshot
{
    public static readonly CastMediaSnapshot Empty = new();

    public CastPlayerState PlayerState { get; init; } = CastPlayerState.Unknown;

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtUrl { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan? Duration { get; init; }

    public bool IsLive { get; init; }

    public string? IdleReason { get; init; }

    public int CurrentItemId { get; init; }

    public int ItemCount { get; init; }

    public string RepeatMode { get; init; } = "OFF";

    public bool Shuffle { get; init; }

    public bool SupportsSeek { get; init; } = true;

    public bool IsPlaying => PlayerState == CastPlayerState.Playing;

    public bool HasMedia => !string.IsNullOrWhiteSpace(Title) || PlayerState is CastPlayerState.Playing or CastPlayerState.Paused or CastPlayerState.Buffering;

    public override string ToString() => $"{PlayerState}: {Title ?? "(nothing)"}";
}

/// <summary>Snapshot of the device-level receiver state (volume, running app, standby).</summary>
public sealed record CastReceiverSnapshot
{
    /// <summary>Chromecast's generic screen mirroring receiver.</summary>
    public const string MirroringAppId = "0F5096E8";

    /// <summary>The receiver Chromium uses when a browser tab or window is cast.</summary>
    public const string ChromeMirroringAppId = "85CDB22F";

    /// <summary>Every receiver id that carries an audio stream rather than a media session.</summary>
    public static readonly string[] MirroringAppIds = [MirroringAppId, ChromeMirroringAppId];

    public VolumeState Volume { get; init; } = VolumeState.Unknown;

    public string? RunningAppId { get; init; }

    public string? RunningAppName { get; init; }

    public bool IsStandBy { get; init; }

    public bool IsIdle => string.IsNullOrWhiteSpace(RunningAppId);

    public string? SessionId { get; init; }

    /// <summary>
    /// True when the device is mirroring a screen or a browser tab rather than playing media.
    ///
    /// A mirroring session carries an audio stream and no media session, so play/pause simply does
    /// not exist on the device side. That is the normal case when Chromium casts a tab's audio,
    /// and it deserves to be named rather than dressed up as a failed command.
    /// </summary>
    public bool IsMirroring =>
        (RunningAppId is { } id && MirroringAppIds.Contains(id, StringComparer.OrdinalIgnoreCase)) ||
        (RunningAppName?.Contains("mirror", StringComparison.OrdinalIgnoreCase) ?? false);
}

public sealed record GroupMember(string DeviceId, string Name, VolumeState Volume);

public sealed record CastError(CastErrorKind Kind, string Message, string? Detail = null)
{
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>Describes a single item the app wants a device to play.</summary>
public sealed record CastMediaRequest
{
    public required string ContentUrl { get; init; }

    public required string ContentType { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public string? AlbumArtUrl { get; init; }

    public TimeSpan? Duration { get; init; }

    public bool IsLive { get; init; }

    /// <summary>Where the media came from, used for logging and the now-playing UI.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Start position; used when re-loading transcoded media at a seek point.</summary>
    public TimeSpan? StartPosition { get; init; }

    public override string ToString() => Title ?? ContentUrl;
}
