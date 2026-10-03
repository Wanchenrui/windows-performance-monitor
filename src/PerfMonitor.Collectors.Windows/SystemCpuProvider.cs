using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class SystemCpuProvider : IMetricProvider
{
    private CpuCounters? _previous;

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.SystemCpu,
        ProviderIds.SystemCpu,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(750),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.GetSystemTimes(
                out var idle,
                out var kernel,
                out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var current = new CpuCounters(
            idle.ToUInt64(),
            kernel.ToUInt64(),
            user.ToUInt64());
        double? utilization = null;
        if (_previous is not null)
        {
            utilization = CalculateUtilizationPercent(
                _previous,
                current);
        }

        _previous = current;
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.SystemCpuUtilization] = MetricJson.Value(
                    utilization,
                    Units.Percent,
                    SourceIds.SystemCpu),
                [MetricIds.LogicalProcessorCount] = MetricJson.Value(
                    context.LogicalProcessorCount,
                    Units.Count,
                    SourceIds.SystemCpu),
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

    public static double? CalculateUtilizationPercent(
        CpuCounters previous,
        CpuCounters current)
    {
        if (current.Idle < previous.Idle ||
            current.Kernel < previous.Kernel ||
            current.User < previous.User)
        {
            return null;
        }

        var idleDelta = current.Idle - previous.Idle;
        var kernelDelta = current.Kernel - previous.Kernel;
        var userDelta = current.User - previous.User;
        var totalDelta = kernelDelta + userDelta;
        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return null;
        }

        var busyDelta = totalDelta - idleDelta;
        return Math.Clamp(
            100.0 * busyDelta / totalDelta,
            0,
            100);
    }

    public sealed record CpuCounters(
        ulong Idle,
        ulong Kernel,
        ulong User);
}
