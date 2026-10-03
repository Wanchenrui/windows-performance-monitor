using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PerfMonitor.Desktop;

public sealed partial class MainWindow
{
    // Settings live only in this Desktop process; no Agent settings or persistent preferences change.
    private readonly DesktopDiagnosticNotifications _notifications;
    private Task? _notificationTask;
    private IDesktopTray? _notificationTray;
    private NotificationControls? _notificationControls;
    internal DesktopDiagnosticNotifications DiagnosticNotifications => _notifications;

    internal void AttachNotificationTray(IDesktopTray? tray)
    {
        Dispatcher.VerifyAccess();
        _notificationTray = tray;
        _notifications.SetTrayAvailable(tray?.NotificationsAvailable == true);
        UpdateNotificationControls();
    }

    internal async Task<bool> ShowDiagnosticNotificationAsync(DesktopDiagnosticNotification notification, CancellationToken token)
    {
        if (Dispatcher.HasShutdownStarted || token.IsCancellationRequested) return false;
        return await Dispatcher.InvokeAsync(() =>
        {
            // The coordinator invalidates pending work on disconnect, DND, disabling or exit.
            token.ThrowIfCancellationRequested();
            var state = _notifications.Current;
            if (_stopTask is not null || !state.Enabled || !state.TrayAvailable || state.DoNotDisturb) return false;
            return _notificationTray?.TryShowNotification(notification) == true;
        }, DispatcherPriority.Background, token);
    }

    private void OnNotificationStateChanged(DesktopNotificationState state)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) UpdateNotificationControls();
        else _ = Dispatcher.InvokeAsync(UpdateNotificationControls, DispatcherPriority.Background);
    }

    private UIElement BuildNotificationControls()
    {
        var view = _notificationControls ??= new(this);
        UpdateNotificationControls();
        return view.Root;
    }

    private void UpdateNotificationControls()
    {
        if (_notificationControls is not { } view) return;
        var state = _notifications.Current;
        var status = DesktopNotificationPresentation.Status(state);
        view.Summary.Text = status;
        view.Toggle.Content = state.Enabled ? "关闭通知" : "开启通知";
        view.Toggle.IsEnabled = state.TrayAvailable && _stopTask is null;
        AutomationProperties.SetItemStatus(view.Toggle, state.Enabled ? "已开启" : "已关闭");
        view.Pause15.IsEnabled = view.Pause60.IsEnabled = view.PauseManual.IsEnabled =
            state.Enabled && state.TrayAvailable && _stopTask is null;
        view.EndPause.Visibility = state.DoNotDisturb ? Visibility.Visible : Visibility.Collapsed;
        view.EndPause.IsEnabled = _stopTask is null;
        view.Detail.Text = "默认关闭 · 仅本次 Desktop 运行有效\n只读取现有诊断，不改变采集或执行动作\n不自动打开窗口 · 显示受 Windows 设置影响" +
            (state.QueryError is null ? "" : "\n诊断通知查询暂不可用，等待自动重试");
        view.Root.ToolTip = status + " · 设置仅本次 Desktop 运行有效";
    }

    private sealed class NotificationControls
    {
        public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 10) };
        public TextBlock Summary { get; } = Text("通知已关闭", 11, Muted);
        public TextBlock Detail { get; } = Text("", 10, Muted);
        public Button Toggle { get; }
        public Button Pause15 { get; }
        public Button Pause60 { get; }
        public Button PauseManual { get; }
        public Button EndPause { get; }

        public NotificationControls(MainWindow owner)
        {
            var header = new DockPanel();
            var expand = Action("通知设置  ›", (_, _) => { });
            DockPanel.SetDock(expand, Dock.Right); header.Children.Add(expand); header.Children.Add(Summary);
            Root.Children.Add(header);
            var controls = new StackPanel { Visibility = Visibility.Collapsed };
            expand.Click += (_, _) =>
            {
                var open = controls.Visibility != Visibility.Visible;
                controls.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                expand.Content = open ? "通知设置  ⌄" : "通知设置  ›";
                AutomationProperties.SetItemStatus(expand, open ? "已展开" : "已折叠");
            };
            AutomationProperties.SetName(expand, "展开或折叠本次运行的诊断通知设置");
            Toggle = Action("开启通知", (_, _) => owner._notifications.SetEnabled(!owner._notifications.Current.Enabled));
            AutomationProperties.SetName(Toggle, "开启或关闭本次 Desktop 运行的诊断通知");
            controls.Children.Add(Toggle);
            var pauses = new WrapPanel();
            Pause15 = Action("免打扰 15 分钟", (_, _) => owner._notifications.SetDoNotDisturb(TimeSpan.FromMinutes(15)));
            Pause60 = Action("免打扰 1 小时", (_, _) => owner._notifications.SetDoNotDisturb(TimeSpan.FromHours(1)));
            PauseManual = Action("免打扰至手动结束", (_, _) => owner._notifications.SetDoNotDisturb(null));
            EndPause = Action("结束免打扰", (_, _) => owner._notifications.EndDoNotDisturb());
            pauses.Children.Add(Pause15); pauses.Children.Add(Pause60); pauses.Children.Add(PauseManual); pauses.Children.Add(EndPause);
            controls.Children.Add(pauses); Detail.Margin = new Thickness(0, 5, 0, 0); controls.Children.Add(Detail);
            Root.Children.Add(controls);
        }
    }
}

internal static class DesktopNotificationPresentation
{
    internal static string Status(DesktopNotificationState state)
    {
        if (!state.TrayAvailable) return "通知不可用 · 托盘不可用";
        if (!state.Enabled) return "通知已关闭";
        if (state.DoNotDisturb)
            return state.DoNotDisturbRemaining is { } remaining
                ? $"免打扰中 · 剩余约 {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟"
                : "免打扰中 · 至手动结束";
        return "诊断通知已开启";
    }
}
