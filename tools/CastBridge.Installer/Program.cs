using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CastBridge.Installer;

internal enum InstallerMode
{
    Install,
    Uninstall,
}

internal sealed class SetupOptions
{
    public InstallerMode Mode = InstallerMode.Install;

    public bool Silent;

    public bool ShowHelp;

    public bool RemoveSettings = true;

    public InstallOptions Install { get; } = new InstallOptions();

    /// <summary>
    /// Command line for sharing scripts and for testing: /S installs or uninstalls with the
    /// defaults, /D="folder" picks the folder, and the /no-* switches turn the optional steps off.
    /// </summary>
    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var argument = raw.Trim();
            var name = argument;
            string? value = null;

            var equals = argument.IndexOf('=');
            if (equals > 0)
            {
                name = argument.Substring(0, equals);
                value = Unquote(argument.Substring(equals + 1));
            }

            switch (name.ToLowerInvariant())
            {
                case "/?":
                case "-h":
                case "--help":
                case "/help":
                    options.ShowHelp = true;
                    break;

                case "/s":
                case "-s":
                case "/silent":
                case "/q":
                case "/qn":
                case "--silent":
                    options.Silent = true;
                    break;

                case "/uninstall":
                case "--uninstall":
                case "/remove":
                    options.Mode = InstallerMode.Uninstall;
                    break;

                case "/d":
                case "--dir":
                case "--install-dir":
                    if (!string.IsNullOrWhiteSpace(value))
                        options.Install.InstallDirectory = value!;
                    break;

                case "/no-shortcuts":
                case "--no-shortcuts":
                    options.Install.StartMenuShortcut = false;
                    options.Install.DesktopShortcut = false;
                    break;

                case "/no-desktop":
                case "--no-desktop":
                    options.Install.DesktopShortcut = false;
                    break;

                case "/no-startup":
                case "--no-startup":
                    options.Install.StartWithWindows = false;
                    break;

                case "/launch":
                case "--launch":
                    options.Install.LaunchAfterInstall = true;
                    break;

                case "/no-launch":
                case "--no-launch":
                    options.Install.LaunchAfterInstall = false;
                    break;

                case "--remove-settings":
                case "/remove-settings":
                    options.RemoveSettings = true;
                    break;

                case "--keep-settings":
                case "/keep-settings":
                    options.RemoveSettings = false;
                    break;

                default:
                    // Unknown switches are ignored rather than failing the install; a wrapper
                    // script passing the wrong flag should not leave a half installed app.
                    break;
            }
        }

        return options;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;
}

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        InstallerUi.PrepareApplication();

        var options = SetupOptions.Parse(args);
        if (options.ShowHelp)
        {
            ShowUsage();
            return 0;
        }

        InstallerShared.WriteLog(
            "setup " + InstallerShared.OwnVersion() + " started (" + options.Mode + (options.Silent ? ", silent" : string.Empty) + ")");

        try
        {
            return options.Mode == InstallerMode.Uninstall
                ? RunUninstall(options)
                : RunInstall(options);
        }
        catch (Exception ex)
        {
            InstallerShared.WriteLog("setup failed: " + ex);
            MessageBox.Show(
                "CastBridge could not be " + (options.Mode == InstallerMode.Uninstall ? "removed" : "installed") +
                Environment.NewLine + Environment.NewLine + ex.Message +
                Environment.NewLine + Environment.NewLine + "Details: " + InstallerShared.LogPath,
                "CastBridge Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunInstall(SetupOptions options)
    {
        if (options.Silent)
            AttachToParentConsole();

        if (!options.Silent)
        {
            InstallerShared.WriteLog("showing the install window");
            using var window = new SetupForm(options);
            InstallerShared.WriteLog("install window created, running it");
            Application.Run(window);
            InstallerShared.WriteLog("install window closed, exit code " + window.ExitCode);
            return window.ExitCode;
        }

        InstallerCore.Install(options.Install, ReportToConsole);
        if (options.Install.LaunchAfterInstall)
            InstallerCore.Launch(Path.GetFullPath(options.Install.InstallDirectory));

        Console.WriteLine("CastBridge installed in " + Path.GetFullPath(options.Install.InstallDirectory));
        return 0;
    }

    private static int RunUninstall(SetupOptions options)
    {
        if (options.Silent)
            AttachToParentConsole();

        var directory = InstallerShared.RecordedInstallDirectory();
        if (string.IsNullOrEmpty(directory))
            directory = InstallerShared.DefaultInstallDirectory;

        if (!Directory.Exists(directory) && !InstallerShared.IsInstalled())
        {
            Console.WriteLine("CastBridge is not installed in " + directory + ".");
            return 0;
        }

        if (!options.Silent)
        {
            var answer = MessageBox.Show(
                "Remove CastBridge from this computer?" + Environment.NewLine + Environment.NewLine +
                "Its files, shortcuts and logon entry are deleted." + Environment.NewLine +
                "Settings and logs in " + InstallerShared.SettingsDirectory +
                (options.RemoveSettings ? " are deleted as well." : " are kept."),
                "Uninstall CastBridge",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return 2;
        }

        // Safe to do the removal here: the setup file lives wherever the user downloaded it, not
        // inside the folder it is deleting.
        var removed = InstallerShared.Uninstall(directory!, options.RemoveSettings, ReportToConsole);
        Console.WriteLine(removed
            ? "CastBridge removed from " + directory + "."
            : "CastBridge could not be fully removed from " + directory + ", see " + InstallerShared.LogPath + ".");
        return removed ? 0 : 1;
    }

    private static void ReportToConsole(string message, int percent)
    {
        try
        {
            Console.WriteLine("  " + percent.ToString("0") + "%  " + message);
        }
        catch (IOException)
        {
            // No console attached: the log file already has everything.
        }
    }

    /// <summary>
    /// A silent install is usually started from a script, so its output goes to the console that
    /// started it rather than nowhere at all.
    /// </summary>
    private static void AttachToParentConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch (Exception)
        {
            // Started from Explorer, with no console to attach to. That is the normal case.
        }
    }

    private static void ShowUsage()
    {
        MessageBox.Show(
            "CastBridge Setup" + Environment.NewLine + Environment.NewLine +
            "  CastBridge-Setup.exe                install, with a window" + Environment.NewLine +
            "  CastBridge-Setup.exe /S             install silently" + Environment.NewLine +
            "  CastBridge-Setup.exe /D=\"folder\"    install into a chosen folder" + Environment.NewLine +
            "  CastBridge-Setup.exe /no-startup    do not start with Windows" + Environment.NewLine +
            "  CastBridge-Setup.exe /no-launch     do not run the app afterwards" + Environment.NewLine +
            "  CastBridge-Setup.exe /uninstall     remove CastBridge again",
            "CastBridge Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
