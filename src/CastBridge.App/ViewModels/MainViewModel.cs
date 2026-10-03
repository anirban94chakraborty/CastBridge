using System.Collections.ObjectModel;
using System.Windows;
using CastBridge.Core;
using CastBridge.Core.Cast;
using CastBridge.Core.Diagnostics;
using CastBridge.Core.Discovery;
using CastBridge.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CastBridge.App.ViewModels;

/// <summary>
/// Drives the volume popup.
///
/// This app controls speakers; it does not send them anything. Casting happens in the browser, and
/// everything here is volume, mute and transport on the local Cast protocol - none of which needs a
/// media server, a local port, or anything else running in the background.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly CastBridgeServices _services;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(CastBridgeServices services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _logger = loggerFactory.CreateLogger<MainViewModel>();

        _services.Devices.DeviceStateChanged += OnDeviceStateChanged;
        _services.Devices.ErrorOccurred += OnError;
        _services.Discovery.DevicesChanged += OnDevicesChanged;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    /// <summary>The speaker group the popup is currently controlling.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial DeviceViewModel? Target { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "Looking for speakers…";

    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>
    /// Follows the selected speaker: connects once, then keeps its volume and media session fresh.
    /// Without this the popup would sit at zero showing nothing, because the cast it is looking at
    /// was started by the browser, not by us.
    /// </summary>
    partial void OnTargetChanged(DeviceViewModel? value) => _ = WatchTargetAsync(value);

    private async Task WatchTargetAsync(DeviceViewModel? target)
    {
        try
        {
            await _services.Devices.WatchAsync(target?.Id);
            LastError = null;
        }
        catch (Exception ex)
        {
            Fail($"Could not reach {target?.Name}: {ex.Message}");
        }
    }

    /// <summary>Used by the device picker; follows the same target as the popup.</summary>
    public DeviceViewModel? Selected
    {
        get => Target;
        set
        {
            if (value is not null && !ReferenceEquals(value, Target))
                Target = value;
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsScanning = true;
        try
        {
            await _services.Discovery.RefreshAsync();
        }
        catch (Exception ex)
        {
            Fail($"Could not scan the network: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Pushes the slider to the speaker. Volume writes are coalesced on the Core side, so firing on
    /// every step of a drag is safe and keeps the control feeling immediate.
    /// </summary>
    [RelayCommand]
    private async Task SetVolumeAsync()
    {
        if (Target is not { } target)
            return;

        try
        {
            await _services.Devices.SetVolumeAsync(target.Id, target.Volume / 100.0);
            LastError = null;
        }
        catch (Exception ex)
        {
            Fail($"Could not set the volume on {target.Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        if (Target is not { } target)
            return;

        try
        {
            await _services.Devices.ToggleMuteAsync(target.Id);
            target.Apply(await RefreshDeviceAsync(target));
            LastError = null;
        }
        catch (Exception ex)
        {
            Fail($"Could not change mute on {target.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Toggles play and pause through the device's own media session.
    ///
    /// The session belongs to whoever started the cast - normally the browser - so the command is
    /// sent with the session id the device last reported, after confirming that a session exists.
    /// </summary>
    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (Target is not { } target)
            return;

        try
        {
            var state = await RefreshDeviceAsync(target);
            if (state?.Media.IsPlaying == true)
                await _services.Devices.PauseAsync(target.Id);
            else
                await _services.Devices.PlayAsync(target.Id);

            LastError = null;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (Target is not { } target) return;
        await TryAsync(() => _services.Devices.NextAsync(target.Id));
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (Target is not { } target) return;
        await TryAsync(() => _services.Devices.PreviousAsync(target.Id));
    }

    /// <summary>
    /// Stops playback but leaves the receiver running. Quitting the receiver app would also tear
    /// down the cast the browser set up, which is not what a Stop button is for.
    /// </summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (Target is not { } target) return;
        await TryAsync(() => _services.Devices.StopAsync(target.Id, stopReceiverApp: false));
    }

    [RelayCommand]
    private void ShowDiagnostics()
    {
        var items = NetworkDiagnostics.Run(_services.Devices.Devices, _services.Server?.Port);
        Summary = string.Join("   •   ", items.Select(i => $"{i.Title}: {i.Detail}"));
    }

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            LastError = null;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task<DeviceRuntimeState?> RefreshDeviceAsync(DeviceViewModel device)
    {
        try
        {
            await _services.Devices.RefreshAsync(device.Id);
            var state = _services.Devices.GetState(device.Id);
            device.Apply(state);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Refresh failed for {Device}", device.Name);
            return null;
        }
    }

    private void OnDevicesChanged(object? sender, IReadOnlyList<CastDevice> found) =>
        Dispatch(() => Rebuild(found));

    /// <summary>
    /// Rebuilds the list, keeping the chosen speaker selected. On startup the group wins, because a
    /// stereo pair is the thing people actually want to reach for.
    /// </summary>
    private void Rebuild(IReadOnlyList<CastDevice> found)
    {
        var existing = Devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var targetId = Target?.Id;

        Devices.Clear();
        foreach (var device in found)
        {
            if (!existing.TryGetValue(device.Id, out var item))
                item = new DeviceViewModel(device, DevicePlaybackSupport.FromDevice(device));

            item.Apply(_services.Devices.GetState(device.Id));
            Devices.Add(item);
        }

        // Keep whatever the user picked; otherwise prefer the group, then any sensible default.
        var preferred = Devices.FirstOrDefault(d => d.Id == targetId)
            ?? Devices.FirstOrDefault(d => DeviceTargetSelector.IsGroup(d.Device))
            ?? Devices.FirstOrDefault(d => DeviceTargetSelector.ChooseDefault(found)?.Id == d.Id);

        Target = preferred ?? Devices.FirstOrDefault();

        Summary = Devices.Count == 0
            ? "No Cast devices found. Check that the speakers and this PC are on the same network."
            : Target is null
                ? $"{Devices.Count} device(s) found"
                : $"Controlling {Target.Name} · {Devices.Count} device(s) found";
    }

    private void OnDeviceStateChanged(object? sender, DeviceRuntimeState state) =>
        Dispatch(() => Devices.FirstOrDefault(d => d.Id == state.Device.Id)?.Apply(state));

    private void OnError(object? sender, CastError error) => Fail(error.ToString());

    private void Fail(string message)
    {
        _logger.LogWarning("{Message}", message);
        Dispatch(() =>
        {
            LastError = message;
            Summary = message;
        });
    }

    /// <summary>
    /// Marshals onto the UI thread; the protocol raises events on its own threads.
    ///
    /// The action runs inside a guard, and the call never blocks the caller. Dispatcher.Invoke would
    /// rethrow anything the action threw back onto the poll thread, where nothing can catch it - that
    /// takes the whole process down without so much as a log line.
    /// </summary>
    private void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RunSafely(action);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => RunSafely(action)));
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Never let a UI update failure become a process failure.
            _logger.LogWarning(ex, "UI update failed");
        }
    }
}