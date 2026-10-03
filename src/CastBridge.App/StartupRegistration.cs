using Microsoft.Win32;

namespace CastBridge.App;

/// <summary>
/// Start-with-Windows, kept deliberately simple: one value under HKCU, which needs no elevation
/// and is removed cleanly if the app is ever deleted.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CastBridge";

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(RegisteredCommand);

    /// <summary>The command line Windows would run at logon, or null when there is no entry.</summary>
    private static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Registers the running executable, or removes the entry when passed false.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path))
                    key.SetValue(ValueName, Quote(path));
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Failing to register for startup is not worth interrupting the user over.
        }
    }

    /// <summary>
    /// Re-points an existing entry at the executable that is running now. Publishing to a different
    /// folder would otherwise leave the entry aimed at a file that is no longer there, which looks
    /// like "it stopped starting with Windows" for no visible reason. True when it changed anything.
    /// </summary>
    public static bool RefreshIfStale()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var registered = RegisteredCommand;
        if (string.IsNullOrWhiteSpace(registered) ||
            string.Equals(registered, Quote(path), StringComparison.OrdinalIgnoreCase))
            return false;

        Set(true);
        return true;
    }

    /// <summary>Windows parses the command line, so a path with spaces has to be quoted.</summary>
    private static string Quote(string path) => $"\"{path}\"";
}