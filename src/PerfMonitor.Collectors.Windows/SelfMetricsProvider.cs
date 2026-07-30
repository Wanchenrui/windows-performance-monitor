using System.Diagnostics;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class SelfMetricsProvider : IMetricProvider
{
    private long? _previousProcessorTicks;
    private long? _previousTimestamp;

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Self,
        ProviderIds.Self,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(500),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.GetCurrentProcess();
        var processorTicks = process.TotalProcessorTime.Ticks;
        double? coreEquivalent = null;
        if (_previousProcessorTicks is not null &&
            _previousTimestamp is not null)
        {
            var elapsed = context.TimeProvider.GetElapsedTime(
                _previousTimestamp.Value,
                context.Timestamp).TotalSeconds;
            (_, coreEquivalent) = ProcessProvider.CalculateCpuPercentages(
                processorTicks - _previousProcessorTicks.Value,
                elapsed,
                context.LogicalProcessorCount);
        }

        _previousProcessorTicks = processorTicks;
        _previousTimestamp = context.Timestamp;
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.AgentCpuCoreEquivalent] = MetricJson.Value(
                    coreEquivalent,
                    Units.Percent,
                    SourceIds.SelfProcess),
                [MetricIds.AgentWorkingSetBytes] = MetricJson.Value(
                    process.WorkingSet64,
                    Units.Byte,
                    SourceIds.SelfProcess),
                [MetricIds.AgentPrivateBytes] = MetricJson.Value(
                    process.PrivateMemorySize64,
                    Units.Byte,
                    SourceIds.SelfProcess),
                [MetricIds.AgentGcHeapBytes] = MetricJson.Value(
                    GC.GetTotalMemory(forceFullCollection: false),
                    Units.Byte,
                    SourceIds.DotnetGc),
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
