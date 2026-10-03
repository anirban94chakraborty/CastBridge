using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CastBridge.App.ViewModels;
using CastBridge.Media;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;

namespace CastBridge.App;

/// <summary>
/// CastBridge runs as a background utility: a tray icon, and a small popup you open from it to set
/// the speaker group's volume.
///
/// It deliberately starts no local HTTP server. Casting is Chromium's job; everything here is volume,
/// mute and transport over the local Cast protocol, none of which needs a port. That keeps the app
/// out of the way of whatever else is serving media, and means it has nothing to firewall.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CastBridge.SingleInstance";
    private const string TrayTooltipIdle = "CastBridge - no speaker selected";
    private const string TrayTooltipReady = "CastBridge - {0} at {1}%";

    /// <summary>
    /// How soon after the popup dismisses a tray click counts as the click that dismissed it rather
    /// than as a fresh "open the popup". Clicking the icon while the popup is open must close it,
    /// and the dismissal and the mouse-up are two halves of that one click.
    /// </summary>
    private static readonly TimeSpan ReopenSuppression = TimeSpan.FromMilliseconds(400);

    private Mutex? _instanceMutex;
    private bool _quitting;
    private ILoggerFactory? _loggerFactory;
    private CastBridgeServices? _services;
    private TaskbarIcon? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayPopupWindow? _trayPopup;
    private DeviceViewModel? _tooltipSource;
    private System.Windows.Controls.MenuItem? _startWithWindowsItem;
    private DateTime _popupDismissedUtc;
    private bool _popupShowing;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Exceptions that never touch the dispatcher still kill the process, and the Windows event
        // log only records a code. Both hooks write the real exception to the log file first.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"FATAL (background thread): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"FATAL (unobserved task): {args.Exception}");
            args.SetObserved();
        };

        // A background app has nowhere to show a crash: without this, a startup fault would leave a
        // process that silently never appears. Everything goes to the log file first.
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"unhandled: {args.Exception}");

            if (_tray is not null)
            {
                // The app is already running; keep it alive and let the user keep using the tray.
                args.Handled = true;
                return;
            }

            MessageBox.Show(
                $"CastBridge hit an unexpected error while starting.\n\n{args.Exception.Message}",
                "CastBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        };

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "CastBridge is already running. Look for it in the notification area.",
                "CastBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // No StartupUri: the window is created here so it can be told to hide instead of close.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);

            // A tray app has nowhere to show a console, so startup problems go to a file.
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                builder.AddProvider(new FileLoggerProvider(Path.Combine(ConfigDirectory, "app.log")));
            }
            catch (Exception)
            {
                // Logging must never be the reason the app fails to start.
            }
        });

        _services = CastBridgeServices.Create(new CastBridgeOptions(), _loggerFactory);
        _ = StartServicesAsync();

        _viewModel = new MainViewModel(_services, _loggerFactory);
        Log("view model created");
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _window = new MainWindow { DataContext = _viewModel };
        MainWindow = _window;
        _window.Closing += (_, e) =>
        {
            // Closing the popup means "get out of my way", not "quit": the tray icon is the app.
            // Only the tray's Quit command sets the flag that lets the shutdown through.
            if (_quitting)
                return;

            e.Cancel = true;
            _window.Hide();
        };

        // Minimizing has no taskbar button to land on (ShowInTaskbar is false), so it means the same
        // thing as closing: get out of the way and stay reachable from the tray.
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.Hide();
        };

        _window.Show();
        Log("popup shown");

        EnsureStartWithWindows();
        CreateTrayIcon();
        Log("startup complete");

        // A developer aid: --tray-popup shows the tray popup straight away, for screenshots and for
        // machines where Windows has hidden the icon in the overflow flyout.
        if (e.Args.Contains("--tray-popup", StringComparer.OrdinalIgnoreCase))
            ShowTrayPopup();
    }

    /// <summary>Where the log and small state markers live.</summary>
    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CastBridge");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.AppendAllText(
                Path.Combine(ConfigDirectory, "app.log"),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Never let logging break startup.
        }
    }

    /// <summary>True the first time the app ever runs, and only then.</summary>
    private static bool IsFirstRun()
    {
        try
        {
            var marker = Path.Combine(ConfigDirectory, "tray-intro-shown");
            if (File.Exists(marker))
                return false;

            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task StartServicesAsync()
    {
        try
        {
            // scanForDevices: true; the media server stays off unless asked for.
            await _services!.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"CastBridge could not start device discovery.\n\n{ex.Message}",
                "CastBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CreateTrayIcon()
    {
        var icon = LoadTrayIcon();

        _tray = new TaskbarIcon
        {
            ToolTipText = TrayTooltipIdle,
            ContextMenu = BuildTrayMenu(),
        };

        if (icon is not null)
            _tray.IconSource = icon;

        // This icon is built in code and never added to a visual tree, so its Loaded event never
        // fires and the library would never hand it to the shell. ForceCreate is exactly for that
        // case; without it the app runs happily with no icon in the notification area at all.
        _tray.ForceCreate();

        Log($"tray icon: source={(icon is null ? "MISSING" : "loaded")} created={_tray.IsCreated}");

        // Windows 11 files a new tray icon into the overflow flyout until the user pins it, which
        // looks exactly like "the app has no icon at all". Point at it once, the first time.
        if (IsFirstRun())
        {
            _tray.ShowNotification(
                "CastBridge is running",
                "Its speaker icon is in the notification area. If you cannot see it, click the ^ arrow and drag it onto the taskbar.",
                H.NotifyIcon.Core.NotificationIcon.Info);
            Log("tray icon: first-run notification shown");
        }

        // A single click opens the small volume popup, the way the system's own flyouts work, and a
        // double click is the standard "open the real window" gesture. Every handler is deferred
        // through Dispatch: these run inside a shell callback, and creating or showing a window from
        // there is what used to kill the process with STATUS_FATAL_USER_CALLBACK_EXCEPTION.
        _tray.TrayLeftMouseUp += (_, _) => Dispatch(OnTrayLeftClick);
        _tray.TrayMouseDoubleClick += (_, _) => Dispatch(OpenMainWindowFromTray);

        _tray.TrayContextMenuOpen += (_, _) => Dispatch(DismissTrayPopup);
        _tray.PreviewTrayContextMenuOpen += (_, _) => Dispatch(() =>
        {
            RefreshStartWithWindowsCheck();
            DismissTrayPopup();
        });
    }

    /// <summary>The tray icon's single left click: open the compact popup, or close it again.</summary>
    private void OnTrayLeftClick()
    {
        // The click that just dismissed the popup must not be read as "open it": clicking the icon
        // while the popup is open counts as a click outside it, so the dismissal and this mouse-up
        // are one gesture. No suppression, and the popup could never be closed from the icon.
        if (DateTime.UtcNow - _popupDismissedUtc < ReopenSuppression)
            return;

        if (_trayPopup is { IsVisible: true })
        {
            DismissTrayPopup();
            return;
        }

        ShowTrayPopup();
    }

    /// <summary>Double click: the standard "open the app" gesture. The single click that led here
    /// already opened the popup, so this closes it and shows the real window instead.</summary>
    private void OpenMainWindowFromTray()
    {
        // The second click's mouse-up is still on its way; keep it from reopening the popup.
        _popupDismissedUtc = DateTime.UtcNow;
        DismissTrayPopup();
        ShowPopup();
    }

    private void ShowTrayPopup()
    {
        if (_popupShowing)
            return;

        _popupShowing = true;
        try
        {
            if (_viewModel is null)
                return;

            if (_trayPopup is null)
                CreateTrayPopup();

            if (_trayPopup is null)
                return;

            var wasHidden = !_trayPopup.IsVisible;
            _trayPopup.ShowAboveTray();

            // Activating is what makes the outside-click dismissal work: any click elsewhere takes
            // the focus away, and that is the event the popup closes on.
            _trayPopup.Activate();

            if (wasHidden)
                Log($"tray popup shown (active={_trayPopup.IsActive})");
        }
        catch (Exception ex)
        {
            Log($"tray popup failed: {ex}");
        }
        finally
        {
            _popupShowing = false;
        }
    }

    private void CreateTrayPopup()
    {
        if (_viewModel is null)
            return;

        _trayPopup = new TrayPopupWindow { DataContext = _viewModel };
        _trayPopup.OpenRequested += (_, _) =>
        {
            DismissTrayPopup();
            ShowPopup();
        };

        // The dismissal rule is the simplest one there is: a click outside the popup moves the focus
        // away, so the window deactivates, and that is the click that closes it. No pointer tracking
        // and no timeout: the popup stays until the user clicks elsewhere.
        _trayPopup.Deactivated += (_, _) => DismissTrayPopup();
    }

    /// <summary>
    /// Hides the popup, recording when, so the click that caused the dismissal is not also taken as
    /// a request to open it again.
    /// </summary>
    private void DismissTrayPopup()
    {
        if (_trayPopup is not { IsVisible: true } popup)
            return;

        popup.Hide();
        _popupDismissedUtc = DateTime.UtcNow;
        Log("tray popup hidden");
    }

    /// <summary>Keeps the menu's check mark honest when the entry is changed outside the app.</summary>
    private void RefreshStartWithWindowsCheck()
    {
        if (_startWithWindowsItem is not null)
            _startWithWindowsItem.IsChecked = StartupRegistration.IsEnabled;
    }

    /// <summary>
    /// Registers the app to start at logon. It does so itself on the first run, because a background
    /// speaker utility that has to be launched by hand is useless; the tray menu turns it off again,
    /// and the marker file is what makes that choice stick. An existing entry is re-pointed at the
    /// executable that is running now, which is what a republish to another folder needs.
    /// </summary>
    private static void EnsureStartWithWindows()
    {
        try
        {
            // A build output is not a stable place to register from; only a published copy does.
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) ||
                path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
                return;

            var marker = Path.Combine(ConfigDirectory, "startup-default-applied");
            if (!File.Exists(marker))
            {
                Directory.CreateDirectory(ConfigDirectory);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                StartupRegistration.Set(true);
                Log("startup: registered to run at logon (first-run default)");
                return;
            }

            if (StartupRegistration.RefreshIfStale())
                Log("startup: run-at-logon path refreshed");
        }
        catch (Exception ex)
        {
            Log($"startup: registration failed: {ex.Message}");
        }
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var show = new System.Windows.Controls.MenuItem { Header = "Volume…" };
        show.Click += (_, _) => ShowPopup();

        var refresh = new System.Windows.Controls.MenuItem { Header = "Find speakers" };
        refresh.Click += (_, _) => Dispatch(() => _viewModel?.RefreshCommand.Execute(null));

        _startWithWindowsItem = new System.Windows.Controls.MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = StartupRegistration.IsEnabled,
        };
        _startWithWindowsItem.Click += (_, _) => StartupRegistration.Set(_startWithWindowsItem.IsChecked);

        var quit = new System.Windows.Controls.MenuItem { Header = "Quit CastBridge" };
        quit.Click += (_, _) =>
        {
            _quitting = true;
            Shutdown();
        };

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(show);
        menu.Items.Add(refresh);
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(quit);

        UseSystemMenuColours(menu);
        return menu;
    }

    /// <summary>
    /// The tray menu is a system surface, not one of the app's dark windows, so it keeps the system
    /// menu colours.
    ///
    /// This matters more than it looks: the app styles TextBlock implicitly in near-white, and WPF
    /// draws a menu header through a TextBlock. Left alone, every item came out near-white on the
    /// system's white menu background, which is to say invisible. An empty TextBlock style here
    /// switches the app's off inside this one menu, and the item foregrounds make the result hold
    /// whatever the theme's template does with inheritance.
    /// </summary>
    private static void UseSystemMenuColours(System.Windows.Controls.ContextMenu menu)
    {
        menu.Background = SystemColors.MenuBrush;
        menu.Foreground = SystemColors.MenuTextBrush;
        menu.BorderBrush = SystemColors.WindowFrameBrush;
        menu.Resources[typeof(System.Windows.Controls.TextBlock)] =
            new Style(typeof(System.Windows.Controls.TextBlock));

        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
            item.Foreground = SystemColors.MenuTextBrush;
    }

    /// <summary>
    /// Loads the icon the tray shows, trying the embedded resources first and the files next to the
    /// executable second, so a packaging change cannot leave the app invisible in the tray.
    /// </summary>
    private static ImageSource? LoadTrayIcon()
    {
        foreach (var uri in CandidateIconUris())
        {
            try
            {
                var image = DecodeIcon(uri);
                if (image is not null)
                    return image;
            }
            catch (Exception)
            {
                Log($"tray icon: {uri} did not load");
            }
        }

        return null;
    }

    private static ImageSource? DecodeIcon(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            // A .ico is not one of the formats BitmapImage understands: it throws "no imaging
            // component suitable" and the tray would show an empty square. The icon decoder reads
            // it, and the frame nearest the notification area's size stays crisp.
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frames = decoder.Frames.OrderBy(f => f.PixelWidth).ToArray();
            var frame = frames.FirstOrDefault(f => f.PixelWidth >= 32) ?? frames.LastOrDefault();
            return frame;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<Uri> CandidateIconUris()
    {
        // The ICO comes first: it is what the notification area is built from, and the frame decoder
        // hands the library a bitmap it accepts as an icon. The PNG is a fallback for a build where
        // the ICO resource is missing.
        yield return new Uri("pack://application:,,,/assets/castbridge.ico", UriKind.Absolute);
        yield return new Uri("pack://application:,,,/assets/castbridge.png", UriKind.Absolute);

        // Next to the executable, in case the resources are ever dropped.
        foreach (var name in new[] { "castbridge.ico", "castbridge.png" })
        {
            var alongside = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(alongside))
                yield return new Uri(alongside, UriKind.Absolute);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Target))
            return;

        // Follow the selected speaker's own changes, so the tooltip shows the live volume rather
        // than the value it happened to have when the speaker was picked.
        if (_tooltipSource is not null)
            _tooltipSource.PropertyChanged -= OnTargetPropertyChanged;

        _tooltipSource = _viewModel?.Target;
        if (_tooltipSource is not null)
            _tooltipSource.PropertyChanged += OnTargetPropertyChanged;

        UpdateTooltip();
    }

    private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.Volume) or nameof(DeviceViewModel.Name))
            UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_tray is null)
            return;

        var target = _viewModel?.Target;
        _tray.ToolTipText = target is null
            ? TrayTooltipIdle
            : string.Format(TrayTooltipReady, target.Name, target.Volume);
    }

    private void ShowPopup()
    {
        if (_window is null)
            return;

        DismissTrayPopup();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SafeInvoke(action);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => SafeInvoke(action)));
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log($"UI update failed: {ex}");
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_trayPopup is not null)
        {
            _trayPopup.Close();
            _trayPopup = null;
        }

        if (_tray is not null)
        {
            _tray.Dispose();
        }

        if (_services is not null)
            await _services.DisposeAsync();

        _instanceMutex?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}