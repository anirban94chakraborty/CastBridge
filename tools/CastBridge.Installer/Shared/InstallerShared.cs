using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CastBridge.Installer;

/// <summary>
/// Everything the setup executable and the uninstaller stub have to agree on: where things live,
/// what the registry looks like, how a shortcut is written, and how a running app is stopped.
///
/// Both projects compile this file, so the two halves of the installer cannot drift apart.
/// </summary>
internal static class InstallerShared
{
    public const string AppName = "CastBridge";
    public const string ExeName = "CastBridge.exe";
    public const string UninstallerName = "CastBridge-Uninstall.exe";
    public const string Vendor = "CastBridge";
    public const string Description = "Control Cast speakers on your local network.";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CastBridge";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CastBridge";
    private const string RecordKeyPath = @"Software\CastBridge\Install";

    /// <summary>
    /// The app marks its own first run by dropping this file next to its settings, and then never
    /// registers for startup again. The installer writes it too, so that unchecking "start with
    /// Windows" here is not quietly undone by the app on its very first launch.
    /// </summary>
    private const string StartupDefaultMarkerName = "startup-default-applied";

    private const long MaxLogBytes = 256 * 1024;

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string LogPath => Path.Combine(SettingsDirectory, "installer.log");

    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string StartMenuDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static string ExecutablePath(string directory) => Path.Combine(directory, ExeName);

    public static string UninstallerPath(string directory) => Path.Combine(directory, UninstallerName);

    /// <summary>Windows parses the command line, so a path with spaces has to be quoted.</summary>
    private static string Quote(string path) => "\"" + path + "\"";

    // ---------------------------------------------------------------- logging

    /// <summary>
    /// Appends to the installer's own log next to the app log. Installation happens with no console
    /// and often with no visible window, so this file is the only place that can say what went wrong.
    /// </summary>
    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Delete(LogPath);

            File.AppendAllText(
                LogPath,
                string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}{2}",
                    DateTime.Now, message, Environment.NewLine),
                Encoding.UTF8);
        }
        catch (Exception)
        {
            // Logging must never be the reason an install fails.
        }
    }

    // --------------------------------------------------------- install record

    public static string? RecordedInstallDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RecordKeyPath, writable: false);
            return key?.GetValue("InstallDir") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsInstalled() => !string.IsNullOrEmpty(RecordedInstallDirectory());

    /// <summary>Where a fresh install should go: the folder a previous install used, if any.</summary>
    public static string PreferredInstallDirectory()
    {
        var recorded = RecordedInstallDirectory();
        return string.IsNullOrEmpty(recorded) ? DefaultInstallDirectory : recorded!;
    }

    public static void RecordInstall(string directory, string version)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RecordKeyPath, writable: true);
            key.SetValue("InstallDir", directory, RegistryValueKind.String);
            key.SetValue("Version", version, RegistryValueKind.String);
            key.SetValue("InstalledUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            WriteLog("could not record the install location: " + ex.Message);
        }
    }

    public static void RemoveInstallRecord() => DeleteKey(RecordKeyPath);

    // ------------------------------------------------------- startup handling

    /// <summary>Writes the logon entry in exactly the shape CastBridge.App.StartupRegistration uses.</summary>
    public static void WriteStartupEntry(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(RunValueName, Quote(executablePath), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            WriteLog("could not register for startup: " + ex.Message);
        }
    }

    /// <summary>
    /// Removes the logon entry only when it points at the given executable. A user who has pointed
    /// it somewhere else on purpose keeps their choice.
    /// </summary>
    public static void RemoveStartupEntryFor(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
                return;

            var current = key.GetValue(RunValueName) as string;
            if (string.IsNullOrWhiteSpace(current))
                return;

            var registered = current!.Trim();
            if (!Unquote(registered).Equals(executablePath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                WriteLog("startup entry points somewhere else, left alone: " + current);
                return;
            }

            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            WriteLog("could not remove the startup entry: " + ex.Message);
        }
    }

    /// <summary>
    /// Keeps the app's own first-run default in step with what was chosen here: with the marker file
    /// present it stays out of the way, without it the app keeps registering itself as it always has.
    /// </summary>
    public static void SyncStartupMarker(bool startWithWindows)
    {
        try
        {
            if (startWithWindows)
            {
                if (File.Exists(StartupDefaultMarkerPath))
                    File.Delete(StartupDefaultMarkerPath);
            }
            else
            {
                Directory.CreateDirectory(SettingsDirectory);
                if (!File.Exists(StartupDefaultMarkerPath))
                    File.WriteAllText(StartupDefaultMarkerPath, "The installer was run with start-with-Windows turned off.");
            }
        }
        catch (Exception ex)
        {
            WriteLog("could not update the startup marker: " + ex.Message);
        }
    }

    private static string StartupDefaultMarkerPath => Path.Combine(SettingsDirectory, StartupDefaultMarkerName);

    // --------------------------------------------------- add / remove programs

    public static void WriteAddRemovePrograms(string installDirectory, string version)
    {
        var exe = ExecutablePath(installDirectory);
        var uninstaller = UninstallerPath(installDirectory);

        try
        {
            long bytes = 0;
            foreach (var file in new[] { exe, uninstaller })
                if (File.Exists(file))
                    bytes += new FileInfo(file).Length;

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true);
            key.SetValue("DisplayName", AppName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", Vendor, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Quote(exe) + ",0", RegistryValueKind.String);
            key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            key.SetValue("UninstallString", Quote(uninstaller), RegistryValueKind.String);
            key.SetValue("QuietUninstallString", Quote(uninstaller) + " /silent", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            WriteLog("could not write the Add/Remove Programs entry: " + ex.Message);
        }
    }

    public static void RemoveAddRemovePrograms() => DeleteKey(UninstallKeyPath);

    private static void DeleteKey(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            WriteLog("could not delete registry key " + path + ": " + ex.Message);
        }
    }

    // ----------------------------------------------------------------- process

    /// <summary>
    /// Stops CastBridge, but only the copy running from this install folder. The executable is
    /// memory mapped while it runs, so it has to be closed before it can be replaced or deleted.
    /// Returns how many processes were stopped.
    /// </summary>
    public static int StopRunningApp(string installDirectory)
    {
        var target = ExecutablePath(Path.GetFullPath(installDirectory));
        var stopped = 0;

        foreach (var process in Process.GetProcessesByName(AppName))
        {
            try
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Runs as another user, or exited between the two calls: not ours to touch.
                    continue;
                }

                if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                    continue;

                process.Kill();
                if (!process.WaitForExit(5000))
                    process.Kill();
                stopped++;
                WriteLog("stopped the running CastBridge (" + stopped + ")");
            }
            catch (Exception ex)
            {
                WriteLog("could not stop CastBridge: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        return stopped;
    }

    // --------------------------------------------------------------- shortcuts

    public static bool WriteStartMenuShortcuts(string installDirectory)
    {
        var exe = ExecutablePath(installDirectory);
        var uninstaller = UninstallerPath(installDirectory);

        try
        {
            Directory.CreateDirectory(StartMenuDirectory);
            return WriteShortcut(
                Path.Combine(StartMenuDirectory, AppName + ".lnk"),
                exe,
                installDirectory,
                Description + " (" + AppName + " " + OwnVersion() + ")",
                Quote(exe) + ",0")
                & WriteShortcut(
                    Path.Combine(StartMenuDirectory, "Uninstall " + AppName + ".lnk"),
                    uninstaller,
                    installDirectory,
                    "Remove " + AppName + " from this computer",
                    Quote(uninstaller) + ",0");
        }
        catch (Exception ex)
        {
            WriteLog("could not create the Start menu shortcuts: " + ex.Message);
            return false;
        }
    }

    public static bool WriteDesktopShortcut(string installDirectory)
    {
        var exe = ExecutablePath(installDirectory);
        try
        {
            return WriteShortcut(
                DesktopShortcutPath,
                exe,
                installDirectory,
                Description + " (" + AppName + " " + OwnVersion() + ")",
                Quote(exe) + ",0");
        }
        catch (Exception ex)
        {
            WriteLog("could not create the desktop shortcut: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes a .lnk through WScript.Shell, the same way the rest of the project's tooling does.
    /// Reflection instead of an interop reference keeps the setup a single file with no extra
    /// assemblies next to it. A missing shell object is not worth failing an install over.
    /// </summary>
    private static bool WriteShortcut(string linkPath, string target, string workingDirectory, string description, string iconLocation)
    {
        object? shell = null;
        object? link = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                WriteLog("WScript.Shell is unavailable, skipping shortcut " + linkPath);
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return false;

            link = Call(shell, "CreateShortcut", linkPath) ?? throw new InvalidOperationException("no shortcut object");
            Set(link, "TargetPath", target);
            Set(link, "WorkingDirectory", workingDirectory);
            Set(link, "Description", description);
            Set(link, "IconLocation", iconLocation);
            Call(link, "Save");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("could not write shortcut " + linkPath + ": " + ex.Message);
            return false;
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    private static object Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, binder: null, target, arguments)!;

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, binder: null, target, new[] { value });

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            WriteLog("could not delete " + path + ": " + ex.Message);
        }
    }

    public static void RemoveShortcuts(string installDirectory)
    {
        var directory = Path.GetFullPath(installDirectory);
        DeleteShortcutIfItPointsHere(directory, DesktopShortcutPath);
        // Older layouts, and anything a user has dragged around by hand.
        DeleteShortcutIfItPointsHere(directory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));

        try
        {
            if (!Directory.Exists(StartMenuDirectory))
                return;

            // The folder is CastBridge's, but it only goes with us if it holds nothing else: a user
            // is allowed to drop a shortcut of their own in there.
            foreach (var link in Directory.GetFiles(StartMenuDirectory, "*.lnk"))
            {
                if (!PointsInto(ShortcutTarget(link), directory))
                {
                    WriteLog("Start menu folder holds a shortcut of the user's own, kept: " + link);
                    return;
                }
            }

            TryDeleteDirectory(StartMenuDirectory, out _);
        }
        catch (Exception ex)
        {
            WriteLog("could not clean up the Start menu folder: " + ex.Message);
        }
    }

    /// <summary>Reads a shortcut's target, or null when it is missing or unreadable.</summary>
    private static string? ShortcutTarget(string linkPath)
    {
        object? shell = null;
        object? link = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null || !File.Exists(linkPath))
                return null;

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return null;

            link = Call(shell, "CreateShortcut", linkPath);
            return link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, binder: null, link, null) as string;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Release(link);
            Release(shell);
        }
    }

    /// <summary>
    /// Deletes a shortcut only when it points inside the install folder. The desktop shortcut from
    /// the portable build in this repository points at dist\CastBridge.exe, and an uninstall must
    /// not take that away from someone who never installed anything.
    /// </summary>
    private static void DeleteShortcutIfItPointsHere(string installDirectory, string linkPath)
    {
        var target = ShortcutTarget(linkPath);
        if (target == null)
            return;

        if (!PointsInto(target, installDirectory))
        {
            WriteLog("shortcut left alone, it points elsewhere: " + linkPath);
            return;
        }

        DeleteIfPresent(linkPath);
    }

    private static bool PointsInto(string? target, string installDirectory)
    {
        try
        {
            return target != null && Path.GetFullPath(target)
                .StartsWith(installDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ file system

    /// <summary>
    /// Deletes a directory, retrying briefly: Windows keeps a freshly exited executable locked for a
    /// moment, and one retry loop handles that far more reliably than a single attempt.
    /// keepFileName leaves one file behind, which is how the uninstaller stub gets rid of itself:
    /// it cannot delete a file it is running from, so it keeps that one step for last.
    /// </summary>
    public static bool TryDeleteDirectory(string path, out string error, string? keepFileName = null)
    {
        error = string.Empty;

        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (!Directory.Exists(path))
                return true;

            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (keepFileName != null && string.Equals(Path.GetFileName(file), keepFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DeleteFileQuietly(file);
                }

                // Deepest first, so the tree empties out from the bottom up.
                foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(directory => directory.Length)
                             .ToArray())
                {
                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory);
                }

                error = string.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var leftovers = Directory.Exists(path)
                ? Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            if (leftovers.Length == 0)
            {
                try
                {
                    Directory.Delete(path);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }
            else if (keepFileName != null && leftovers.All(entry => IsKeeper(entry, keepFileName)))
            {
                // Nothing left for us to do: whoever owns that file removes it as its final step.
                WriteLog(path + " now holds only " + keepFileName + ", which the caller removes last");
                return true;
            }

            Thread.Sleep(400);
        }

        WriteLog("could not delete " + path + ": " + error);
        return false;
    }

    private static bool IsKeeper(string path, string keepFileName) =>
        string.Equals(Path.GetFileName(path), keepFileName, StringComparison.OrdinalIgnoreCase);

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && (info.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                info.Attributes &= ~FileAttributes.ReadOnly;

            File.Delete(path);
        }
        catch (Exception)
        {
            // Locked or in use: the retry loop around this gets another go.
        }
    }

    /// <summary>
    /// Hands our own file to a short lived cmd.exe so it can be deleted after this process exits. A
    /// running executable cannot remove itself, which is the last trick an uninstaller needs; the
    /// folder it sat in goes in the same breath, because that is empty by then.
    /// </summary>
    public static void ScheduleSelfDelete(string executablePath, string? folderToRemove = null)
    {
        try
        {
            var command = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + executablePath + "\"";
            if (!string.IsNullOrEmpty(folderToRemove))
                command += " & rmdir /s /q \"" + folderToRemove + "\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            WriteLog("could not schedule the removal of " + executablePath + ": " + ex.Message);
        }
    }

    // -------------------------------------------------------------- uninstall

    /// <summary>
    /// Removes everything this installer added: the app, its shortcuts, its logon entry and its
    /// Add/Remove Programs entry. Settings and logs are only removed when asked for, because that
    /// is the one thing a user cannot get back.
    /// </summary>
    public static bool Uninstall(string installDirectory, bool removeSettings, Action<string, int>? report, string? keepFileName = null)
    {
        var directory = Path.GetFullPath(installDirectory);
        WriteLog("uninstall: " + directory + (removeSettings ? " (with settings)" : " (keeping settings)"));

        report?.Invoke("Stopping CastBridge", 10);
        StopRunningApp(directory);

        report?.Invoke("Removing shortcuts", 30);
        RemoveShortcuts(directory);

        report?.Invoke("Removing the logon entry", 50);
        RemoveStartupEntryFor(ExecutablePath(directory));
        RemoveStartupEntryFor(UninstallerPath(directory));

        report?.Invoke("Removing the program list entry", 65);
        RemoveAddRemovePrograms();
        RemoveInstallRecord();

        report?.Invoke("Deleting program files", 80);
        var ok = TryDeleteDirectory(directory, out _, keepFileName);

        // Everything that writes anything, including these last two lines, happens before the
        // settings go: the log lives in that folder, and one more line would put it straight back.
        report?.Invoke("Done", 100);
        WriteLog("uninstall finished, removed=" + ok);

        if (removeSettings)
            TryDeleteDirectory(SettingsDirectory, out _);

        return ok;
    }

    /// <summary>The version Windows will show in Apps &amp; features, taken from the installed exe.</summary>
    public static string Version(string installDirectory)
    {
        try
        {
            var exe = ExecutablePath(installDirectory);
            if (File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!string.IsNullOrWhiteSpace(info))
                    return info.Trim();
            }
        }
        catch (Exception)
        {
            // Fall through to the setup's own version.
        }

        return OwnVersion();
    }

    /// <summary>This executable's own version, shown in the installer window before anything is copied.</summary>
    public static string OwnVersion()
    {
        try
        {
            var location = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                var info = FileVersionInfo.GetVersionInfo(location).ProductVersion;
                if (!string.IsNullOrWhiteSpace(info))
                    return info.Trim();
            }
        }
        catch (Exception)
        {
            // Fall through.
        }

        return "1.0";
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;
}
