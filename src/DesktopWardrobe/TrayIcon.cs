using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopWardrobe;

/// <summary>System tray icon with the app's menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DesktopWardrobe";

    private readonly NotifyIcon _icon;

    public TrayIcon(Wardrobe wardrobe, Action exit)
    {
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startWithWindows.CheckedChanged += (_, _) => SetStartup(startWithWindows.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open wardrobe folder", null, (_, _) => Shell.Open(wardrobe.StoragePath));
        menu.Items.Add("Show in Open/Upload dialogs (pin to Quick access)", null, (_, _) =>
        {
            try
            {
                Shell.PinToQuickAccess(wardrobe.StoragePath);
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't pin the folder: {ex.Message}");
            }
        });
        menu.Items.Add("Put everything back on the desktop", null, (_, _) => wardrobe.ReturnAll());
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Desktop Wardrobe",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.ShowBalloonTip(5000, "Desktop Wardrobe",
            "Push the mouse to the top of the screen to open the wardrobe. Drag folders up there to hang them.",
            ToolTipIcon.None);
    }

    public void ShowError(string message) =>
        _icon.ShowBalloonTip(5000, "Desktop Wardrobe", message, ToolTipIcon.Warning);

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, false);
    }

    /// <summary>Draws a folder pegged to a sagging line for the tray.</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var rope = new Pen(Color.FromArgb(196, 154, 94), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(rope, 1, 5, 11, 13, 21, 13, 31, 5);

            using var folder = new SolidBrush(Color.FromArgb(244, 196, 48));
            g.FillRectangle(folder, 7, 14, 18, 14);

            using var wood = new SolidBrush(Color.FromArgb(214, 176, 120));
            using var edge = new Pen(Color.FromArgb(138, 99, 52), 1f);
            g.FillRectangle(wood, 13, 4, 6, 15);
            g.DrawRectangle(edge, 13, 4, 6, 15);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
