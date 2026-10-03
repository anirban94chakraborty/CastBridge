using System.Windows;
using CastBridge.App.ViewModels;

namespace CastBridge.App;

/// <summary>
/// The volume popup. Closing it hides to the tray instead of exiting, because the point of this app
/// is to sit quietly in the background while the browser does the casting.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (e.OldValue == e.NewValue || e.OldValue < 0)
            return;

        // A volume the device reported is not user intent. Writing it back would echo every poll
        // (and fight a drag in progress on the other window's slider).
        if (ViewModel?.Target?.IsApplyingDeviceState == true)
            return;

        ViewModel?.SetVolumeCommand.Execute(null);
    }

    private void OnToggleMute(object sender, RoutedEventArgs e) =>
        ViewModel?.ToggleMuteCommand.Execute(null);

    private void OnPlayPause(object sender, RoutedEventArgs e) =>
        ViewModel?.PlayPauseCommand.Execute(null);

    private void OnNext(object sender, RoutedEventArgs e) =>
        ViewModel?.NextCommand.Execute(null);

    private void OnPrevious(object sender, RoutedEventArgs e) =>
        ViewModel?.PreviousCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e) =>
        ViewModel?.StopCommand.Execute(null);

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        ViewModel?.RefreshCommand.Execute(null);

    private void OnSwitchDevice(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        var picker = new DevicePickerWindow { Owner = this, DataContext = viewModel };
        if (picker.ShowDialog() == true && picker.Picked is { } picked)
            viewModel.Target = picked;
    }
}
