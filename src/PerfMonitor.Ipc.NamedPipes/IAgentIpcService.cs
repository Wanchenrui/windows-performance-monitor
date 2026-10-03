using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Ipc.NamedPipes;

public interface IAgentIpcService
{
    string InstanceId { get; }

    AgentSnapshot ReadLatestSnapshot();

    HealthContract ReadHealth();

    CapabilitiesContract ReadCapabilities();

    LightModeContract ReadLightMode() => LightModeContract.Unsupported(InstanceId);

    LightModeContract SetLightMode(bool enabled, CancellationToken cancellationToken) =>
        LightModeContract.Unsupported(InstanceId);

    ValueTask<HistoryContract> QueryHistoryAsync(
        HistoryQueryContract query,
        CancellationToken cancellationToken);

    ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        CancellationToken cancellationToken);

    ValueTask<ActionResultContract> ExecuteActionAsync(
        UserActionRequestContract request,
        CancellationToken cancellationToken);
}
