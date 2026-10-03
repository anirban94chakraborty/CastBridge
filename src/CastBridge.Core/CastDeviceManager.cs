using Microsoft.Extensions.Logging;
using CastBridge.Core.Cast;
using CastBridge.Core.Discovery;
using CastBridge.Core.Volume;

namespace CastBridge.Core;

public sealed record DeviceRuntimeState
{
    public required CastDevice Device { get; init; }
    public CastConnectionState Connection { get; init; } = CastConnectionState.Disconnected;
    public VolumeState Volume { get; init; } = VolumeState.Unknown;
    public CastMediaSnapshot Media { get; init; } = CastMediaSnapshot.Empty;
    public CastReceiverSnapshot Receiver { get; init; } = new();
    public string? LastError { get; init; }
    public bool IsActive { get; init; }
    public bool HasControl => Connection == CastConnectionState.Connected;
}

/// <summary>What the app is currently casting, and to where.</summary>
public sealed record CastSession
{
    public required string DeviceId { get; init; }
    public required IReadOnlyList<MediaItem> Items { get; init; }
    public int StartIndex { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;

    public MediaItem? Current => Items.Count == 0 ? null : Items[Math.Clamp(StartIndex, 0, Items.Count - 1)];
}

/// <summary>
/// Owns connections, volume pacing, the active cast session and reconnection. The UI and the CLI
/// both drive devices through this type and never touch a protocol client directly.
/// </summary>
public sealed class CastDeviceManager : IAsyncDisposable
{
    private readonly CastDiscoveryService _discovery;
    private readonly IMediaUrlProvider? _mediaProvider;
    private readonly ILogger<CastDeviceManager>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<CastDevice, ICastDeviceClient> _clientFactory;
    private readonly Dictionary<string, DeviceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;
    private string? _watchedDeviceId;
    private bool _disposed;

    public CastDeviceManager(
        CastDiscoveryService discovery,
        IMediaUrlProvider? mediaProvider = null,
        ILoggerFactory? loggerFactory = null,
        Func<CastDevice, ICastDeviceClient>? clientFactory = null)
    {
        _discovery = discovery;
        _mediaProvider = mediaProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<CastDeviceManager>();
        _clientFactory = clientFactory ?? (device => new SharpCasterDeviceClient(device, loggerFactory));

        _discovery.DevicesChanged += (_, _) => SynchronizeEntries();
    }

    public event EventHandler<DeviceRuntimeState>? DeviceStateChanged;

    public event EventHandler<CastError>? ErrorOccurred;

    public event EventHandler<string?>? ActiveDeviceChanged;

    public CastSession? Session { get; private set; }

    public string? ActiveDeviceId { get; private set; }

    public IReadOnlyList<CastDevice> Devices => _discovery.Devices;

    public CastDiscoveryService Discovery => _discovery;

    public IReadOnlyList<DeviceRuntimeState> GetStates()
    {
        lock (_sync)
            return _entries.Values.Select(e => e.Snapshot()).ToArray();
    }

    public DeviceRuntimeState? GetState(string deviceId)
    {
        lock (_sync)
            return _entries.TryGetValue(deviceId, out var entry) ? entry.Snapshot() : null;
    }

    public CastDevice? FindDevice(string deviceId) =>
        Devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connects to a device on demand and returns a live client.</summary>
    public async Task<ICastDeviceClient> GetClientAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = FindDevice(deviceId) ?? throw new InvalidOperationException($"Unknown device '{deviceId}'.");
        var entry = GetOrCreateEntry(device);

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Client is { IsConnected: true })
                return entry.Client;

            if (entry.Client is null)
            {
                entry.Client = _clientFactory(device);
                entry.Attach(Subscribe);
            }

            await entry.Client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            entry.RecordSuccess();
            RaiseState(entry);
            return entry.Client;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // ---------------------------------------------------------------- volume and mute

    public Task SetVolumeAsync(string deviceId, double level, CancellationToken cancellationToken = default)
    {
        var device = FindDevice(deviceId) ?? throw new InvalidOperationException($"Unknown device '{deviceId}'.");
        var entry = GetOrCreateEntry(device);
        var coalescer = EnsureCoalescer(entry);

        // Optimistic update so dragging a slider feels immediate, then pace the device writes.
        entry.Volume = new VolumeState(Math.Clamp(level, 0, 1), entry.Volume.Muted);
        RaiseState(entry);
        coalescer.SetLevel(level);
        return Task.CompletedTask;
    }

    private VolumeCoalescer EnsureCoalescer(DeviceEntry entry)
    {
        if (entry.Coalescer is not null)
            return entry.Coalescer;

        entry.Coalescer = new VolumeCoalescer(async level =>
        {
            var client = await GetClientAsync(entry.Device.Id).ConfigureAwait(false);
            var receiver = await client.SetVolumeAsync(level).ConfigureAwait(false);
            UpdateFromReceiver(entry.Device.Id, receiver);
        });

        entry.Coalescer.Failed += ex =>
        {
            entry.LastError = ex.Message;
            RaiseState(entry);
        };

        return entry.Coalescer;
    }

    public async Task SetMutedAsync(string deviceId, bool muted, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var snapshot = await client.SetMutedAsync(muted, cancellationToken).ConfigureAwait(false);
        UpdateFromReceiver(deviceId, snapshot);
    }

    public Task ToggleMuteAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var state = GetState(deviceId);
        return SetMutedAsync(deviceId, !(state?.Volume.Muted ?? false), cancellationToken);
    }

    /// <summary>
    /// Group members each keep their own volume. We prefer talking to the member directly (the
    /// documented receiver path) and only fall back to the group's multizone channel.
    /// </summary>
    public async Task SetGroupMemberVolumeAsync(string groupDeviceId, string memberDeviceId, double level, CancellationToken cancellationToken = default)
    {
        var member = Devices.FirstOrDefault(d =>
            string.Equals(d.Id, memberDeviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.MacAddress, memberDeviceId, StringComparison.OrdinalIgnoreCase));

        if (member is not null)
        {
            await SetVolumeAsync(member.Id, level, cancellationToken).ConfigureAwait(false);
            return;
        }

        var client = await GetClientAsync(groupDeviceId, cancellationToken).ConfigureAwait(false);
        await client.SetGroupMemberVolumeAsync(memberDeviceId, level, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return await client.GetGroupMembersAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- casting

    /// <summary>Casts a list of items and makes that device the active target.</summary>
    public async Task CastQueueAsync(
        string deviceId,
        IReadOnlyList<MediaItem> items,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return;

        var provider = _mediaProvider ?? throw new InvalidOperationException("No media provider is configured.");
        var device = FindDevice(deviceId) ?? throw new InvalidOperationException($"Unknown device '{deviceId}'.");
        var prepared = new List<CastMediaRequest>(items.Count);
        foreach (var item in items)
            prepared.Add(await provider.PrepareAsync(item, device.IpAddress, cancellationToken).ConfigureAwait(false));

        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (prepared.Count == 1)
            await client.LoadAsync(prepared[0], autoPlay: true, cancellationToken).ConfigureAwait(false);
        else
            await client.LoadQueueAsync(prepared, Math.Clamp(startIndex, 0, prepared.Count - 1), "OFF", cancellationToken).ConfigureAwait(false);

        Session = new CastSession { DeviceId = deviceId, Items = items, StartIndex = startIndex };
        SetActive(deviceId);
        StartPolling();
    }

    public async Task AppendToQueueAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        var provider = _mediaProvider ?? throw new InvalidOperationException("No media provider is configured.");
        var deviceId = ActiveDeviceId ?? throw new InvalidOperationException("Nothing is being cast yet.");
        var targetAddress = FindDevice(deviceId)?.IpAddress;

        var prepared = new List<CastMediaRequest>(items.Count);
        foreach (var item in items)
            prepared.Add(await provider.PrepareAsync(item, targetAddress, cancellationToken).ConfigureAwait(false));

        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.InsertIntoQueueAsync(prepared, cancellationToken).ConfigureAwait(false);

        if (Session is not null)
            Session = Session with { Items = Session.Items.Concat(items).ToArray() };
    }

    public async Task PlayAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.PlayAsync(cancellationToken).ConfigureAwait(false);
        SetActive(deviceId);
        StartPolling();
    }

    public async Task PauseAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NextAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.NextAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PreviousAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.PreviousAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekAsync(string deviceId, TimeSpan position, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.SeekAsync(position, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRepeatModeAsync(string deviceId, string repeatMode, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.SetRepeatModeAsync(repeatMode, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetShuffleAsync(string deviceId, bool shuffle, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);
        await client.SetShuffleAsync(shuffle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops playback and sends the device back to idle.</summary>
    public async Task StopAsync(string deviceId, bool stopReceiverApp = true, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(deviceId, out var entry) || entry.Client is null)
        {
            if (string.Equals(ActiveDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                ClearActive();
            return;
        }

        try
        {
            await entry.Client.StopMediaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Stop media failed for {Device}", deviceId);
        }

        if (stopReceiverApp)
        {
            try
            {
                await entry.Client.StopReceiverAppAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Stopping the receiver app failed for {Device}", deviceId);
            }
        }

        entry.Media = CastMediaSnapshot.Empty;
        RaiseState(entry);

        if (Session?.DeviceId == deviceId)
        {
            foreach (var item in Session.Items)
                _mediaProvider?.Release(item.Id ?? item.Path);

            Session = null;
            ClearActive();
        }
    }

    /// <summary>Re-reads volume and media from a device, for example after the user changes the volume elsewhere.</summary>
    public Task RefreshAsync(string deviceId, CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(deviceId, surfaceErrors: true, cancellationToken);

    /// <summary>
    /// Keeps a device connected and freshly polled.
    ///
    /// The popup calls this for whatever it is controlling. It matters because the state this app
    /// shows - volume, the running media session, whether the speaker is idle - is normally changed
    /// somewhere else: the browser starts the cast, the Google Home app renames a group, somebody
    /// touches the speaker. Without a watcher there is nothing to read that state from.
    /// </summary>
    public async Task WatchAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _watchedDeviceId = deviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        StartPolling();
        await RefreshCoreAsync(deviceId, surfaceErrors: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The device the popup is following, or null when nothing is selected.</summary>
    public string? WatchedDeviceId => _watchedDeviceId;

    private async Task RefreshCoreAsync(string deviceId, bool surfaceErrors, CancellationToken cancellationToken)
    {
        var device = FindDevice(deviceId);
        if (device is null)
            return;

        var entry = GetOrCreateEntry(device);
        try
        {
            // Connect on demand. A control-only app has to be able to read a speaker that it has
            // never spoken to, because the session we are controlling was started by the browser.
            var client = entry.Client is { IsConnected: true } connected
                ? connected
                : await GetClientAsync(deviceId, cancellationToken).ConfigureAwait(false);

            var receiver = await client.RefreshAsync(cancellationToken).ConfigureAwait(false);
            UpdateFromReceiver(deviceId, receiver);
            entry.Media = client.Media;
            RaiseState(entry);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Refresh failed for {Device}", deviceId);

            if (surfaceErrors)
            {
                entry.LastError = ex.Message;
                RaiseState(entry);
            }
        }
    }

    private void SetActive(string deviceId)
    {
        if (string.Equals(ActiveDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        ActiveDeviceId = deviceId;
        ActiveDeviceChanged?.Invoke(this, deviceId);
        foreach (var entry in _entries.Values.ToArray())
            RaiseState(entry);
    }

    private void ClearActive()
    {
        ActiveDeviceId = null;
        ActiveDeviceChanged?.Invoke(this, null);
    }

    // ---------------------------------------------------------------- plumbing

    private sealed class DeviceEntry
    {
        public DeviceEntry(CastDevice device)
        {
            Device = device;
        }

        public CastDevice Device { get; set; }
        public ICastDeviceClient? Client { get; set; }
        public VolumeCoalescer? Coalescer { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public VolumeState Volume { get; set; } = VolumeState.Unknown;
        public CastMediaSnapshot Media { get; set; } = CastMediaSnapshot.Empty;
        public CastConnectionState Connection { get; set; } = CastConnectionState.Disconnected;
        public string? LastError { get; set; }
        public int ReconnectAttempts { get; set; }

        /// <summary>A flapping link must not start a reconnect loop per dropped message.</summary>
        public bool Reconnecting { get; set; }

        public void Attach(Action<DeviceEntry, ICastDeviceClient> subscribe) => subscribe(this, Client!);

        public void RecordSuccess() => ReconnectAttempts = 0;

        public DeviceRuntimeState Snapshot() => new()
        {
            Device = Device,
            Connection = Connection,
            Volume = Volume,
            Media = Media,
            Receiver = Client?.Receiver ?? new CastReceiverSnapshot(),
            LastError = LastError,
        };
    }

    private DeviceEntry GetOrCreateEntry(CastDevice device)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(device.Id, out var existing))
            {
                existing.Device = device;
                return existing;
            }

            var entry = new DeviceEntry(device);
            _entries[device.Id] = entry;
            return entry;
        }
    }

    private void SynchronizeEntries()
    {
        lock (_sync)
        {
            foreach (var device in _discovery.Devices)
            {
                if (_entries.TryGetValue(device.Id, out var entry))
                    entry.Device = device;
            }
        }
    }

    private void Subscribe(DeviceEntry entry, ICastDeviceClient client)
    {
        client.ReceiverChanged += (_, receiver) => UpdateFromReceiver(entry.Device.Id, receiver);
        client.MediaChanged += (_, media) =>
        {
            entry.Media = media;
            RaiseState(entry);
        };
        client.ConnectionStateChanged += (_, state) =>
        {
            entry.Connection = state;
            RaiseState(entry);

            if (state is CastConnectionState.Disconnected or CastConnectionState.Failed)
                _ = TryReconnectAsync(entry, client);
        };
        client.ErrorOccurred += (_, error) =>
        {
            entry.LastError = error.Message;
            RaiseState(entry);
            ErrorOccurred?.Invoke(this, error);
        };
    }

    private void UpdateFromReceiver(string deviceId, CastReceiverSnapshot receiver)
    {
        if (!_entries.TryGetValue(deviceId, out var entry))
            return;

        entry.Volume = receiver.Volume;
        entry.Connection = entry.Client?.ConnectionState ?? entry.Connection;
        entry.Coalescer?.ResetFromDevice(receiver.Volume.Level);
        RaiseState(entry);
    }

    private async Task TryReconnectAsync(DeviceEntry entry, ICastDeviceClient client)
    {
        if (_disposed || entry.Client != client || entry.Reconnecting)
            return;

        entry.Reconnecting = true;
        try
        {
            await ReconnectLoopAsync(entry, client).ConfigureAwait(false);
        }
        finally
        {
            entry.Reconnecting = false;
        }
    }

    private async Task ReconnectLoopAsync(DeviceEntry entry, ICastDeviceClient client)
    {
        for (var attempt = 1; attempt <= 4 && !_disposed; attempt++)
        {
            entry.ReconnectAttempts = attempt;
            var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempt - 1)));
            try
            {
                await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (_disposed || entry.Client != client)
                return;

            try
            {
                await client.ConnectAsync().ConfigureAwait(false);
                entry.RecordSuccess();
                RaiseState(entry);
                return;
            }
            catch (Exception ex)
            {
                entry.LastError = $"Reconnect failed: {ex.Message}";
                RaiseState(entry);
            }
        }
    }

    private void RaiseState(DeviceEntry entry) =>
        DeviceStateChanged?.Invoke(this, entry.Snapshot() with { IsActive = entry.Device.Id == ActiveDeviceId });

    private void StartPolling()
    {
        if (_pollLoop is not null)
            return;

        _pollCts = new CancellationTokenSource();
        _pollLoop = Task.Run(() => PollAsync(_pollCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// The device pushes status messages, but a slow poll keeps the UI honest when a track ends,
    /// when the network drops a message, or when someone else changes the device. It follows the
    /// watched device (what the popup shows) and the active cast target, if they differ.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                var watched = _watchedDeviceId;
                var active = ActiveDeviceId;

                if (watched is null && active is null)
                {
                    if (_pollLoop is not null && !_entries.Values.Any(e => e.Client?.IsConnected == true))
                        return;

                    continue;
                }

                if (watched is not null)
                    await RefreshCoreAsync(watched, surfaceErrors: false, cancellationToken).ConfigureAwait(false);

                if (active is not null && !string.Equals(active, watched, StringComparison.OrdinalIgnoreCase))
                    await RefreshCoreAsync(active, surfaceErrors: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Status poll failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_pollCts is not null)
                await _pollCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        List<DeviceEntry> entries;
        lock (_sync)
            entries = _entries.Values.ToList();

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Coalescer is not null)
                    await entry.Coalescer.DisposeAsync().ConfigureAwait(false);

                if (entry.Client is not null)
                    await entry.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Disposing {Device} failed", entry.Device.Name);
            }
        }

        _pollCts?.Dispose();
    }
}
