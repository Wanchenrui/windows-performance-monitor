using System.IO;
using System.Windows.Media.Imaging;

namespace PerfMonitor.Desktop;

internal static class DesktopAppIcon
{
    private static Stream OpenResource() => typeof(DesktopAppIcon).Assembly
        .GetManifestResourceStream("PerfMonitor.Desktop.Icon")
        ?? throw new InvalidOperationException("Desktop icon resource is missing.");

    internal static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = OpenResource();
        return new System.Drawing.Icon(stream, 32, 32);
    }

    internal static BitmapFrame LoadWindowImage()
    {
        using var stream = OpenResource();
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
