using PerfMonitor.Actions;

namespace PerfMonitor.Broker;

public static class BrokerRunner
{
    public static async Task<int> RunAsync(
        BrokerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var policy = ActionPolicyFile.LoadOrCreateBroker(
            options.PolicyPath);
        if (options.Mode == BrokerRunMode.Service)
        {
            policy = BrokerServicePolicy.Validate(policy);
        }

        await using var audit = new BrokerAuditStore(
            options.DatabasePath);
        await audit.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        await audit.RecoverPendingAsync(
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        var forceDryRunOnly =
            options.Mode == BrokerRunMode.Console;
        var coordinator = new BrokerActionCoordinator(
            policy,
            audit,
            new WindowsPrivilegedActionExecutor(),
            forceDryRunOnly);
        await using var server = new BrokerNamedPipeServer(
            new BrokerPipeEndpoint(options.PipeName),
            coordinator,
            forceDryRunOnly: forceDryRunOnly);
        server.Start();

        using var durationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        if (options.Duration is not null)
        {
            durationCancellation.CancelAfter(
                options.Duration.Value);
        }

        try
        {
            var stopped = Task.Delay(
                Timeout.InfiniteTimeSpan,
                durationCancellation.Token);
            var winner = await Task.WhenAny(
                stopped,
                server.Completion).ConfigureAwait(false);
            if (winner == server.Completion)
            {
                await server.Completion.ConfigureAwait(false);
                throw new IOException(
                    "Broker listener stopped unexpectedly.");
            }

            await stopped.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            durationCancellation.IsCancellationRequested)
        {
        }

        return 0;
    }
}

public static class BrokerServicePolicy
{
    public static BrokerMachinePolicy Validate(
        BrokerMachinePolicy policy,
        string? programFilesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy = policy.Validate();
        if (!policy.RequireApprovedClientImage ||
            !policy.RequireTrustedClientSignature ||
            !policy.RequireProtectedClientPath)
        {
            throw new InvalidDataException(
                "broker_service_client_trust_required");
        }

        if (policy.EnabledActionTypes.Count > 0 &&
            policy.ApprovedClientImages.Count != 1)
        {
            throw new InvalidDataException(
                "broker_service_approved_agent_required");
        }

        if (policy.ApprovedClientImages.Any(image =>
            !WindowsBrokerClientExecutableTrustVerifier
                .IsExpectedAgentPath(
                    image.Path,
                    programFilesRoot ??
                        Environment.GetFolderPath(
                            Environment.SpecialFolder
                                .ProgramFiles))))
        {
            throw new InvalidDataException(
                "broker_service_client_path_invalid");
        }

        return policy;
    }
}
