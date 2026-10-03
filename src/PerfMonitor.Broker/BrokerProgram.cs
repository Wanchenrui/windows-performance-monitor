using System.Security.Principal;
using System.ServiceProcess;

namespace PerfMonitor.Broker;

public static class BrokerProgram
{
    public static async Task<int> Main(string[] args)
    {
        BrokerOptions options;
        try
        {
            options = BrokerOptions.Parse(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FormatException or
            OverflowException or
            InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(exception.Message)
                .ConfigureAwait(false);
            return 2;
        }

        if (options.Mode == BrokerRunMode.Service)
        {
            if (!IsLocalSystem())
            {
                await Console.Error.WriteLineAsync(
                    "broker_service_requires_local_system")
                    .ConfigureAwait(false);
                return 3;
            }

            ServiceBase.Run(new BrokerWindowsService(options));
            return 0;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        try
        {
            return await BrokerRunner.RunAsync(
                options,
                stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"broker_failure: {StableException(exception)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    internal static bool IsLocalSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        return identity.IsSystem;
    }

    private static string StableException(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                "access_denied",
            IOException => "io_failure",
            InvalidDataException => "invalid_data",
            _ => exception.GetType().Name,
        };
}

internal sealed class BrokerWindowsService : ServiceBase
{
    private readonly BrokerOptions _options;
    private CancellationTokenSource? _stopping;
    private Task<int>? _runTask;

    public BrokerWindowsService(BrokerOptions options)
    {
        _options = options;
        ServiceName = "PerfMonitorBroker";
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _ = args;
        _stopping = new CancellationTokenSource();
        _runTask = BrokerRunner.RunAsync(
            _options,
            _stopping.Token);
        _ = _runTask.ContinueWith(
            static (task, state) =>
            {
                if (task.IsFaulted &&
                    state is BrokerWindowsService service)
                {
                    service.ExitCode = 1;
                    service.Stop();
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    protected override void OnStop() => StopRunner();

    protected override void OnShutdown() => StopRunner();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StopRunner()
    {
        _stopping?.Cancel();
        if (_runTask is null)
        {
            return;
        }

        if (!_runTask.Wait(TimeSpan.FromSeconds(30)))
        {
            ExitCode = 1;
            throw new System.TimeoutException(
                "Broker did not stop within 30 seconds.");
        }

        if (_runTask.IsFaulted)
        {
            ExitCode = 1;
        }
    }
}
