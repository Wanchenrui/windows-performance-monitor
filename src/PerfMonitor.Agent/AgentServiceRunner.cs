using PerfMonitor.Actions;
using PerfMonitor.Broker.Client;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;
using PerfMonitor.Ipc.NamedPipes;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.Agent;

public static class AgentServiceRunner
{
    public static async Task<int> RunAsync(
        AgentServiceOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Sampling.Once)
        {
            return await AgentRunner.RunAsync(
                options.Sampling,
                cancellationToken).ConfigureAwait(false);
        }

        var providers = AgentProviderFactory.CreateDefault();
        var assembler = new SnapshotAssembler(
            providers.Select(provider => provider.Descriptor));
        var endpoint = PipeEndpoint.ForCurrentUser();
        var databasePath = Path.Combine(
            options.DataDirectory,
            "history-v1.db");
        var diagnosticPolicy = DiagnosticPolicyFile.LoadOrCreate(
            options.DiagnosticPolicyPath,
            out var diagnosticPolicyWarning);
        if (diagnosticPolicyWarning is not null)
        {
            await Console.Error.WriteLineAsync(
                diagnosticPolicyWarning).ConfigureAwait(false);
        }
        var actionPolicy = ActionPolicyFile.LoadOrCreateAgent(
            options.ActionPolicyPath,
            out var actionPolicyWarning);
        if (actionPolicyWarning is not null)
        {
            await Console.Error.WriteLineAsync(
                actionPolicyWarning).ConfigureAwait(false);
        }

        var actionGateway = new AgentActionGateway(
            actionPolicy,
            new BrokerActionClient());
        // v0.7.2: Broker availability is optional and must never delay
        // telemetry scheduling. Capabilities start unavailable and are
        // refreshed asynchronously when the fixed Broker endpoint responds.
        _ = actionGateway.ProbeAsync(cancellationToken);

        await using var storage = new SqliteHistoryStore(
            new SqliteHistoryOptions
            {
                DatabasePath = databasePath,
            });
        await using var diagnostics = new DiagnosticEngine(
            diagnosticPolicy,
            [storage]);
        await using var subscriptions = new SnapshotSubscriptionHub();
        var queryService = new AgentQueryService(
            assembler,
            storage,
            storage,
            diagnostics,
            actionGateway,
            diagnosticPolicy.ToCapabilities(),
            providers.Select(provider => provider.Descriptor),
            endpoint,
            options.Sampling.OutputPeriod);
        await using var server = options.EnableIpc
            ? new NamedPipeAgentServer(
                endpoint,
                queryService,
                subscriptions)
            : null;
        await using var scheduler = new ProviderScheduler(
            providers,
            assembler,
            options.Sampling.MaxConcurrency);
        var fanout = new SnapshotFanout(
            [storage, diagnostics, subscriptions]);

        diagnostics.Start();
        scheduler.Start();
        server?.Start();

        var storageStart = storage.StartAsync(cancellationToken);
        if (server is not null)
        {
            var startWinner = await Task.WhenAny(
                storageStart,
                server.Completion).ConfigureAwait(false);
            if (startWinner == server.Completion)
            {
                await EnsureServerRunningAsync(server)
                    .ConfigureAwait(false);
            }
        }

        await storageStart.ConfigureAwait(false);
        if (options.Sampling.Warmup > TimeSpan.Zero)
        {
            await DelayWithServerAsync(
                options.Sampling.Warmup,
                server,
                cancellationToken).ConfigureAwait(false);
        }

        using var durationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        if (options.Sampling.Duration is not null)
        {
            durationCancellation.CancelAfter(
                options.Sampling.Duration.Value);
        }

        var append = false;
        try
        {
            while (true)
            {
                durationCancellation.Token.ThrowIfCancellationRequested();
                var snapshot = assembler.Read();
                fanout.Publish(snapshot);
                await WriteSnapshotAsync(
                    snapshot,
                    options.Sampling.OutputPath,
                    append,
                    options.Sampling.Quiet,
                    durationCancellation.Token).ConfigureAwait(false);
                append = true;
                await DelayWithServerAsync(
                    options.Sampling.OutputPeriod,
                    server,
                    durationCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            durationCancellation.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested &&
                options.Sampling.Duration is not null)
            {
                var snapshot = assembler.Read();
                fanout.Publish(snapshot);
                await WriteSnapshotAsync(
                    snapshot,
                    options.Sampling.OutputPath,
                    append,
                    options.Sampling.Quiet,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task DelayWithServerAsync(
        TimeSpan delay,
        NamedPipeAgentServer? server,
        CancellationToken cancellationToken)
    {
        var delayTask = Task.Delay(delay, cancellationToken);
        if (server is null)
        {
            await delayTask.ConfigureAwait(false);
            return;
        }

        var winner = await Task.WhenAny(
            delayTask,
            server.Completion).ConfigureAwait(false);
        if (winner == server.Completion)
        {
            await EnsureServerRunningAsync(server).ConfigureAwait(false);
        }

        await delayTask.ConfigureAwait(false);
    }

    private static async Task EnsureServerRunningAsync(
        NamedPipeAgentServer server)
    {
        await server.Completion.ConfigureAwait(false);
        throw new IOException(
            "The Agent IPC listener stopped unexpectedly.");
    }

    private static async Task WriteSnapshotAsync(
        AgentSnapshot snapshot,
        string? outputPath,
        bool append,
        bool quiet,
        CancellationToken cancellationToken)
    {
        ValidateCoreGroups(snapshot);
        var json = AgentJson.Serialize(snapshot);
        if (!quiet)
        {
            await Console.Out.WriteLineAsync(
                json.AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }

        if (outputPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (append)
        {
            await File.AppendAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(
                outputPath,
                json + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateCoreGroups(AgentSnapshot snapshot)
    {
        string[] required =
        [
            GroupIds.SystemCpu,
            GroupIds.Memory,
            GroupIds.Network,
            GroupIds.DiskIo,
            GroupIds.Power,
            GroupIds.Gpu,
            GroupIds.Sensors,
            GroupIds.Volumes,
            GroupIds.Uptime,
            GroupIds.Processes,
            GroupIds.Sampler,
            GroupIds.Self,
        ];
        foreach (var groupId in required)
        {
            if (!snapshot.Groups.ContainsKey(groupId))
            {
                throw new InvalidDataException(
                    $"Snapshot is missing group {groupId}.");
            }
        }
    }
}
