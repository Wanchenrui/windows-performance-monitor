using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PerfMonitor.Desktop;

/// <summary>Owns the Desktop's local shell integration; hiding never ends its Agent session.</summary>
internal sealed class DesktopResidentController : IDisposable
{
    private readonly Window _window;
    private readonly Func<Task> _stopSession;
    private readonly Action _shutdown;
    private readonly Action<string> _setStatus;
    private readonly Func<HwndSource, Action, IDesktopHotkey> _registerHotkey;
    private readonly Action _activateWindow;
    private IDesktopTray? _tray;
    private IDesktopHotkey? _hotkey;
    private WindowState _restoreState;
    private Task? _exitTask;
    private bool _exiting;
    private bool _closed;
    private bool _disposed;

    internal DesktopResidentController(
        Window window,
        Func<Task> stopSession,
        Action shutdown,
        Action<string> setStatus,
        Func<Action, Action, IDesktopTray?>? createTray = null,
        Func<HwndSource, Action, IDesktopHotkey>? registerHotkey = null,
        Action? activateWindow = null)
    {
        window.Dispatcher.VerifyAccess();
        _window = window;
        _stopSession = stopSession;
        _shutdown = shutdown;
        _setStatus = setStatus;
        _registerHotkey = registerHotkey ?? ((source, toggle) => new DesktopHotkey(source, toggle));
        _activateWindow = activateWindow ?? (() => { _ = window.Activate(); });
        _restoreState = window.WindowState == WindowState.Maximized
            ? WindowState.Maximized : WindowState.Normal;

        try
        {
            _tray = (createTray ?? DesktopTrayIcon.Create)(
                () => Dispatch(Toggle), () => Dispatch(() => { _ = ExitAsync(); }));
        }
        catch (Exception exception) when (exception is Win32Exception or ExternalException or
            ArgumentException or InvalidOperationException)
        {
            // An ordinary closable window is safer than hiding without a usable tray entry.
            Trace.TraceWarning("Desktop tray unavailable: {0}", exception.GetType().Name);
        }

        window.SourceInitialized += OnSourceInitialized;
        window.Closing += OnClosing;
        window.Closed += OnClosed;
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.StateChanged += OnStateChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) RegisterHotkey(handle);
        UpdateStatus();
    }

    internal void Toggle()
    {
        _window.Dispatcher.VerifyAccess();
        if (_exiting || _disposed) return;
        if (_tray is not null && _window.IsVisible && _window.WindowState != WindowState.Minimized)
            Hide();
        else
            Show();
    }

    internal bool TryStartHidden()
    {
        _window.Dispatcher.VerifyAccess();
        if (_exiting || _disposed || _tray is null) return false;
        _window.ShowActivated = false;
        // Create the real message HWND/shortcut without ever showing the window.
        _ = new WindowInteropHelper(_window).EnsureHandle();
        UpdateStatus();
        return true;
    }

    internal void Show()
    {
        _window.Dispatcher.VerifyAccess();
        if (_exiting || _disposed) return;
        // Size, position and the dashboard's compact/expanded choice belong to the window.
        // Show before restoring state so Windows keeps the real restore bounds, not the icon bounds.
        var restoreState = _restoreState;
        _window.Show();
        _window.WindowState = restoreState;
        _activateWindow();
    }

    internal void Hide()
    {
        _window.Dispatcher.VerifyAccess();
        if (_exiting || _disposed || _tray is null) return;
        if (_window.WindowState != WindowState.Minimized) _restoreState = _window.WindowState;
        _window.Hide();
        UpdateTray();
    }

    internal Task ExitAsync()
    {
        _window.Dispatcher.VerifyAccess();
        return _exitTask ??= ExitCoreAsync();
    }

    private async Task ExitCoreAsync()
    {
        _exiting = true;
        if (!_closed) _window.Hide();
        ReleaseNativeIntegration();
        try
        {
            await _stopSession().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Cleanup failure must not leave a hidden Desktop process behind.
            Trace.TraceError("Desktop session shutdown failed: {0}", exception.GetType().Name);
        }
        finally
        {
            DetachWindowEvents();
            try
            {
                if (!_closed) _window.Close();
            }
            finally
            {
                _shutdown();
            }
        }
    }

    internal void BeginSystemShutdown()
    {
        _window.Dispatcher.VerifyAccess();
        _exiting = true; // Never cancel Windows sign-out/shutdown in order to remain in the tray.
        ReleaseNativeIntegration();
        _exitTask ??= StopForSystemShutdownAsync();
    }

    private async Task StopForSystemShutdownAsync()
    {
        try { await _stopSession().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Trace.TraceError("Desktop session shutdown failed: {0}", exception.GetType().Name);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        RegisterHotkey(new WindowInteropHelper(_window).Handle);
        UpdateStatus();
    }

    private void RegisterHotkey(IntPtr handle)
    {
        if (_hotkey is not null || _exiting || _disposed) return;
        if (HwndSource.FromHwnd(handle) is { } source)
            _hotkey = _registerHotkey(source, () => Dispatch(Toggle));
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_exiting || _disposed) return;
        args.Cancel = true;
        if (_tray is not null) Hide();
        else _ = _window.Dispatcher.InvokeAsync(() => { _ = ExitAsync(); });
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        if (!_exiting && !_disposed) _ = ExitAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _tray is null || _exiting || _disposed) return;
        args.Handled = true;
        Hide();
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (_exiting || _disposed) return;
        if (_window.WindowState == WindowState.Minimized && _tray is not null)
        {
            // Recheck after the native state change so a just-recalled window is not hidden again.
            _ = _window.Dispatcher.InvokeAsync(() =>
            {
                if (_window.WindowState == WindowState.Minimized) Hide();
            });
        }
        else if (_window.WindowState != WindowState.Minimized)
            _restoreState = _window.WindowState;
        UpdateTray();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateTray();

    private void UpdateStatus()
    {
        var status = _tray is null
            ? "托盘不可用 · 关闭窗口退出"
            : _hotkey is null
                ? "Ctrl+Alt+P 呼出 / 收起 · Esc / × 收起 · 托盘菜单退出"
                : _hotkey.IsRegistered
                    ? "Ctrl+Alt+P 呼出 / 收起 · Esc / × 收起 · 托盘菜单退出"
                    : _hotkey.ErrorCode == DesktopHotkey.AlreadyRegisteredError
                        ? "Ctrl+Alt+P 已被占用 · 从托盘呼出 / 收起 · 托盘菜单退出"
                        : $"全局快捷键不可用（系统错误 {_hotkey.ErrorCode}）· 从托盘呼出 / 退出";
        _setStatus(status);
        UpdateTray(status);
    }

    private void UpdateTray(string? status = null) => _tray?.Update(
        _window.IsVisible && _window.WindowState != WindowState.Minimized, status);

    private void Dispatch(Action callback)
    {
        if (_exiting || _disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (_window.Dispatcher.CheckAccess()) callback();
        else _ = _window.Dispatcher.InvokeAsync(callback);
    }

    private void ReleaseNativeIntegration()
    {
        var hotkey = _hotkey;
        var tray = _tray;
        _hotkey = null;
        _tray = null;
        try { hotkey?.Dispose(); }
        finally { tray?.Dispose(); }
    }

    private void DetachWindowEvents()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _window.PreviewKeyDown -= OnPreviewKeyDown;
        _window.StateChanged -= OnStateChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachWindowEvents();
        ReleaseNativeIntegration();
    }
}
