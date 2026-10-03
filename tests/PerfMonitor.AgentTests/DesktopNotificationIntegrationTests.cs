using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Desktop;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopNotificationIntegrationTests
{
    [STATestMethod]
    public void HiddenNotificationsUseFakeTrayRespectDndAndDoNotActivateWindow()
    {
        using var fixture = new Fixture();
        Assert.IsFalse(fixture.Window.DiagnosticNotifications.Current.Enabled);
        Assert.IsTrue(fixture.Window.DiagnosticNotifications.Current.TrayAvailable);
        Assert.IsFalse(fixture.Window.IsVisible);
        fixture.Tray.ToggleEnabled!();
        Assert.IsTrue(fixture.Tray.State!.Enabled);
        var notification = new DesktopDiagnosticNotification("test-instance", []);
        Assert.IsTrue(Wait(fixture.Window.ShowDiagnosticNotificationAsync(notification, CancellationToken.None)));
        Assert.AreEqual(1, fixture.Tray.ShowRequests);
        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.AreEqual(0, fixture.Activations);

        foreach (var duration in new TimeSpan?[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), null })
        {
            fixture.Tray.Pause!(duration);
            Assert.IsTrue(fixture.Tray.State!.DoNotDisturb);
            Assert.IsFalse(Wait(fixture.Window.ShowDiagnosticNotificationAsync(notification, CancellationToken.None)));
            fixture.Tray.EndPause!();
            Assert.IsFalse(fixture.Tray.State!.DoNotDisturb);
        }
        Assert.AreEqual(1, fixture.Tray.ShowRequests);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.IsFalse(Wait(fixture.Window.ShowDiagnosticNotificationAsync(notification, canceled.Token)));
        fixture.Tray.ToggleEnabled!();
        Assert.IsFalse(Wait(fixture.Window.ShowDiagnosticNotificationAsync(notification, CancellationToken.None)));
        Assert.AreEqual(1, fixture.Tray.ShowRequests);
        Assert.AreEqual(0, fixture.Activations);
    }

    [STATestMethod]
    public void NotificationControlsKeepIdentityAndExpansionAcrossDiagnosticRefreshes()
    {
        using var fixture = new Fixture();
        Populate(fixture.Window);
        var buttons = Elements(fixture.Window).OfType<Button>().ToArray();
        var expand = buttons.Single(button => Equals(button.Content, "通知设置  ›"));
        var toggle = buttons.Single(button => Equals(button.Content, "开启通知"));
        var pause = buttons.Single(button => Equals(button.Content, "免打扰 15 分钟"));
        var controls = (StackPanel)LogicalTreeHelper.GetParent(toggle)!;
        Assert.AreEqual(Visibility.Collapsed, controls.Visibility);
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Populate(fixture.Window); Populate(fixture.Window);
        var refreshed = Elements(fixture.Window).OfType<Button>().ToArray();
        Assert.AreSame(toggle, refreshed.Single(button => Equals(button.Content, "关闭通知")));
        Assert.AreSame(expand, refreshed.Single(button => Equals(button.Content, "通知设置  ⌄")));
        Assert.AreEqual(Visibility.Visible, controls.Visibility);
        Assert.IsTrue(Elements(fixture.Window).OfType<TextBlock>().Any(block => block.Text.StartsWith("免打扰中", StringComparison.Ordinal)));
        foreach (var range in new[] { "60 秒", "5 分钟", "1 小时", "24 小时" })
            Assert.IsTrue(refreshed.Any(button => Equals(button.Content, range)));
        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.AreEqual(0, fixture.Activations);
    }

    [STATestMethod]
    public void MissingTrayDisablesNotificationToggleAndKeepsDiagnosticsAvailable()
    {
        using var fixture = new Fixture(trayAvailable: false);
        Populate(fixture.Window);
        var toggle = Elements(fixture.Window).OfType<Button>().Single(button => Equals(button.Content, "开启通知"));
        Assert.IsFalse(toggle.IsEnabled);
        Assert.IsFalse(fixture.Window.DiagnosticNotifications.Current.TrayAvailable);
        Assert.IsTrue(Elements(fixture.Window).OfType<Button>().Any(button => Equals(button.Content, "刷新列表") && button.IsEnabled));
        Assert.IsTrue(Elements(fixture.Window).OfType<TextBlock>().Any(block => block.Text.Contains("托盘不可用", StringComparison.Ordinal)));
    }

    private static void Populate(MainWindow window) => typeof(MainWindow)
        .GetMethod("PopulateDiagnosticsDrawer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Elements(child)) yield return item;
    }

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task); return task.GetAwaiter().GetResult();
    }
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        Assert.IsTrue(task.IsCompleted, "Desktop notification cleanup did not finish.");
        task.GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        public MainWindow Window { get; }
        public FakeTray Tray { get; } = new();
        public DesktopResidentController Controller { get; }
        public int Activations { get; private set; }
        public Fixture(bool trayAvailable = true)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var endpoint = PipeEndpoint.ForCurrentUser() with { PipeName = $"PerfMonitor.notification-ui-test.{Guid.NewGuid():N}" };
            Window = new MainWindow(endpoint) { ShowActivated = false, ShowInTaskbar = false };
            Controller = new(Window, Window.StopSessionAsync, () => { }, Window.SetResidentStatus,
                (_, _) => trayAvailable ? Tray : null, (_, _) => new FakeHotkey(), () => Activations++);
        }
        public void Dispose()
        {
            Controller.Dispose(); Wait(Window.StopSessionAsync()); Window.Close();
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }

    private sealed class FakeTray : IDesktopTray
    {
        public bool NotificationsAvailable => true;
        public Action? ToggleEnabled { get; private set; }
        public Action<TimeSpan?>? Pause { get; private set; }
        public Action? EndPause { get; private set; }
        public DesktopNotificationState? State { get; private set; }
        public int ShowRequests { get; private set; }
        public void ConfigureNotifications(Action openDiagnostics, Action toggleEnabled, Action<TimeSpan?> pause, Action endPause)
        { ToggleEnabled = toggleEnabled; Pause = pause; EndPause = endPause; }
        public void UpdateNotifications(DesktopNotificationState state) => State = state;
        public bool TryShowNotification(DesktopDiagnosticNotification notification) { ShowRequests++; return true; }
        public void Update(bool windowVisible, string? status) { }
        public void Dispose() { }
    }
    private sealed class FakeHotkey : IDesktopHotkey
    {
        public bool IsRegistered => true;
        public int ErrorCode => 0;
        public void Dispose() { }
    }
}
