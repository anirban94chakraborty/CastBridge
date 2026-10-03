using CastBridge.Core;
using CastBridge.Core.Cast;
using CastBridge.Media.Compatibility;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CastBridge.App.ViewModels;

/// <summary>
/// One speaker in the sidebar. The view model owns presentation only; the protocol lives in Core.
/// </summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(CastDevice device, DevicePlaybackSupport support)
    {
        Device = device;
        Support = support;
        SupportMessage = support.Reason;
    }

    public CastDevice Device { get; }

    public DevicePlaybackSupport Support { get; }

    public string Id => Device.Id;

    public string Name => Device.Name;

    public string Address => Device.Port == 8009 ? Device.IpAddress : $"{Device.IpAddress}:{Device.Port}";

    public string Kind => Device.DisplayKind;

    public bool IsGroup => Device.Kind == CastDeviceKind.Group;

    public bool IsOnline => !Device.IsStale;

    /// <summary>Volume the user is dragging. Separate from the value the device confirmed.</summary>
    [ObservableProperty]
    public partial int Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Not connected";

    /// <summary>
    /// True while device state is being folded in. The volume slider is bound two-way, so without
    /// this the window would treat a device report as user intent and write the same value straight
    /// back to the speaker.
    /// </summary>
    public bool IsApplyingDeviceState { get; private set; }

    /// <summary>The last media snapshot the device reported, which is what Now Playing shows.</summary>
    public CastMediaSnapshot Media { get; private set; } = CastMediaSnapshot.Empty;

    /// <summary>The device's receiver state: which app is running, and whether it is mirroring.</summary>
    public CastReceiverSnapshot Receiver { get; private set; } = new();

    /// <summary>Set when the device cannot play local files; shown in place of a dead Cast button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCast))]
    public partial string? SupportMessage { get; set; }

    /// <summary>Result of the end-to-end cast test, once the user has run it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Compatibility))]
    public partial PlaybackVerdict Verdict { get; set; } = PlaybackVerdict.Unknown;

    /// <summary>Why the verdict came out the way it did.</summary>
    [ObservableProperty]
    public partial string? VerdictDetail { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public string Compatibility => Verdict switch
    {
        PlaybackVerdict.Unknown => "not tested",
        PlaybackVerdict.Ready => "can play from this PC",
        _ => CompatibilityResult.Describe(Verdict),
    };

    public bool HasMedia => Media.HasMedia;

    public string? Title => Media.Title;

    public string? Subtitle => Join(" • ", Media.Artist, Media.Album);

    public string? AlbumArtUrl => Media.AlbumArtUrl;

    public bool HasArtwork => !string.IsNullOrWhiteSpace(Media.AlbumArtUrl);

    public bool IsPlaying => Media.IsPlaying;

    public string PlayPauseGlyph => IsPlaying ? "❚❚" : "▶";

    /// <summary>
    /// True when the speaker is mirroring a screen or a tab rather than playing media - which is
    /// what Chromium does when it casts tab audio. A mirroring session has no media session behind
    /// it, so there is nothing on the device for a play button to control.
    /// </summary>
    public bool IsMirroring => Receiver.IsMirroring;

    /// <summary>The app the speaker is running, for example "Chrome Audio Mirroring".</summary>
    public string? ReceiverAppName => Receiver.RunningAppName;

    /// <summary>Play/pause/stop can only be sent while a real media session exists.</summary>
    public bool CanControlTransport => !IsMirroring && HasMedia;

    /// <summary>Explains the transport buttons when they are unavailable.</summary>
    public string TransportNote => IsMirroring
        ? "Mirroring browser audio — play and pause stay in the browser"
        : HasMedia
            ? string.Empty
            : "Nothing playing";

    public bool HasTransportNote => TransportNote.Length > 0;

    /// <summary>What to show when the device is not playing media.</summary>
    public string EmptyMediaMessage
    {
        get
        {
            if (!IsMirroring)
                return "Nothing playing";

            var app = string.IsNullOrWhiteSpace(ReceiverAppName) ? "browser" : ReceiverAppName;
            return $"Mirroring browser audio · {app}";
        }
    }

    /// <summary>One line of now-playing text, for the compact tray popup.</summary>
    public string NowPlayingLine => HasMedia
        ? Title ?? PlaybackState
        : EmptyMediaMessage;

    /// <summary>The player state as a word, exactly as the device reports it.</summary>
    public string PlaybackState => Media.PlayerState switch
    {
        CastPlayerState.Playing => "Playing",
        CastPlayerState.Paused => "Paused",
        CastPlayerState.Buffering => "Buffering",
        CastPlayerState.Loading => "Loading",
        CastPlayerState.Idle => "Stopped",
        _ => "Idle",
    };

    /// <summary>"1:23 / 4:56" for a track, "Live" for a stream, empty when there is nothing to show.</summary>
    public string Timeline => Media.IsLive
        ? "Live"
        : Media.Duration is { TotalSeconds: > 0 } duration
            ? $"{Format(Media.Position)} / {Format(duration)}"
            : string.Empty;

    public bool HasTimeline => Timeline.Length > 0;

    public double ProgressPercent => Media.Duration is { TotalSeconds: > 0 } duration
        ? Math.Clamp(Media.Position.TotalSeconds / duration.TotalSeconds * 100, 0, 100)
        : 0;

    public bool CanCast => Support.CanCastAnything;

    /// <summary>Folds a protocol state snapshot into the fields the UI binds to.</summary>
    public void Apply(DeviceRuntimeState? state)
    {
        if (state is null)
        {
            Status = "Not connected";
            return;
        }

        Status = DescribeConnection(state.Connection);
        OnPropertyChanged(nameof(IsOnline));

        if (state.Volume.IsKnown)
        {
            IsApplyingDeviceState = true;
            try
            {
                Volume = state.Volume.Percent;
                IsMuted = state.Volume.Muted;
            }
            finally
            {
                IsApplyingDeviceState = false;
            }
        }

        Media = state.Media;
        Receiver = state.Receiver;

        // The media snapshot is one immutable record, so announce each derived value explicitly.
        OnPropertyChanged(nameof(Media));
        OnPropertyChanged(nameof(Receiver));
        OnPropertyChanged(nameof(IsMirroring));
        OnPropertyChanged(nameof(ReceiverAppName));
        OnPropertyChanged(nameof(CanControlTransport));
        OnPropertyChanged(nameof(TransportNote));
        OnPropertyChanged(nameof(HasTransportNote));
        OnPropertyChanged(nameof(EmptyMediaMessage));
        OnPropertyChanged(nameof(NowPlayingLine));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(AlbumArtUrl));
        OnPropertyChanged(nameof(HasArtwork));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(PlaybackState));
        OnPropertyChanged(nameof(Timeline));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return kept.Length == 0 ? null : string.Join(separator, kept);
    }

    private static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    private static string DescribeConnection(CastConnectionState state) => state switch
    {
        CastConnectionState.Connected => "Connected",
        CastConnectionState.Connecting => "Connecting…",
        CastConnectionState.Failed => "Connection failed",
        _ => "Not connected",
    };
}
