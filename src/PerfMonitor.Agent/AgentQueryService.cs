using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.Agent;

internal sealed class AgentQueryService : IAgentIpcService
{
    private readonly SnapshotAssembler _assembler;
    private readonly IHistoryReader _history;
    private readonly IDiagnosticEventReader _persistedDiagnostics;
    private readonly IDiagnosticEventReader _recentDiagnostics;
    private readonly CapabilitiesContract _capabilities;

    public AgentQueryService(
        SnapshotAssembler assembler,
        IHistoryReader history,
        IDiagnosticEventReader persistedDiagnostics,
        IDiagnosticEventReader recentDiagnostics,
        DiagnosticsCapabilityContract diagnosticsCapabilities,
        IEnumerable<ProviderDescriptor> descriptors,
        PipeEndpoint endpoint,
        TimeSpan snapshotPeriod)
    {
        _assembler = assembler;
        _history = history;
        _persistedDiagnostics = persistedDiagnostics;
        _recentDiagnostics = recentDiagnostics;
        _capabilities = BuildCapabilities(
            assembler.InstanceId,
            diagnosticsCapabilities,
            descriptors,
            endpoint,
            snapshotPeriod);
    }

    public string InstanceId => _assembler.InstanceId;

    public AgentSnapshot ReadLatestSnapshot() => _assembler.Read();

    public HealthContract ReadHealth()
    {
        var snapshot = _assembler.Read();
        return new HealthContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            Service = ServiceIds.PerfMonitor,
            InstanceId = snapshot.InstanceId,
            Sequence = snapshot.Sequence,
            CompletedAtUtc = snapshot.CompletedAtUtc,
            Summary = new SummaryContract
            {
                Availability = snapshot.Summary.Availability,
                Freshness = snapshot.Summary.Freshness,
            },
        };
    }

    public CapabilitiesContract ReadCapabilities() => _capabilities;

    public async ValueTask<HistoryContract> QueryHistoryAsync(
        HistoryQueryContract query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _history.QueryAsync(
                query,
                InstanceId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (StorageUnavailableException exception)
        {
            throw new IpcServiceUnavailableException(
                exception.ErrorCode);
        }
    }

    public async ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        CancellationToken cancellationToken)
    {
        var recent = await _recentDiagnostics.QueryDiagnosticsAsync(
            query,
            InstanceId,
            cancellationToken).ConfigureAwait(false);
        DiagnosticsContract? persisted = null;
        try
        {
            persisted =
                await _persistedDiagnostics.QueryDiagnosticsAsync(
                    query,
                    InstanceId,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (StorageUnavailableException)
        {
            // v0.6.0: RAM diagnostics remain queryable when SQLite
            // is unavailable. No action endpoint is exposed.
        }

        var candidates = recent.Events
            .Concat(persisted?.Events ?? [])
            .GroupBy(
                static item => item.EventId,
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderByDescending(static item => item.LastSeenUtc)
            .ThenByDescending(
                static item => item.EventId,
                StringComparer.Ordinal)
            .ToArray();
        var selected = candidates
            .Take(query.MaxEvents)
            .Reverse()
            .ToArray();
        return new DiagnosticsContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            InstanceId = InstanceId,
            Query = query,
            EventCount = selected.Length,
            Truncated = recent.Truncated ||
                persisted?.Truncated == true ||
                candidates.Length > selected.Length,
            Events = selected,
        };
    }

    private static CapabilitiesContract BuildCapabilities(
        string instanceId,
        DiagnosticsCapabilityContract diagnosticsCapabilities,
        IEnumerable<ProviderDescriptor> descriptors,
        PipeEndpoint endpoint,
        TimeSpan snapshotPeriod)
    {
        var groups = descriptors
            .Select(descriptor => new ProviderCapabilityContract
            {
                GroupId = descriptor.GroupId,
                ProviderId = descriptor.ProviderId,
                DefaultPeriodMs = checked((int)Math.Round(
                    descriptor.DefaultPeriod.TotalMilliseconds,
                    MidpointRounding.AwayFromZero)),
                RequiredPrivilege = descriptor.RequiredPrivilege,
                CostClass = descriptor.CostClass,
            })
            .ToList();
        if (groups.All(
            group => group.GroupId != GroupIds.Sampler))
        {
            groups.Add(new ProviderCapabilityContract
            {
                GroupId = GroupIds.Sampler,
                ProviderId = ProviderIds.Sampler,
                DefaultPeriodMs = checked((int)Math.Round(
                    snapshotPeriod.TotalMilliseconds,
                    MidpointRounding.AwayFromZero)),
                RequiredPrivilege = "user",
                CostClass = "low",
            });
        }

        groups.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(
                left.GroupId,
                right.GroupId));
        var baseEndpoint = $"pipe://./{endpoint.PipeName}";
        return new CapabilitiesContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            InstanceId = instanceId,
            Groups = groups,
            History = new HistoryCapabilityContract
            {
                MetricIds = HistoryPolicy.SupportedMetricIds,
                DefaultMaxPoints = HistoryPolicy.DefaultMaxPoints,
                MaxPoints = HistoryPolicy.MaxPoints,
                RamPointLimit = HistoryPolicy.RamPointLimit,
                Aggregations = HistoryPolicy.Aggregations,
            },
            Diagnostics = diagnosticsCapabilities,
            Endpoints = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["snapshot"] = $"{baseEndpoint}/snapshot",
                ["history"] = $"{baseEndpoint}/history",
                ["diagnostics"] = $"{baseEndpoint}/diagnostics",
                ["capabilities"] = $"{baseEndpoint}/capabilities",
                ["health"] = $"{baseEndpoint}/health",
                ["subscribe"] = $"{baseEndpoint}/subscribe",
            },
            StableErrorCodes =
            [
                StableErrorCodes.AccessDenied,
                StableErrorCodes.ProcessExited,
                StableErrorCodes.NotSupported,
                StableErrorCodes.Timeout,
                StableErrorCodes.InvalidData,
                StableErrorCodes.ResourceExhausted,
                StableErrorCodes.ProviderFailure,
                IpcErrorCodes.ContractVersionUnsupported,
                IpcErrorCodes.InvalidRequest,
                IpcErrorCodes.MessageTooLarge,
                IpcErrorCodes.RequestTimedOut,
                IpcErrorCodes.ServiceUnavailable,
            ],
        };
    }
}
