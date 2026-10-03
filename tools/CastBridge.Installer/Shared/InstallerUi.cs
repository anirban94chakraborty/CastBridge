using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CastBridge.Installer;

/// <summary>
/// The few pieces of look and feel both installer executables share, so the setup window and the
/// uninstall window look like they came from the same place. Windows' own colours do the work; the
/// only brand colour is the blue the CastBridge icon is built from.
/// </summary>
internal static class InstallerUi
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);

    public static Font BodyFont { get; } = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

    public static Font TitleFont { get; } = new Font("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point);

    public static Font HintFont { get; } = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>
    /// Must run before any window exists. .NET Framework has no high DPI mode switch, so the
    /// process level call is what keeps the dialog sharp on a scaled display.
    /// </summary>
    public static void PrepareApplication()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>Configures a dialog so WinForms scales the fixed layout for the current display.</summary>
    public static void PrepareDialog(Form form, string title, int width, int height)
    {
        form.Text = title;
        form.Font = BodyFont;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ClientSize = new Size(width, height);
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    /// <summary>The 32 pixel icon embedded in this executable, or null if the shell cannot hand it over.</summary>
    public static Icon? ExtractOwnIcon()
    {
        try
        {
            var location = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(location) && System.IO.File.Exists(location))
                return Icon.ExtractAssociatedIcon(location);
        }
        catch (Exception)
        {
            // A missing window icon is never worth failing over.
        }

        return null;
    }

    public static Label Caption(string text, int x, int y, int width, int height, Font? font = null)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Font = font ?? BodyFont,
            ForeColor = SystemColors.ControlText,
        };
    }

    public static Label Hint(string text, int x, int y, int width, int height)
    {
        var label = Caption(text, x, y, width, height, HintFont);
        label.ForeColor = SystemColors.GrayText;
        return label;
    }

    /// <summary>A flat button in the app's accent colour, or a plain one for the secondary action.</summary>
    public static Button ActionButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(112, 30),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = primary ? Accent : SystemColors.Control,
            ForeColor = primary ? Color.White : SystemColors.ControlText,
            FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = SystemColors.ControlDark },
            Cursor = Cursors.Hand,
        };

        return button;
    }
}
