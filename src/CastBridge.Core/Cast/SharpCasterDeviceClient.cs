using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;
using Sharpcaster.Models.Queue;

namespace CastBridge.Core.Cast;

/// <summary>
/// <see cref="ICastDeviceClient"/> backed by SharpCaster. All protocol details (namespaces,
/// transports, protobuf framing) stay inside this class.
/// </summary>
public sealed class SharpCasterDeviceClient : ICastDeviceClient
{
    /// <summary>Google's Default Media Receiver. Universally available, including on audio-only devices.</summary>
    public const string DefaultMediaReceiverAppId = "CC1AD845";

    private const string ReceiverNamespace = "urn:x-cast:com.google.cast.receiver";
    private const string MultiZoneNamespace = "urn:x-cast:com.google.cast.multizone";
    private const string MediaNamespace = "urn:x-cast:com.google.cast.media";
    private const string ReceiverTransportId = "receiver-0";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Brief pause after the handshake, before the first LAUNCH or volume write.</summary>
    private static readonly TimeSpan ConnectSettle = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<SharpCasterDeviceClient>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _groupStatusTimeout = TimeSpan.FromSeconds(4);

    private ChromecastClient? _client;
    private bool _disposed;

    public SharpCasterDeviceClient(CastDevice device, ILoggerFactory? loggerFactory = null)
    {
        Device = device;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<SharpCasterDeviceClient>();
    }

    public CastDevice Device { get; }

    public CastConnectionState ConnectionState { get; private set; } = CastConnectionState.Disconnected;

    public bool IsConnected => ConnectionState == CastConnectionState.Connected && _client is not null;

    public CastReceiverSnapshot Receiver { get; private set; } = new();

    public CastMediaSnapshot Media { get; private set; } = CastMediaSnapshot.Empty;

    public event EventHandler<CastConnectionState>? ConnectionStateChanged;
    public event EventHandler<CastReceiverSnapshot>? ReceiverChanged;
    public event EventHandler<CastMediaSnapshot>? MediaChanged;
    public event EventHandler<CastError>? ErrorOccurred;

    public async Task<CastReceiverSnapshot> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return Receiver;

            // A socket that is no longer connected is dead weight: close it before opening another,
            // or a flapping connection leaks a receive loop on every attempt.
            if (_client is { } stale)
            {
                Detach(stale);
                await SafeDisposeAsync(stale).ConfigureAwait(false);
                _client = null;
            }

            SetConnectionState(CastConnectionState.Connecting);

            var client = new ChromecastClient(_loggerFactory?.CreateLogger<ChromecastClient>())
            {
                FriendlyName = "CastBridge",
            };

            client.Disconnected += OnDisconnected;
            client.MediaChannel.StatusChanged += OnMediaStatusChanged;
            client.MediaChannel.LoadFailed += OnLoadFailed;
            client.MediaChannel.ErrorHappened += OnMediaError;
            client.MediaChannel.InvalidRequest += OnInvalidRequest;
            client.ReceiverChannel.ReceiverStatusChanged += OnReceiverStatusChanged;
            client.ReceiverChannel.LaunchStatusChanged += OnLaunchStatusChanged;
            client.MultiZoneChannel.StatusChanged += OnMultiZoneStatusChanged;

            var receiver = new ChromecastReceiver
            {
                Name = Device.Name,
                DeviceUri = Device.DeviceUri,
                Port = Device.Port,
                Model = Device.Model ?? string.Empty,
                Version = Device.FirmwareVersion ?? string.Empty,
            };

            try
            {
                var status = await WithTimeout(client.ConnectChromecast(receiver), ConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _client = client;
                SetConnectionState(CastConnectionState.Connected);
                ApplyReceiverStatus(status);

                // Some receivers (notably multi-room groups) accept the TLS handshake but drop the
                // connection if a LAUNCH arrives in the same breath. A short settle avoids that.
                await Task.Delay(ConnectSettle, cancellationToken).ConfigureAwait(false);
                return Receiver;
            }
            catch (Exception ex)
            {
                Detach(client);
                await SafeDisposeAsync(client).ConfigureAwait(false);
                SetConnectionState(CastConnectionState.Failed);
                RaiseError(new CastError(CastErrorKind.Network, $"Could not connect to {Device.Name} ({Device.IpAddress}).", ex.Message));
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var client = _client;
            _client = null;
            if (client is not null)
            {
                Detach(client);
                await SafeDisposeAsync(client).ConfigureAwait(false);
            }

            SetConnectionState(CastConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CastReceiverSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.ReceiverChannel.GetChromecastStatusAsync(), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyReceiverStatus(status);

        if (Receiver.IsMirroring)
        {
            // A mirroring receiver carries an audio stream, not a media session. Asking it for
            // MEDIA_STATUS goes unanswered and the receiver drops the connection, so the poll would
            // spend its life reconnecting. Each mirroring session is its own app instance.
            if (Media.HasMedia)
            {
                Media = CastMediaSnapshot.Empty;
                MediaChanged?.Invoke(this, Media);
            }

            return Receiver;
        }

        try
        {
            var media = await WithTimeout(client.MediaChannel.GetMediaStatusAsync(), CallTimeout, cancellationToken).ConfigureAwait(false);
            if (media is not null)
                ApplyMediaStatus(media);
        }
        catch (Exception ex)
        {
            // A device with no running app has no media session; that is normal, not an error.
            _logger?.LogDebug(ex, "No media status available for {Device}", Device.Name);
        }

        return Receiver;
    }

    // ---------------------------------------------------------------- device level

    public async Task<CastReceiverSnapshot> SetVolumeAsync(double level, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.ReceiverChannel.SetVolume(Math.Clamp(level, 0, 1)), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyReceiverStatus(status);
        return Receiver;
    }

    public async Task<CastReceiverSnapshot> SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.ReceiverChannel.SetMute(muted), CallTimeout, cancellationToken).ConfigureAwait(false);
        ApplyReceiverStatus(status);
        return Receiver;
    }

    public async Task<CastReceiverSnapshot> StopReceiverAppAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.ReceiverChannel.StopApplication(), CallTimeout, cancellationToken).ConfigureAwait(false);
        ApplyReceiverStatus(status);
        Media = CastMediaSnapshot.Empty;
        MediaChanged?.Invoke(this, Media);
        return Receiver;
    }

    public async Task<CastReceiverSnapshot> LaunchReceiverAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var client = RequireClient();
        var status = await WithTimeout(
                client.LaunchApplicationAsync(appId, joinExistingApplicationSession: true),
                CallTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        ApplyReceiverStatus(status);
        return Receiver;
    }

    // ---------------------------------------------------------------- media

    public async Task EnsureMediaReceiverAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        if (!string.IsNullOrEmpty(Receiver.RunningAppId))
            return;

        // The device is idle here, so the launch must not ask to join an existing session: a receiver
        // that has nothing to join rejects it with LAUNCH_ERROR ("could not be started").
        var status = await WithTimeout(
                client.LaunchApplicationAsync(DefaultMediaReceiverAppId, joinExistingApplicationSession: false),
                CallTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        ApplyReceiverStatus(status);
    }

    public async Task<CastMediaSnapshot> LoadAsync(CastMediaRequest request, bool autoPlay = true, CancellationToken cancellationToken = default)
    {
        await EnsureMediaReceiverAsync(cancellationToken).ConfigureAwait(false);
        var client = RequireClient();
        var media = MapMedia(request);
        var status = await WithTimeout(client.MediaChannel.LoadAsync(media, autoPlay), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public async Task<CastMediaSnapshot> LoadQueueAsync(
        IReadOnlyList<CastMediaRequest> items,
        int startIndex = 0,
        string repeatMode = "OFF",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return Media;

        await EnsureMediaReceiverAsync(cancellationToken).ConfigureAwait(false);
        var client = RequireClient();

        var queueItems = items.Select(i => new QueueItem { Media = MapMedia(i), IsAutoPlay = true }).ToArray();
        var repeat = ParseRepeatMode(repeatMode);
        var index = Math.Clamp(startIndex, 0, items.Count - 1);

        var status = await WithTimeout(client.MediaChannel.QueueLoadAsync(queueItems, repeat, index), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public async Task<CastMediaSnapshot> InsertIntoQueueAsync(IReadOnlyList<CastMediaRequest> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return Media;

        await EnsureMediaReceiverAsync(cancellationToken).ConfigureAwait(false);
        var client = RequireClient();
        var queueItems = items.Select(i => new QueueItem { Media = MapMedia(i), IsAutoPlay = true }).ToArray();
        var status = await WithTimeout(client.MediaChannel.QueueInsertAsync(queueItems), CallTimeout, cancellationToken).ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public Task<CastMediaSnapshot> PlayAsync(CancellationToken cancellationToken = default) =>
        MediaCommandAsync(c => c.MediaChannel.PlayAsync(), cancellationToken);

    public Task<CastMediaSnapshot> PauseAsync(CancellationToken cancellationToken = default) =>
        MediaCommandAsync(c => c.MediaChannel.PauseAsync(), cancellationToken);

    public Task<CastMediaSnapshot> StopMediaAsync(CancellationToken cancellationToken = default) =>
        MediaCommandAsync(c => c.MediaChannel.StopAsync(), cancellationToken);

    public Task<CastMediaSnapshot> NextAsync(CancellationToken cancellationToken = default) =>
        SkipAsync(next: true, cancellationToken);

    public Task<CastMediaSnapshot> PreviousAsync(CancellationToken cancellationToken = default) =>
        SkipAsync(next: false, cancellationToken);

    public async Task<CastMediaSnapshot> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        if (Media is { ItemCount: > 0 } or { Duration: not null } && client.MediaStatus is null)
            await GetMediaStatusAsync(cancellationToken).ConfigureAwait(false);

        var status = await WithTimeout(client.MediaChannel.SeekAsync(Math.Max(0, position.TotalSeconds)), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public async Task<CastMediaSnapshot> SetRepeatModeAsync(string repeatMode, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.MediaChannel.QueueSetRepeatModeAsync(ParseRepeatMode(repeatMode)), CallTimeout, cancellationToken)
            .ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public async Task<CastMediaSnapshot> SetShuffleAsync(bool shuffle, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.MediaChannel.QueueShuffleAsync(shuffle), CallTimeout, cancellationToken).ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    public async Task<CastMediaSnapshot> GetMediaStatusAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = await WithTimeout(client.MediaChannel.GetMediaStatusAsync(), CallTimeout, cancellationToken).ConfigureAwait(false);
        if (status is not null)
            ApplyMediaStatus(status);
        return Media;
    }

    // ---------------------------------------------------------------- multi-room groups

    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var status = client.MultiZoneChannel.Status;

        if (status is null || status.Devices is null || status.Devices.Length == 0)
        {
            // Ask the device to describe its group; the reply arrives as a MultizoneStatus message.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<Sharpcaster.Models.MultiZone.MultiZoneStatus> handler = (_, _) => completion.TrySetResult(true);
            client.MultiZoneChannel.StatusChanged += handler;
            try
            {
                await WithTimeout(
                        client.SendAsync(_logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                            MultiZoneNamespace,
                            "{\"type\":\"GET_STATUS\",\"requestId\":1}",
                            ReceiverTransportId),
                        CallTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                await Task.WhenAny(completion.Task, Task.Delay(_groupStatusTimeout, cancellationToken)).ConfigureAwait(false);
                status = client.MultiZoneChannel.Status;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Multizone status request failed for {Device}", Device.Name);
            }
            finally
            {
                client.MultiZoneChannel.StatusChanged -= handler;
            }
        }

        if (status?.Devices is null)
            return Array.Empty<GroupMember>();

        // A stereo pair reports itself as its only member, which would show up as a useless row in the
        // UI; real multi-room groups list their member speakers here.
        return status.Devices
            .Select(d => new GroupMember(
                d.DeviceId ?? string.Empty,
                d.Name ?? "(unknown)",
                new VolumeState(d.Volume?.Level ?? double.NaN, d.Volume?.Muted ?? false)))
            .Where(m => !string.IsNullOrEmpty(m.DeviceId) && !IdsMatch(m.DeviceId, Device.Id))
            .ToArray();
    }

    private static bool IdsMatch(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(
            a.Replace("-", string.Empty, StringComparison.Ordinal),
            b.Replace("-", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetGroupMemberVolumeAsync(string memberDeviceId, double level, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var payload = JsonSerializer.Serialize(new
        {
            type = "SET_VOLUME",
            requestId = 1,
            volume = new { deviceId = memberDeviceId, level = Math.Clamp(level, 0, 1) },
        });

        await WithTimeout(
                client.SendAsync(
                    _logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    MultiZoneNamespace,
                    payload,
                    ReceiverTransportId),
                CallTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- internals

    private async Task<CastMediaSnapshot> MediaCommandAsync(
        Func<ChromecastClient, Task<MediaStatus?>> command,
        CancellationToken cancellationToken)
    {
        await EnsureMediaSessionAsync(cancellationToken).ConfigureAwait(false);
        var client = RequireClient();
        var status = await WithTimeout(command(client), CallTimeout, cancellationToken).ConfigureAwait(false);
        ApplyMediaStatus(status);
        return Media;
    }

    /// <summary>
    /// Skips a track.
    ///
    /// SharpCaster only exposes QUEUE_NEXT/QUEUE_PREV, which are queue commands the Default Media
    /// Receiver understands but streaming apps (YouTube, SoundCloud) do not. The media namespace also
    /// carries plain NEXT/PREVIOUS, so send that directly when there is no queue to jump around in.
    /// </summary>
    private async Task<CastMediaSnapshot> SkipAsync(bool next, CancellationToken cancellationToken)
    {
        var media = await EnsureMediaSessionAsync(cancellationToken).ConfigureAwait(false);
        var client = RequireClient();
        var application = client.ChromecastStatus?.Application
            ?? throw new InvalidOperationException(NoApplicationMessage());

        // A queue (the Default Media Receiver, or a music app that published items) wants the queue
        // commands; anything else maps NEXT/PREVIOUS onto the app's own next/previous track.
        var queued = media.Items is { Length: > 1 };
        var type = queued
            ? next ? "QUEUE_NEXT" : "QUEUE_PREV"
            : next ? "NEXT" : "PREVIOUS";

        var payload = JsonSerializer.Serialize(new
        {
            type,
            requestId = 1,
            mediaSessionId = media.MediaSessionId,
        });

        await WithTimeout(
                client.SendAsync(
                    _logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    MediaNamespace,
                    payload,
                    application.TransportId ?? ReceiverTransportId),
                CallTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        // The receiver answers with a MEDIA_STATUS broadcast; read it back once it has had a moment
        // to switch tracks, so the caller gets the new title rather than the old one.
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        return await GetMediaStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the active media session, asking the device for one when the app has never seen it.
    ///
    /// Every media command has to carry a mediaSessionId, and in this app the session is normally
    /// started by somebody else (the browser), so the first command after startup needs to discover
    /// the session before it can act on it.
    /// </summary>
    private async Task<MediaStatus> EnsureMediaSessionAsync(CancellationToken cancellationToken)
    {
        var client = RequireClient();

        // Answer before sending anything: a mirroring app does not answer media messages at all, and
        // asking wedges the connection.
        if (Receiver.IsMirroring)
            throw new InvalidOperationException(MirroringMessage());

        if (client.MediaChannel.MediaStatus is { } known)
            return known;

        if (client.ChromecastStatus?.Application is null)
            throw new InvalidOperationException(NoApplicationMessage());

        var fetched = await WithTimeout(client.MediaChannel.GetMediaStatusAsync(), CallTimeout, cancellationToken)
            .ConfigureAwait(false);

        return fetched is not null
            ? fetched
            : throw new InvalidOperationException(
                $"{Device.Name} is not playing media right now. Playback controls only work while a cast " +
                "session is active.");
    }

    /// <summary>
    /// Explains why there is nothing to control. Mirroring a browser tab or the desktop produces a
    /// Cast session with no media session behind it, which is the common case for Chromium users and
    /// is worth naming rather than reporting as a protocol failure.
    /// </summary>
    private string NoApplicationMessage() =>
        $"{Device.Name} has no cast session to control yet. Playback controls work once something is " +
        "casting media to it.";

    private string MirroringMessage() =>
        $"{Device.Name} is mirroring browser audio, not playing media. A mirrored tab has no media " +
        "session on the speaker, so play and pause stay in the browser - this app controls the volume.";

    private ChromecastClient RequireClient() =>
        _client ?? throw new InvalidOperationException($"Not connected to {Device.Name} ({Device.IpAddress}).");

    private static Media MapMedia(CastMediaRequest request)
    {
        var metadata = new MusicTrackMetadata
        {
            MetadataType = MetadataType.Music,
            Title = request.Title ?? "Unknown",
            SongName = request.Title,
            Artist = request.Artist,
            AlbumName = request.Album,
            AlbumArtist = request.AlbumArtist,
        };

        if (!string.IsNullOrEmpty(request.AlbumArtUrl))
            metadata.Images = new[] { new Image { Url = request.AlbumArtUrl } };

        return new Media
        {
            ContentUrl = request.ContentUrl,
            ContentType = request.ContentType,
            StreamType = request.IsLive ? StreamType.Live : StreamType.Buffered,
            Duration = request.IsLive ? null : request.Duration?.TotalSeconds,
            Metadata = metadata,
        };
    }

    private static RepeatModeType ParseRepeatMode(string? mode) => mode?.ToUpperInvariant() switch
    {
        "ALL" => RepeatModeType.ALL,
        "SINGLE" or "ONE" => RepeatModeType.SINGLE,
        "ALL_AND_SHUFFLE" => RepeatModeType.ALL_AND_SHUFFLE,
        _ => RepeatModeType.OFF,
    };

    private void ApplyReceiverStatus(ChromecastStatus? status)
    {
        if (status is null)
            return;

        var volume = status.Volume is null
            ? VolumeState.Unknown
            : new VolumeState(status.Volume.Level ?? double.NaN, status.Volume.Muted ?? false);

        var app = status.Application;
        var snapshot = new CastReceiverSnapshot
        {
            Volume = volume,
            RunningAppId = app?.AppId,
            RunningAppName = app?.DisplayName,
            IsStandBy = status.IsStandBy,
            SessionId = app?.SessionId,
        };

        Receiver = snapshot;
        ReceiverChanged?.Invoke(this, snapshot);
    }

    private void ApplyMediaStatus(MediaStatus? status)
    {
        if (status is null)
            return;

        var media = status.Media;
        var metadata = media?.Metadata as MusicTrackMetadata;
        var idle = string.IsNullOrEmpty(status.IdleReason) ? null : status.IdleReason;

        var snapshot = new CastMediaSnapshot
        {
            PlayerState = MapPlayerState(status.PlayerState),
            Title = metadata?.Title ?? metadata?.SongName ?? media?.Metadata?.Title,
            Artist = metadata?.Artist,
            Album = metadata?.AlbumName,
            AlbumArtUrl = media?.Metadata?.Images?.FirstOrDefault()?.Url,
            Position = TimeSpan.FromSeconds(Math.Max(0, status.CurrentTime)),
            Duration = media?.Duration is { } d && d > 0 ? TimeSpan.FromSeconds(d) : null,
            IsLive = media?.StreamType == StreamType.Live,
            IdleReason = idle,
            CurrentItemId = status.CurrentItemId,
            ItemCount = status.Items?.Length ?? 0,
            RepeatMode = status.RepeatMode.ToString(),
            Shuffle = status.RepeatMode == RepeatModeType.ALL_AND_SHUFFLE,
            SupportsSeek = media?.StreamType != StreamType.Live,
        };

        Media = snapshot;
        MediaChanged?.Invoke(this, snapshot);
    }

    private static CastPlayerState MapPlayerState(PlayerStateType state) => state switch
    {
        PlayerStateType.Playing => CastPlayerState.Playing,
        PlayerStateType.Paused => CastPlayerState.Paused,
        PlayerStateType.Buffering => CastPlayerState.Buffering,
        PlayerStateType.Loading => CastPlayerState.Loading,
        PlayerStateType.Idle => CastPlayerState.Idle,
        _ => CastPlayerState.Unknown,
    };

    private void OnReceiverStatusChanged(object? sender, ChromecastStatus status) => ApplyReceiverStatus(status);

    private void OnMediaStatusChanged(object? sender, MediaStatus status) => ApplyMediaStatus(status);

    private void OnMultiZoneStatusChanged(object? sender, Sharpcaster.Models.MultiZone.MultiZoneStatus status) =>
        GroupStatusUpdated?.Invoke(this, status.Devices?.Select(d => new GroupMember(
            d.DeviceId ?? string.Empty,
            d.Name ?? "(unknown)",
            new VolumeState(d.Volume?.Level ?? double.NaN, d.Volume?.Muted ?? false))).ToArray() ?? Array.Empty<GroupMember>());

    public event EventHandler<IReadOnlyList<GroupMember>>? GroupStatusUpdated;

    private void OnLoadFailed(object? sender, Sharpcaster.Messages.Media.LoadFailedMessage message) =>
        RaiseError(new CastError(
            CastErrorKind.MediaLoad,
            "The speaker could not play that track.",
            $"item {message.ItemId}. The usual causes are a blocked inbound firewall on this PC, " +
            "a file format the speaker cannot decode, or a media URL the speaker cannot reach."));

    private void OnMediaError(object? sender, Sharpcaster.Messages.Media.ErrorMessage message) =>
        RaiseError(new CastError(
            CastErrorKind.Protocol,
            $"The speaker reported a playback error (code {message.DetailedErrorCode}).",
            $"item {message.ItemId}"));

    private void OnLaunchStatusChanged(object? sender, Sharpcaster.Messages.Receiver.LaunchStatusMessage message) =>
        _logger?.LogDebug("Launch status: {Status}", SafeSerialize(message));

    private void OnInvalidRequest(object? sender, Sharpcaster.Messages.Media.InvalidRequestMessage message) =>
        RaiseError(new CastError(CastErrorKind.Protocol, "The speaker rejected the request.", SafeSerialize(message)));

    private void OnDisconnected(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        SetConnectionState(CastConnectionState.Disconnected);
    }

    private void SetConnectionState(CastConnectionState state)
    {
        if (ConnectionState == state)
            return;

        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private void RaiseError(CastError error)
    {
        _logger?.LogWarning("{Error}", error);
        ErrorOccurred?.Invoke(this, error);
    }

    private void Detach(ChromecastClient client)
    {
        try
        {
            client.Disconnected -= OnDisconnected;
            client.MediaChannel.StatusChanged -= OnMediaStatusChanged;
            client.MediaChannel.LoadFailed -= OnLoadFailed;
            client.MediaChannel.ErrorHappened -= OnMediaError;
            client.MediaChannel.InvalidRequest -= OnInvalidRequest;
            client.ReceiverChannel.ReceiverStatusChanged -= OnReceiverStatusChanged;
            client.ReceiverChannel.LaunchStatusChanged -= OnLaunchStatusChanged;
            client.MultiZoneChannel.StatusChanged -= OnMultiZoneStatusChanged;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Detaching event handlers failed");
        }
    }

    private static async Task SafeDisposeAsync(ChromecastClient client)
    {
        try
        {
            await client.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // The socket is already gone; nothing useful to do.
        }

        try
        {
            await client.Dispose().ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The device did not answer within {timeout.TotalSeconds:F0}s.");
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The device did not answer within {timeout.TotalSeconds:F0}s.");
        }

        await task.ConfigureAwait(false);
    }

    private static string SafeSerialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value?.ToString() ?? string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
