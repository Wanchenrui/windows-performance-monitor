using PerfMonitor.Collectors.Windows;
using PerfMonitor.Collectors.Worker;
using PerfMonitor.Core;

namespace PerfMonitor.Agent;

internal static class AgentProviderFactory
{
    public static IReadOnlyList<IMetricProvider> CreateDefault()
    {
        var providers = new List<IMetricProvider>();
        providers.AddRange(
            WindowsProviderFactory.CreateDefault());
        providers.AddRange(
            HardwareWorkerProviderFactory.CreateDefault());
        return providers;
    }
}
