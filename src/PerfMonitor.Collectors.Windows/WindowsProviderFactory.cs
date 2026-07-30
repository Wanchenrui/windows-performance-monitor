using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public static class WindowsProviderFactory
{
    public static IReadOnlyList<IMetricProvider> CreateDefault() =>
        [
            new SystemCpuProvider(),
            new MemoryProvider(),
            new NetworkThroughputProvider(),
            new DiskIoProvider(),
            new PowerStatusProvider(),
            new VolumeCapacityProvider(),
            new UptimeProvider(),
            new ProcessProvider(),
            new SelfMetricsProvider(),
        ];
}
