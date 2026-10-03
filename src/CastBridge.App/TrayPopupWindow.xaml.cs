using System.Windows;
using CastBridge.App.ViewModels;

namespace CastBridge.App;

/// <summary>
/// The small volume popup that opens when the tray icon is clicked. It binds to the same view model
/// as the main window, so the slider, the mute state and the now-playing line stay in step with
/// whatever the main window is showing.
///
/// It takes the focus when it opens, which is what makes the dismissal rule work: a click anywhere
/// outside moves the focus away, the window deactivates, and the app hides it. Nothing here tracks
/// the pointer or runs a timer.
/// </summary>
public partial class TrayPopupWindow : Window
{
    public TrayPopupWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user asks for the full window from inside the popup.</summary>
    public event EventHandler? OpenRequested;

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Places the popup just above the notification area, the way the system's own flyouts sit.
    ///
    /// Anchoring to the tray rather than to the pointer keeps it in one predictable place even when
    /// Windows has tucked this app's icon into the icon-overflow flyout.
    /// </summary>
    public void ShowAboveTray()
    {
        try
        {
            var work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - Width - 6);
            Top = Math.Max(work.Top, work.Bottom - Height - 6);
        }
        catch (Exception)
        {
            // Never let placement stop the popup from appearing; the defaults are usable.
        }

        if (!IsVisible)
            Show();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.OldValue == e.NewValue || e.OldValue < 0)
            return;

        // A volume the device reported is not user intent; see MainWindow for the full explanation.
        if (ViewModel?.Target?.IsApplyingDeviceState == true)
            return;

        ViewModel?.SetVolumeCommand.Execute(null);
    }

    private void OnToggleMute(object sender, RoutedEventArgs e) =>
        ViewModel?.ToggleMuteCommand.Execute(null);

    private void OnPlayPause(object sender, RoutedEventArgs e) =>
        ViewModel?.PlayPauseCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e) =>
        ViewModel?.StopCommand.Execute(null);

    private void OnOpen(object sender, RoutedEventArgs e) =>
        OpenRequested?.Invoke(this, EventArgs.Empty);
}
