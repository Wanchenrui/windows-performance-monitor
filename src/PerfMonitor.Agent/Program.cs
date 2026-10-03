namespace PerfMonitor.Agent;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AgentOptions options;
        try
        {
            options = AgentOptions.Parse(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FormatException or
            OverflowException)
        {
            await Console.Error.WriteLineAsync(exception.Message)
                .ConfigureAwait(false);
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        try
        {
            return await AgentRunner.RunAsync(
                options,
                stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"agent_failure: {exception.GetType().Name}")
                .ConfigureAwait(false);
            return 1;
        }
    }
}
