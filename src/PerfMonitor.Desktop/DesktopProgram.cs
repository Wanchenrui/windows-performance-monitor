using System.Windows;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public static class DesktopProgram
{
    [STAThread]
    public static int Main()
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };
        var window = new MainWindow(
            PipeEndpoint.ForCurrentUser());
        return application.Run(window);
    }
}
