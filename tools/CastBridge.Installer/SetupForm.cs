using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CastBridge.Installer;

/// <summary>
/// The window a person sees when they double click the setup file. Everything it does is available
/// on the command line too; this only collects the choices and shows progress.
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly InstallOptions _options;
    private readonly bool _updating;
    private readonly Icon? _icon;

    private readonly TextBox _directory = new TextBox();
    private readonly CheckBox _startMenu = new CheckBox();
    private readonly CheckBox _desktop = new CheckBox();
    private readonly CheckBox _startup = new CheckBox();
    private readonly CheckBox _launch = new CheckBox();
    private readonly Label _status = InstallerUi.Caption(string.Empty, 16, 262, 448, 34);
    private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 100, Visible = false };
    private readonly Button _browse = InstallerUi.ActionButton("Browse...", false);
    private readonly Button _cancel = InstallerUi.ActionButton("Cancel", false);
    private readonly Button _install = InstallerUi.ActionButton("Install", true);

    private bool _working;
    private bool _installed;

    /// <summary>What Application.Run should return, since Form.ExitCode is internal.</summary>
    public int ExitCode { get; set; }

    public SetupForm(SetupOptions options)
    {
        _options = options.Install;
        _updating = InstallerShared.IsInstalled();

        InstallerUi.PrepareDialog(this, "CastBridge Setup", 480, 380);
        _icon = InstallerUi.ExtractOwnIcon();
        if (_icon != null)
            Icon = _icon;

        // Reuse whatever the command line asked for, otherwise prefill the previous install.
        _options.InstallDirectory = options.Install.InstallDirectory;
        _directory.Text = _options.InstallDirectory;

        var icon = new PictureBox
        {
            Location = new Point(16, 18),
            Size = new Size(32, 32),
        };
        if (_icon != null)
            icon.Image = _icon.ToBitmap();
        Controls.Add(icon);

        Controls.Add(InstallerUi.Caption(
            _updating ? "Update CastBridge" : "Install CastBridge", 60, 18, 360, 26, InstallerUi.TitleFont));
        Controls.Add(InstallerUi.Hint(
            "Version " + InstallerShared.OwnVersion() + "  ·  installs for you only, no administrator rights needed",
            60, 46, 404, 20));

        Controls.Add(InstallerUi.Caption("Install to", 16, 80, 200, 18));
        _directory.Location = new Point(16, 100);
        _directory.Size = new Size(354, 23);
        Controls.Add(_directory);

        _browse.Location = new Point(380, 98);
        _browse.Size = new Size(84, 27);
        _browse.Click += OnBrowse;
        Controls.Add(_browse);

        var group = new GroupBox { Text = "Options", Location = new Point(16, 136), Size = new Size(448, 116) };
        AddOption(group, _startMenu, "Add a Start menu shortcut", _options.StartMenuShortcut, 24);
        AddOption(group, _desktop, "Add a desktop shortcut", _options.DesktopShortcut, 47);
        AddOption(group, _startup, "Start CastBridge when Windows signs in", _options.StartWithWindows, 70);
        AddOption(group, _launch, "Run CastBridge now", _options.LaunchAfterInstall, 93);
        Controls.Add(group);

        _status.ForeColor = SystemColors.GrayText;
        Controls.Add(_status);

        _progress.Location = new Point(16, 300);
        _progress.Size = new Size(448, 14);
        Controls.Add(_progress);

        _cancel.Location = new Point(248, 332);
        _cancel.DialogResult = DialogResult.Cancel;
        // DialogResult on its own closes nothing here: this window is shown by Application.Run, not
        // ShowDialog, and a non-modal form only stores the value. The click handler is what closes
        // it, and CancelButton routes Escape through the same path.
        _cancel.Click += OnCancel;
        Controls.Add(_cancel);

        _install.Location = new Point(356, 332);
        _install.Click += OnInstall;
        Controls.Add(_install);

        _status.Text = _updating
            ? "CastBridge is already installed. Installing again replaces it and keeps your settings."
            : "CastBridge lives in the notification area. Windows 11 may file its icon away in the overflow menu.";

        AcceptButton = _install;
        CancelButton = _cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icon?.Dispose();
        base.Dispose(disposing);
    }

    private static void AddOption(GroupBox group, CheckBox box, string text, bool isChecked, int top)
    {
        box.Text = text;
        box.Checked = isChecked;
        box.AutoSize = true;
        box.Location = new Point(14, top);
        group.Controls.Add(box);
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder CastBridge should be installed in.",
            SelectedPath = SafeExistingDirectory(_directory.Text),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _directory.Text = dialog.SelectedPath;
    }

    private static string SafeExistingDirectory(string candidate)
    {
        try
        {
            return Directory.Exists(candidate) ? candidate : InstallerShared.DefaultInstallDirectory;
        }
        catch (Exception)
        {
            return InstallerShared.DefaultInstallDirectory;
        }
    }

    private async void OnInstall(object? sender, EventArgs e)
    {
        if (_installed)
        {
            Close();
            return;
        }

        if (_working)
            return;

        string directory;
        try
        {
            directory = Path.GetFullPath(_directory.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CastBridge Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _options.InstallDirectory = directory;
        _options.StartMenuShortcut = _startMenu.Checked;
        _options.DesktopShortcut = _desktop.Checked;
        _options.StartWithWindows = _startup.Checked;
        _options.LaunchAfterInstall = _launch.Checked;

        BeginWork();

        var failure = (Exception?)null;
        try
        {
            await Task.Run(() => InstallerCore.Install(_options, Report));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure == null)
        {
            ShowFinished();
            var version = InstallerShared.Version(directory);
            _status.Text = "CastBridge " + version + " is installed. It runs in the notification area; if the icon is missing, look in the overflow menu next to the clock.";
            _installed = true;
            ExitCode = 0;

            if (_options.LaunchAfterInstall)
                InstallerCore.Launch(directory);
        }
        else
        {
            InstallerShared.WriteLog("install failed: " + failure);
            _status.Text = "CastBridge could not be installed.";
            ExitCode = 1;
            EndWork();
            MessageBox.Show(
                this,
                failure.Message + Environment.NewLine + Environment.NewLine + "Details: " + InstallerShared.LogPath,
                "CastBridge Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnCancel(object? sender, EventArgs e)
    {
        if (!_working)
            Close();
    }

    /// <summary>Called from the install thread for every step and every megabyte.</summary>
    private void Report(string message, int percent)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                _status.ForeColor = SystemColors.ControlText;
                _status.Text = message;
                _progress.Value = Math.Min(_progress.Maximum, Math.Max(_progress.Minimum, percent));
            }));
        }
        catch (Exception)
        {
            // The window is on its way out; nothing left to report to.
        }
    }

    private void BeginWork()
    {
        _working = true;
        _progress.Visible = true;
        _progress.Value = 0;
        _status.ForeColor = SystemColors.ControlText;
        _directory.Enabled = false;
        _browse.Enabled = false;
        _startMenu.Enabled = false;
        _desktop.Enabled = false;
        _startup.Enabled = false;
        _launch.Enabled = false;
        _cancel.Enabled = false;
        _install.Enabled = false;
        _install.Text = "Installing...";
        ControlBox = false;
    }

    private void EndWork()
    {
        _working = false;
        _progress.Visible = false;
        _directory.Enabled = true;
        _browse.Enabled = true;
        _startMenu.Enabled = true;
        _desktop.Enabled = true;
        _startup.Enabled = true;
        _launch.Enabled = true;
        _cancel.Enabled = true;
        _cancel.Visible = true;
        _install.Enabled = true;
        _install.Text = "Install";
        ControlBox = true;
    }

    /// <summary>
    /// Nothing is being installed any more, so this is just a window with one button on it. The
    /// inputs stay disabled to say so, and Cancel goes away rather than sitting there greyed out
    /// saying it would cancel an install that has already finished.
    /// </summary>
    private void ShowFinished()
    {
        _working = false;
        _progress.Visible = false;
        _install.Enabled = true;
        _install.Text = "Close";
        _cancel.Visible = false;
        ControlBox = true;
    }
}
