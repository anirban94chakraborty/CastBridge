using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CastBridge.Installer;

/// <summary>
/// The uninstaller that Windows runs from Add/Remove Programs, and that the Start menu's
/// "Uninstall CastBridge" shortcut points at. It carries no payload of its own, because it lives
/// inside the very folder it has to delete: the actual removal runs from a copy in %TEMP% once
/// this process is gone, which is the same hand-off every Windows uninstaller makes.
/// </summary>
internal static class UninstallProgram
{
    private const string WorkerSwitch = "--worker";

    [STAThread]
    private static int Main(string[] args)
    {
        InstallerUi.PrepareApplication();

        var workerIndex = Array.IndexOf(args, WorkerSwitch);
        if (workerIndex >= 0)
            return RunWorker(args, workerIndex);

        var directory = InstallerShared.RecordedInstallDirectory() ?? InstallerShared.DefaultInstallDirectory;
        var silent = Has(args, "/s", "-s", "/silent", "/q", "/qn", "--silent");
        var removeSettings = Array.IndexOf(args, "--keep-settings") < 0;

        InstallerShared.WriteLog("uninstall started from " + OwnPath());

        if (!Directory.Exists(directory) && !InstallerShared.IsInstalled())
        {
            InstallerShared.WriteLog("uninstall: nothing installed in " + directory);
            if (!silent)
            {
                MessageBox.Show(
                    "CastBridge does not appear to be installed on this computer.",
                    "Uninstall CastBridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }

        if (silent)
            return Remove(directory, removeSettings);

        using var window = new UninstallForm(directory, removeSettings);
        Application.Run(window);
        return window.ExitCode;
    }

    /// <summary>Used by the window: shows the confirmation, then hands over to the worker.</summary>
    public static int Remove(string directory, bool removeSettings)
    {
        var self = OwnPath();
        var helper = Path.Combine(
            Path.GetTempPath(),
            "CastBridge-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");

        try
        {
            File.Copy(self, helper, overwrite: true);
            InstallerShared.WriteLog("removal helper started; it removes " + directory + " and then this file");
            var worker = Process.Start(new ProcessStartInfo(
                helper,
                WorkerSwitch + " \"" + directory + "\" \"" + self + "\"" + (removeSettings ? " --remove-settings" : " --keep-settings"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });

            if (worker == null)
                throw new InvalidOperationException("the removal helper did not start");

            // The helper does everything except this file, which it cannot delete while we are still
            // running. Waiting for it here keeps that last step in one place, where it is certain to
            // happen once this process is out of the way.
            //
            // Nothing is logged after this wait: the helper may just have deleted the settings folder
            // this log lives in, and writing to it would put it straight back.
            worker.WaitForExit(120000);
            worker.Dispose();

            InstallerShared.ScheduleSelfDelete(self, directory);
            return 0;
        }
        catch (Exception ex)
        {
            InstallerShared.WriteLog("could not start the removal helper: " + ex);
            MessageBox.Show(
                "CastBridge could not start the removal helper." + Environment.NewLine + Environment.NewLine +
                "Run CastBridge-Setup.exe with /uninstall instead, or remove this folder by hand:" +
                Environment.NewLine + Environment.NewLine + directory,
                "Uninstall CastBridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static int RunWorker(string[] args, int workerIndex)
    {
        var directory = Unquote(ArgumentAt(args, workerIndex + 1));
        var stubPath = ArgumentAt(args, workerIndex + 2);
        var removeSettings = Array.IndexOf(args, "--remove-settings") >= 0;

        // The stub that started us is still running and still has its own file open, so that one file
        // is left behind for it to remove as its final step. Everything else goes now.
        var removed = InstallerShared.Uninstall(
            directory,
            removeSettings,
            (message, percent) => InstallerShared.WriteLog("  " + percent + "%  " + message),
            keepFileName: Path.GetFileName(stubPath ?? string.Empty));

        InstallerShared.ScheduleSelfDelete(OwnPath());
        return removed ? 0 : 1;
    }

    private static string OwnPath() => Assembly.GetExecutingAssembly().Location;

    private static string ArgumentAt(string[] args, int index) => index < args.Length ? Unquote(args[index]) : string.Empty;

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? value.Substring(1, value.Length - 2)
            : value;

    private static bool Has(string[] args, params string[] names)
    {
        foreach (var argument in args)
            foreach (var name in names)
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }
}

/// <summary>Asks the one question worth asking before deleting things, then gets out of the way.</summary>
internal sealed class UninstallForm : Form
{
    private readonly string _directory;
    private readonly Icon? _icon;

    private readonly CheckBox _settings = new CheckBox();
    private readonly Label _status = InstallerUi.Caption(string.Empty, 16, 214, 448, 20);
    private readonly ProgressBar _progress = new ProgressBar
    {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 30,
        Visible = false,
    };
    private readonly Button _remove = InstallerUi.ActionButton("Uninstall", true);
    private readonly Button _cancel = InstallerUi.ActionButton("Cancel", false);

    private bool _working;

    /// <summary>What Application.Run should return, since Form.ExitCode is internal.</summary>
    public int ExitCode { get; private set; }

    public UninstallForm(string directory, bool removeSettings)
    {
        _directory = directory;

        InstallerUi.PrepareDialog(this, "Uninstall CastBridge", 480, 306);
        _icon = InstallerUi.ExtractOwnIcon();
        if (_icon != null)
            Icon = _icon;

        var icon = new System.Windows.Forms.PictureBox
        {
            Location = new System.Drawing.Point(16, 18),
            Size = new System.Drawing.Size(32, 32),
        };
        if (_icon != null)
            icon.Image = _icon.ToBitmap();
        Controls.Add(icon);

        Controls.Add(InstallerUi.Caption("Uninstall CastBridge", 60, 18, 360, 26, InstallerUi.TitleFont));
        Controls.Add(InstallerUi.Hint(
            "Version " + InstallerShared.Version(directory), 60, 46, 404, 20));

        Controls.Add(InstallerUi.Caption(
            "CastBridge will be removed from this computer. Its files, shortcuts and logon entry are deleted.",
            16, 80, 448, 40));

        _settings.Text = "Also delete settings and logs";
        _settings.Checked = removeSettings;
        _settings.AutoSize = true;
        _settings.Location = new System.Drawing.Point(16, 126);
        Controls.Add(_settings);

        Controls.Add(InstallerUi.Hint(InstallerShared.SettingsDirectory, 32, 148, 420, 18));
        Controls.Add(InstallerUi.Hint("Installed in " + directory, 16, 178, 448, 34));

        _status.ForeColor = SystemColors.GrayText;
        Controls.Add(_status);

        _progress.Location = new System.Drawing.Point(16, 238);
        _progress.Size = new System.Drawing.Size(448, 14);
        Controls.Add(_progress);

        _cancel.Location = new System.Drawing.Point(248, 262);
        _cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_cancel);

        _remove.Location = new System.Drawing.Point(356, 262);
        _remove.Click += OnRemove;
        Controls.Add(_remove);

        AcceptButton = _remove;
        CancelButton = _cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icon?.Dispose();
        base.Dispose(disposing);
    }

    private async void OnRemove(object? sender, EventArgs e)
    {
        if (_working)
            return;

        _working = true;
        _progress.Visible = true;
        _settings.Enabled = false;
        _remove.Enabled = false;
        _cancel.Enabled = false;
        _remove.Text = "Removing...";
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Removing CastBridge...";

        var code = await Task.Run(() => UninstallProgram.Remove(_directory, _settings.Checked));

        ExitCode = code;
        Close();
    }
}
