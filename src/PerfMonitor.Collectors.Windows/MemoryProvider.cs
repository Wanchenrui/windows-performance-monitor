using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class MemoryProvider : IMetricProvider
{
    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Memory,
        ProviderIds.Memory,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(500),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = NativeMethods.MemoryStatusEx.Create();
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var total = checked((long)status.TotalPhysical);
        var available = checked((long)status.AvailablePhysical);
        var used = Math.Max(0, total - available);
        var utilization = CalculateUtilizationPercent(total, available);
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.MemoryUtilization] = MetricJson.Value(
                    utilization,
                    Units.Percent,
                    SourceIds.Memory),
                [MetricIds.MemoryUsedBytes] = MetricJson.Value(
                    used,
                    Units.Byte,
                    SourceIds.Memory),
                [MetricIds.MemoryAvailableBytes] = MetricJson.Value(
                    available,
                    Units.Byte,
                    SourceIds.Memory),
                [MetricIds.MemoryTotalBytes] = MetricJson.Value(
                    total,
                    Units.Byte,
                    SourceIds.Memory),
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

    public static double CalculateUtilizationPercent(
        long totalBytes,
        long availableBytes)
    {
        if (totalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        var used = Math.Clamp(totalBytes - availableBytes, 0, totalBytes);
        return Math.Clamp(100.0 * used / totalBytes, 0, 100);
    }
}
