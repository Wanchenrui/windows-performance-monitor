using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Worker;

public static class HardwareWorkerProviderFactory
{
    public static IReadOnlyList<IMetricProvider> CreateDefault()
    {
        var options = HardwareWorkerOptions.Default;
        var sessionFactory =
            new ProcessWorkerSessionFactory(options);
        var client = new HardwareWorkerClient(
            options,
            sessionFactory);
        return Create(client);
    }

    internal static IReadOnlyList<IMetricProvider> Create(
        IHardwareWorkerClient client)
    {
        const int providerCount = 2;
        var coordinator = new HardwareWorkerCoordinator(
            client,
            providerCount);
        return
        [
            new GpuWorkerProvider(coordinator),
            new TemperatureWorkerProvider(coordinator),
        ];
    }
}
