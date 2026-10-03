using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PerfMonitor.Desktop;

internal interface IDesktopHotkey : IDisposable
{
    bool IsRegistered { get; }
    int ErrorCode { get; }
}

internal sealed class DesktopHotkey : IDesktopHotkey
{
    internal const int AlreadyRegisteredError = 1409;
    internal const uint DefaultModifiers = 0x4003; // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT
    internal const uint DefaultKey = 0x50; // P
    internal const int HotkeyMessage = 0x0312;
    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly Action _toggle;
    private readonly int _id;
    private bool _disposed;

    internal DesktopHotkey(HwndSource source, Action toggle,
        uint modifiers = DefaultModifiers, uint key = DefaultKey, int id = 0x504D)
    {
        _source = source;
        _handle = source.Handle;
        _toggle = toggle;
        _id = id;
        IsRegistered = RegisterHotKey(_handle, id, modifiers, key);
        ErrorCode = IsRegistered ? 0 : Marshal.GetLastPInvokeError();
        if (!IsRegistered) return;
        try { source.AddHook(HandleMessage); }
        catch
        {
            _ = UnregisterHotKey(_handle, id);
            IsRegistered = false;
            throw;
        }
    }

    public bool IsRegistered { get; private set; }
    public int ErrorCode { get; }

    internal IntPtr HandleMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (!_disposed && IsRegistered && message == HotkeyMessage && wParam.ToInt64() == _id)
        {
            handled = true;
            _toggle();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!IsRegistered) return;
        if (!_source.IsDisposed)
        {
            _source.RemoveHook(HandleMessage);
            _ = UnregisterHotKey(_handle, _id);
        }
        IsRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);
}
