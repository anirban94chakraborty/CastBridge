using System.Windows;
using System.Windows.Input;
using CastBridge.App.ViewModels;

namespace CastBridge.App;

/// <summary>Lets the user point the popup at a different speaker or group.</summary>
public partial class DevicePickerWindow : Window
{
    public DevicePickerWindow()
    {
        InitializeComponent();
    }

    public DeviceViewModel? Picked => (DataContext as MainViewModel)?.Selected;

    private void OnPick(object sender, MouseButtonEventArgs e)
    {
        if (Picked is not null)
            DialogResult = true;
    }
}