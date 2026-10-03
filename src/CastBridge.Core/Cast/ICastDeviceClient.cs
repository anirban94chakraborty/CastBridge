namespace CastBridge.Core.Cast;

/// <summary>
/// Everything the app needs from a Cast device. The production implementation wraps SharpCaster;
/// keeping this interface small means the protocol library can be replaced (or faked in tests)
/// without touching the UI or the media pipeline.
/// </summary>
public interface ICastDeviceClient : IAsyncDisposable
{
    CastDevice Device { get; }

    CastConnectionState ConnectionState { get; }

    bool IsConnected { get; }

    CastReceiverSnapshot Receiver { get; }

    CastMediaSnapshot Media { get; }

    event EventHandler<CastConnectionState>? ConnectionStateChanged;

    event EventHandler<CastReceiverSnapshot>? ReceiverChanged;

    event EventHandler<CastMediaSnapshot>? MediaChanged;

    event EventHandler<CastError>? ErrorOccurred;

    Task<CastReceiverSnapshot> ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<CastReceiverSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    // Device level control (works even when the device is idle).
    Task<CastReceiverSnapshot> SetVolumeAsync(double level, CancellationToken cancellationToken = default);

    Task<CastReceiverSnapshot> SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    /// <summary>Stops the running receiver application so the device returns to idle.</summary>
    Task<CastReceiverSnapshot> StopReceiverAppAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a specific receiver application by its Cast app id. Chromium does not always use the
    /// Default Media Receiver: casting YouTube or Spotify launches those services' own receivers,
    /// which is why a speaker can play one and refuse the other.
    /// </summary>
    Task<CastReceiverSnapshot> LaunchReceiverAppAsync(string appId, CancellationToken cancellationToken = default);

    // Media control.
    Task<CastMediaSnapshot> LoadAsync(CastMediaRequest request, bool autoPlay = true, CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> LoadQueueAsync(IReadOnlyList<CastMediaRequest> items, int startIndex = 0, string repeatMode = "OFF", CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> InsertIntoQueueAsync(IReadOnlyList<CastMediaRequest> items, CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> PlayAsync(CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> PauseAsync(CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> StopMediaAsync(CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> NextAsync(CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> PreviousAsync(CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> SetRepeatModeAsync(string repeatMode, CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> SetShuffleAsync(bool shuffle, CancellationToken cancellationToken = default);

    Task<CastMediaSnapshot> GetMediaStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Speakers that belong to this device when it is a multi-room group.</summary>
    Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the volume of a specific member of a multi-room group.</summary>
    Task SetGroupMemberVolumeAsync(string memberDeviceId, double level, CancellationToken cancellationToken = default);
}
