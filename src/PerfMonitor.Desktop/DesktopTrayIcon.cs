using System.Runtime.InteropServices;
using PerfMonitor.Contracts;
using Forms = System.Windows.Forms;

namespace PerfMonitor.Desktop;

internal interface IDesktopTray : IDisposable
{
    void Update(bool windowVisible, string? status);
    // Default members keep lifecycle-only tray implementations usable without notification support.
    bool NotificationsAvailable => false;
    void ConfigureNotifications(Action openDiagnostics, Action toggleEnabled,
        Action<TimeSpan?> pause, Action endPause) { }
    void UpdateNotifications(DesktopNotificationState state) { }
    bool TryShowNotification(DesktopDiagnosticNotification notification) => false;
}

internal sealed class DesktopTrayIcon : IDesktopTray
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _notifications = new("诊断通知已关闭 · 本次运行");
    private readonly Forms.ToolStripMenuItem _notificationToggle = new("开启诊断通知");
    private readonly Forms.ToolStripMenuItem _pause15 = new("免打扰 15 分钟");
    private readonly Forms.ToolStripMenuItem _pause60 = new("免打扰 1 小时");
    private readonly Forms.ToolStripMenuItem _pauseManual = new("免打扰至手动结束");
    private readonly Forms.ToolStripMenuItem _endPause = new("结束免打扰");
    private Action? _openDiagnostics;
    private DesktopNotificationState? _notificationState;
    private bool _disposed;
    public bool NotificationsAvailable => !_disposed;

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
        _notifications.DropDownItems.AddRange([_notificationToggle, new Forms.ToolStripSeparator(), _pause15, _pause60, _pauseManual, _endPause]);
        _notifications.Visible = false;
        _menu.Items.AddRange([_toggle, _status, _notifications, new Forms.ToolStripSeparator(), exitItem]);
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
        _icon.BalloonTipClicked += (_, _) => _openDiagnostics?.Invoke();
        try { _icon.Visible = true; }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ConfigureNotifications(Action openDiagnostics, Action toggleEnabled,
        Action<TimeSpan?> pause, Action endPause)
    {
        _openDiagnostics = openDiagnostics;
        _notificationToggle.Click += (_, _) => toggleEnabled();
        _pause15.Click += (_, _) => pause(TimeSpan.FromMinutes(15));
        _pause60.Click += (_, _) => pause(TimeSpan.FromHours(1));
        _pauseManual.Click += (_, _) => pause(null);
        _endPause.Click += (_, _) => endPause();
        _notifications.Visible = true;
    }

    public void UpdateNotifications(DesktopNotificationState state)
    {
        if (_disposed) return;
        _notificationState = state;
        _notifications.Text = DesktopNotificationPresentation.Status(state) + " · 本次运行";
        _notificationToggle.Text = state.Enabled ? "关闭诊断通知" : "开启诊断通知";
        _notificationToggle.Enabled = state.TrayAvailable;
        _pause15.Enabled = _pause60.Enabled = _pauseManual.Enabled = state.Enabled && state.TrayAvailable;
        _endPause.Visible = state.DoNotDisturb;
    }

    public bool TryShowNotification(DesktopDiagnosticNotification notification)
    {
        if (_disposed || !_icon.Visible || _notificationState is not { Enabled: true, TrayAvailable: true, DoNotDisturb: false } ||
            notification.Events.Count == 0) return false;
        var labels = notification.Events.Take(3).Select(item => RuleLabel(item.RuleId)).ToArray();
        var title = $"PerfMonitor · {notification.Events.Count} 项新诊断";
        var body = string.Join("、", labels) + (notification.Events.Count > 3 ? " 等" : "") + "\n点击查看诊断 · 显示受 Windows 设置影响";
        // This requests a system balloon. Windows may suppress it; this return value is not delivery evidence.
        try { _icon.ShowBalloonTip(8000, title, body, Forms.ToolTipIcon.None); return true; }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    private static string RuleLabel(string rule) => rule switch
    {
        DiagnosticRuleIds.HighCpu => "CPU 持续繁忙", DiagnosticRuleIds.MemoryPressure => "内存压力",
        DiagnosticRuleIds.SystemDiskLow => "系统盘空间不足", DiagnosticRuleIds.ProcessCpuSpike => "进程 CPU 突增",
        DiagnosticRuleIds.SamplingGap => "采样中断", DiagnosticRuleIds.ProviderUnavailable => "采集暂不可用",
        DiagnosticRuleIds.AgentResourceAnomaly => "监测资源开销", _ => "诊断事件",
    };

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
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
