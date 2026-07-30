using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public static class WindowsProviderFactory
{
    public static IReadOnlyList<IMetricProvider> CreateDefault() =>
        [
            new SystemCpuProvider(),
            new MemoryProvider(),
            new VolumeCapacityProvider(),
            new UptimeProvider(),
            new ProcessProvider(),
            new SelfMetricsProvider(),
        ];
}
