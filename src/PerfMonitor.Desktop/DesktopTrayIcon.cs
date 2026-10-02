using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PerfMonitor.Desktop;

internal interface IDesktopTray : IDisposable
{
    void Update(bool windowVisible, string? status);
}

internal sealed class DesktopTrayIcon : IDesktopTray
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _status;

    internal static IDesktopTray? Create(Action toggle, Action exit) =>
        FindWindow("Shell_TrayWnd", null) == IntPtr.Zero ? null : new DesktopTrayIcon(toggle, exit);

    private DesktopTrayIcon(Action toggle, Action exit)
    {
        _image = DesktopAppIcon.LoadTrayIcon();
        _menu = new Forms.ContextMenuStrip();
        _toggle = new Forms.ToolStripMenuItem("显示监控");
        _toggle.Click += (_, _) => toggle();
        _status = new Forms.ToolStripMenuItem("Ctrl+Alt+P 呼出 / 收起") { Enabled = false };
        var exitItem = new Forms.ToolStripMenuItem("退出 PerfMonitor");
        exitItem.Click += (_, _) => exit();
        _menu.Items.AddRange([_toggle, _status, new Forms.ToolStripSeparator(), exitItem]);
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "PerfMonitor · Ctrl+Alt+P 呼出 / 收起",
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) toggle();
        };
        try { _icon.Visible = true; }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(bool windowVisible, string? status)
    {
        _toggle.Text = windowVisible ? "收起监控" : "显示监控";
        if (status is not null)
        {
            _status.Text = status.Contains("占用", StringComparison.Ordinal)
                ? "Ctrl+Alt+P 已被占用"
                : status.Contains("不可用", StringComparison.Ordinal)
                    ? "全局快捷键不可用"
                    : "Ctrl+Alt+P 呼出 / 收起";
            _status.ToolTipText = status;
            // NotifyIcon.Text is capped at 63 characters on older Windows versions.
            _icon.Text = status.Contains("占用", StringComparison.Ordinal)
                ? "PerfMonitor · 快捷键已被占用，请从托盘呼出"
                : status.Contains("不可用", StringComparison.Ordinal)
                    ? "PerfMonitor · 快捷键不可用，请从托盘呼出"
                    : "PerfMonitor · Ctrl+Alt+P 呼出 / 收起";
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
