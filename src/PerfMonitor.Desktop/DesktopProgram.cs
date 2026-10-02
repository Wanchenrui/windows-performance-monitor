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
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var window = new MainWindow(
            PipeEndpoint.ForCurrentUser())
        {
            Icon = DesktopAppIcon.LoadWindowImage(),
        };
        using var resident = new DesktopResidentController(
            window,
            window.StopSessionAsync,
            () => application.Shutdown(),
            window.SetResidentStatus);
        application.SessionEnding += (_, _) => resident.BeginSystemShutdown();
        application.Exit += (_, _) => resident.Dispose();
        return application.Run(window);
    }
}
