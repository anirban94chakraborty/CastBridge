using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CastBridge.Installer;

/// <summary>What the user, or a command line, asked the installer to do.</summary>
internal sealed class InstallOptions
{
    public string InstallDirectory { get; set; } = InstallerShared.PreferredInstallDirectory();

    public bool StartMenuShortcut { get; set; } = true;

    public bool DesktopShortcut { get; set; } = true;

    public bool StartWithWindows { get; set; } = true;

    public bool LaunchAfterInstall { get; set; } = true;
}

/// <summary>
/// Turns the two embedded files into an installed application. Runs on a background thread, and
/// reports what it is doing so the window can show it.
/// </summary>
internal static class InstallerCore
{
    private const string PayloadResource = "CastBridge.Payload.exe";
    private const string UninstallerResource = "CastBridge.Uninstaller.exe";
    private const int CopyBufferSize = 1024 * 1024;

    public static void Install(InstallOptions options, Action<string, int>? report = null)
    {
        var directory = Path.GetFullPath(options.InstallDirectory);
        var exe = InstallerShared.ExecutablePath(directory);

        InstallerShared.WriteLog(
            "install: " + directory +
            " (startMenu=" + options.StartMenuShortcut +
            " desktop=" + options.DesktopShortcut +
            " startup=" + options.StartWithWindows +
            " launch=" + options.LaunchAfterInstall + ")");

        // The executable is memory mapped while it runs, so an already running CastBridge has to be
        // closed before the file underneath it can be replaced.
        report?.Invoke("Closing CastBridge if it is running", 1);
        InstallerShared.StopRunningApp(directory);
        InstallerShared.RemoveStartupEntryFor(exe);

        Directory.CreateDirectory(directory);

        report?.Invoke("Copying CastBridge.exe", 4);
        var staging = exe + ".new";
        CopyResource(PayloadResource, "CastBridge.exe", staging, 4, 80, report);
        if (File.Exists(exe))
            File.Delete(exe);
        File.Move(staging, exe);
        ClearReadOnly(exe);

        report?.Invoke("Copying the uninstaller", 80);
        var uninstaller = InstallerShared.UninstallerPath(directory);
        CopyResource(UninstallerResource, "the uninstaller", uninstaller, 80, 88, report);
        ClearReadOnly(uninstaller);

        report?.Invoke("Adding shortcuts", 88);
        if (options.StartMenuShortcut)
            InstallerShared.WriteStartMenuShortcuts(directory);
        if (options.DesktopShortcut)
            InstallerShared.WriteDesktopShortcut(directory);

        report?.Invoke("Finishing setup", 94);
        var version = InstallerShared.Version(directory);
        InstallerShared.RecordInstall(directory, version);
        InstallerShared.WriteAddRemovePrograms(directory, version);
        if (options.StartWithWindows)
            InstallerShared.WriteStartupEntry(exe);
        InstallerShared.SyncStartupMarker(options.StartWithWindows);

        report?.Invoke("Installed CastBridge " + version, 100);
        InstallerShared.WriteLog("install finished: " + directory + ", version " + version);
    }

    /// <summary>Starts the installed app. Failure here is not an install failure.</summary>
    public static void Launch(string directory)
    {
        try
        {
            var exe = InstallerShared.ExecutablePath(directory);
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = directory,
                UseShellExecute = true,
            });
            InstallerShared.WriteLog("launched " + exe);
        }
        catch (Exception ex)
        {
            InstallerShared.WriteLog("could not launch CastBridge: " + ex.Message);
        }
    }

    private static void CopyResource(
        string resourceName,
        string label,
        string destination,
        int fromPercent,
        int toPercent,
        Action<string, int>? report)
    {
        using var source = typeof(InstallerCore).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("this setup file is incomplete: " + resourceName + " is missing");

        var total = source.Length;
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);

        var buffer = new byte[CopyBufferSize];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            written += read;

            var span = Math.Max(1L, toPercent - fromPercent);
            report?.Invoke(
                "Copying " + label + "  (" + (written / 1048576.0).ToString("0.0") + " of " + (total / 1048576.0).ToString("0.0") + " MB)",
                fromPercent + (int)(written * span / total));
        }

        if (total == 0)
            throw new InvalidOperationException("this setup file is incomplete: " + resourceName + " is empty");
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                info.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch (Exception)
        {
            // Harmless: the file was just written by us.
        }
    }
}
