using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class UptimeProvider : IMetricProvider
{
    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Uptime,
        ProviderIds.Uptime,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(250),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uptimeSeconds = checked(
            (long)(NativeMethods.GetTickCount64() / 1000));
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.UptimeSeconds] = MetricJson.Value(
                    uptimeSeconds,
                    Units.Second,
                    SourceIds.Uptime),
            },
        };
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                AvailabilityStates.Available,
                ProviderCoverage.Complete,
                [],
                data));
    }
}
