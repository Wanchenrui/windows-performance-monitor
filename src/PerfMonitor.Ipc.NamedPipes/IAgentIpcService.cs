using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Ipc.NamedPipes;

public interface IAgentIpcService
{
    string InstanceId { get; }

    AgentSnapshot ReadLatestSnapshot();

    CapabilitiesContract ReadCapabilities();

    ValueTask<HistoryContract> QueryHistoryAsync(
        HistoryQueryContract query,
        CancellationToken cancellationToken);
}
