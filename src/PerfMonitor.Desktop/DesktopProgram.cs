using System.Windows;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public static class DesktopProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!TryParseArguments(args, out var startHidden))
        {
            Console.Error.WriteLine("desktop_invalid_arguments: expected only --start-hidden");
            return 2;
        }
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
        if (startHidden)
        {
            application.MainWindow = window;
            if (!resident.TryStartHidden())
            {
                Console.Error.WriteLine("desktop_start_hidden_tray_unavailable");
                resident.ExitAsync().GetAwaiter().GetResult();
                return 3;
            }
            window.StartSession();
            // Run(window) shows its argument even if ShowActivated is false.
            return application.Run();
        }
        return application.Run(window);
    }

    internal static bool TryParseArguments(string[] args, out bool startHidden)
    {
        startHidden = args.Length == 1 && args[0] == "--start-hidden";
        return args.Length == 0 || startHidden;
    }
}
