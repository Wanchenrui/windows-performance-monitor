using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopLifecycleTests
{
    [STATestMethod]
    public void CloseAndEscapeHideWithoutStoppingSessionAndTrayRestoresSameWindow()
    {
        using var fixture = new Fixture();
        fixture.Window.Show();
        Assert.IsTrue(fixture.Tray.WindowVisible);
        fixture.Window.Close();
        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.IsFalse(fixture.Tray.WindowVisible);
        Assert.AreEqual(0, fixture.StopCalls);
        Assert.AreEqual(0, fixture.ShutdownCalls);

        fixture.Tray.Toggle!();
        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(760d, fixture.Window.Width);
        Assert.AreEqual(300d, fixture.Window.Height);
        Assert.AreEqual(1, fixture.ActivateCalls);
        Assert.AreEqual(1, fixture.HotkeyRegistrations);

        var key = new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(fixture.Window), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        fixture.Window.RaiseEvent(key);
        Assert.IsTrue(key.Handled);
        Assert.IsFalse(fixture.Window.IsVisible);
        fixture.Hotkey.Toggle!();
        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(0, fixture.StopCalls);
        Assert.AreEqual(1, fixture.HotkeyRegistrations);
    }

    [STATestMethod]
    public void MinimizeCollectsToTrayAndRestoresSizeWithoutChangingDashboardMode()
    {
        using var fixture = new Fixture();
        fixture.Window.Show();
        fixture.Window.Width = 1000;
        fixture.Window.Height = 680;
        fixture.Window.WindowState = WindowState.Minimized;
        PumpUntilCondition(() => !fixture.Window.IsVisible);
        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.IsFalse(fixture.Tray.WindowVisible);
        fixture.Controller.Show();
        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(WindowState.Normal, fixture.Window.WindowState);
        fixture.Controller.Hide();
        fixture.Controller.Show();
        Assert.AreEqual(WindowState.Normal, fixture.Window.WindowState);
        Assert.AreEqual(1000d, fixture.Window.Width);
        Assert.AreEqual(680d, fixture.Window.Height);
        Assert.AreEqual(0, fixture.StopCalls);
    }

    [STATestMethod]
    public void ExplicitExitReleasesEntryPointsAndAwaitsSessionExactlyOnceBeforeShutdown()
    {
        using var context = new DispatcherContext();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(stop.Task);
        fixture.Window.Show();
        fixture.Tray.Exit!();
        var exit = fixture.Controller.ExitAsync();
        Assert.AreSame(exit, fixture.Controller.ExitAsync());
        Assert.IsFalse(exit.IsCompleted);
        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.AreEqual(1, fixture.StopCalls);
        Assert.AreEqual(1, fixture.Tray.DisposeCalls);
        Assert.AreEqual(1, fixture.Hotkey.DisposeCalls);
        Assert.AreEqual(0, fixture.ShutdownCalls);
        fixture.Tray.Toggle!();
        fixture.Hotkey.Toggle!();
        Assert.IsFalse(fixture.Window.IsVisible);
        stop.SetResult();
        PumpUntil(exit);
        Assert.AreEqual(1, fixture.ShutdownCalls);
        fixture.Controller.Dispose();
        Assert.AreEqual(1, fixture.Tray.DisposeCalls);
        Assert.AreEqual(1, fixture.Hotkey.DisposeCalls);
    }

    [STATestMethod]
    public void HotkeyConflictIsVisibleAndTrayRemainsAUsableEntryPoint()
    {
        using var fixture = new Fixture(hotkeyRegistered: false,
            hotkeyError: DesktopHotkey.AlreadyRegisteredError);
        fixture.Window.Show();
        StringAssert.Contains(fixture.Status, "已被占用");
        StringAssert.Contains(fixture.Status, "托盘");
        StringAssert.Contains(fixture.Tray.Status!, "已被占用");
        fixture.Window.Close();
        fixture.Tray.Toggle!();
        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(0, fixture.StopCalls);
    }

    [STATestMethod]
    public void MissingOrFailedTrayKeepsWindowRecoverableAndCloseExits()
    {
        foreach (var throws in new[] { false, true })
        {
            using var context = new DispatcherContext();
            using var fixture = new Fixture(trayAvailable: false, trayThrows: throws);
            fixture.Window.Show();
            StringAssert.Contains(fixture.Status, "托盘不可用");
            fixture.Controller.Hide();
            Assert.IsTrue(fixture.Window.IsVisible);
            fixture.Window.WindowState = WindowState.Minimized;
            Assert.IsTrue(fixture.Window.IsVisible);
            fixture.Controller.Toggle();
            Assert.AreEqual(WindowState.Normal, fixture.Window.WindowState);
            fixture.Window.Close();
            PumpUntilCondition(() => fixture.ShutdownCalls == 1);
            Assert.AreEqual(1, fixture.StopCalls);
            Assert.AreEqual(1, fixture.Hotkey.DisposeCalls);
        }
    }

    [STATestMethod]
    public void SessionCleanupFailureStillClosesDesktopAndReleasesResources()
    {
        using var fixture = new Fixture(Task.FromException(new InvalidOperationException("stop failed")));
        fixture.Window.Show();
        PumpUntil(fixture.Controller.ExitAsync());
        Assert.AreEqual(1, fixture.ShutdownCalls);
        Assert.AreEqual(1, fixture.StopCalls);
        Assert.AreEqual(1, fixture.Tray.DisposeCalls);
        Assert.AreEqual(1, fixture.Hotkey.DisposeCalls);
    }

    [STATestMethod]
    public void WindowsSessionEndingDoesNotCancelClosingToStayInTray()
    {
        using var fixture = new Fixture();
        fixture.Window.Show();
        fixture.Controller.BeginSystemShutdown();
        var canceled = true;
        fixture.Window.Closing += (_, args) => canceled = args.Cancel;
        fixture.Window.Close();
        Assert.IsFalse(canceled);
        Assert.AreEqual(1, fixture.StopCalls);
        Assert.AreEqual(1, fixture.Tray.DisposeCalls);
        Assert.AreEqual(1, fixture.Hotkey.DisposeCalls);
    }

    [STATestMethod]
    public void NativeHotkeyConflictAndReleaseUseWindowLifetimeAndIgnoreOtherMessages()
    {
        using var firstSource = MessageWindow("PerfMonitor hotkey test 1");
        using var secondSource = MessageWindow("PerfMonitor hotkey test 2");
        const uint modifiers = 0x4007; // Ctrl + Alt + Shift + no repeat
        const uint key = 0x87; // F24 avoids reserving the user's default shortcut during the test.
        const int id = 0x5123;
        var toggles = 0;
        using var first = new DesktopHotkey(firstSource, () => toggles++, modifiers, key, id);
        Assert.IsTrue(first.IsRegistered, $"Native registration failed: {first.ErrorCode}");
        using (var conflict = new DesktopHotkey(secondSource, () => toggles++, modifiers, key, id))
        {
            Assert.IsFalse(conflict.IsRegistered);
            Assert.AreEqual(DesktopHotkey.AlreadyRegisteredError, conflict.ErrorCode);
        }
        var handled = false;
        _ = first.HandleMessage(firstSource.Handle, DesktopHotkey.HotkeyMessage,
            new IntPtr(id + 1), IntPtr.Zero, ref handled);
        Assert.IsFalse(handled);
        _ = first.HandleMessage(firstSource.Handle, DesktopHotkey.HotkeyMessage,
            new IntPtr(id), IntPtr.Zero, ref handled);
        Assert.IsTrue(handled);
        Assert.AreEqual(1, toggles);
        first.Dispose();
        handled = false;
        _ = first.HandleMessage(firstSource.Handle, DesktopHotkey.HotkeyMessage,
            new IntPtr(id), IntPtr.Zero, ref handled);
        Assert.IsFalse(handled);
        Assert.AreEqual(1, toggles);
        using var replacement = new DesktopHotkey(secondSource, () => toggles++, modifiers, key, id);
        Assert.IsTrue(replacement.IsRegistered, "Disposing the first registration must release the key.");
    }

    [STATestMethod]
    public void OriginalIconCanBeDecodedByBothNativeTrayAndWpfWindow()
    {
        using var icon = DesktopAppIcon.LoadTrayIcon();
        Assert.AreEqual(32, icon.Width);
        Assert.AreEqual(32, icon.Height);
        var frame = DesktopAppIcon.LoadWindowImage();
        Assert.IsTrue(frame.IsFrozen);
        Assert.IsTrue(frame.PixelWidth >= 16);
        Assert.IsTrue(frame.PixelHeight >= 16);
    }

    private static HwndSource MessageWindow(string name) => new(new HwndSourceParameters(name)
    {
        ParentWindow = new IntPtr(-3), // HWND_MESSAGE, invisible and never focused.
        WindowStyle = 0,
        Width = 0,
        Height = 0,
    });

    private static void PumpUntil(Task task)
    {
        PumpUntilCondition(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntilCondition(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(completed(), "Desktop cleanup did not finish within the test deadline.");
    }

    private sealed class DispatcherContext : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        public DispatcherContext() => SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed class Fixture : IDisposable
    {
        public Window Window { get; } = new()
        {
            Width = 760, Height = 300, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        public FakeTray Tray { get; } = new();
        public FakeHotkey Hotkey { get; }
        public DesktopResidentController Controller { get; }
        public int StopCalls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int HotkeyRegistrations { get; private set; }
        public string Status { get; private set; } = string.Empty;

        public Fixture(Task? stop = null, bool trayAvailable = true, bool trayThrows = false,
            bool hotkeyRegistered = true, int hotkeyError = 0)
        {
            Hotkey = new(hotkeyRegistered, hotkeyError);
            Controller = new(Window, () => { StopCalls++; return stop ?? Task.CompletedTask; },
                () => ShutdownCalls++, value => Status = value,
                (toggle, exit) =>
                {
                    if (trayThrows) throw new Win32Exception(5);
                    Tray.Toggle = toggle;
                    Tray.Exit = exit;
                    return trayAvailable ? Tray : null;
                },
                (_, toggle) =>
                {
                    HotkeyRegistrations++;
                    Hotkey.Toggle = toggle;
                    return Hotkey;
                },
                () => ActivateCalls++);
        }

        public void Dispose()
        {
            Controller.Dispose();
            Window.Close();
        }
    }

    private sealed class FakeTray : IDesktopTray
    {
        public Action? Toggle { get; set; }
        public Action? Exit { get; set; }
        public bool WindowVisible { get; private set; }
        public string? Status { get; private set; }
        public int DisposeCalls { get; private set; }
        public void Update(bool windowVisible, string? status)
        {
            WindowVisible = windowVisible;
            if (status is not null) Status = status;
        }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeHotkey(bool registered, int error) : IDesktopHotkey
    {
        public Action? Toggle { get; set; }
        public bool IsRegistered => registered;
        public int ErrorCode => error;
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
    }
}
