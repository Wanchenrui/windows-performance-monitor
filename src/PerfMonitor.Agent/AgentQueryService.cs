using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.Agent;

internal sealed class AgentQueryService : IAgentIpcService
{
    private readonly SnapshotAssembler _assembler;
    private readonly IHistoryReader _history;
    private readonly CapabilitiesContract _capabilities;

    public AgentQueryService(
        SnapshotAssembler assembler,
        IHistoryReader history,
        IEnumerable<ProviderDescriptor> descriptors,
        PipeEndpoint endpoint,
        TimeSpan snapshotPeriod)
    {
        _assembler = assembler;
        _history = history;
        _capabilities = BuildCapabilities(
            assembler.InstanceId,
            descriptors,
            endpoint,
            snapshotPeriod);
    }

    public string InstanceId => _assembler.InstanceId;

    public AgentSnapshot ReadLatestSnapshot() => _assembler.Read();

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

    private static CapabilitiesContract BuildCapabilities(
        string instanceId,
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
            Endpoints = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["snapshot"] = $"{baseEndpoint}/snapshot",
                ["history"] = $"{baseEndpoint}/history",
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
