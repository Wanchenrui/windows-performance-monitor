using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class ProcessProvider : IMetricProvider
{
    private Dictionary<ProcessIdentity, ProcessBaseline> _previous = [];

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Processes,
        ProviderIds.Processes,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(1500),
        "user",
        "medium");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        var processes = Process.GetProcesses();
        var rows = new List<ProcessRow>(processes.Length);
        var current = new Dictionary<ProcessIdentity, ProcessBaseline>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var enumerated = 0;

        foreach (var process in processes)
        {
            using (process)
            {
                enumerated++;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var pid = process.Id;
                    if (pid == 0)
                    {
                        // v0.4.0: Idle 是伪进程，不属于可查询进程覆盖率。
                        enumerated--;
                        continue;
                    }

                    var identity = new ProcessIdentity(
                        pid,
                        process.StartTime.ToUniversalTime().ToFileTimeUtc());
                    var totalProcessorTicks = process.TotalProcessorTime.Ticks;
                    var baseline = new ProcessBaseline(
                        totalProcessorTicks,
                        context.Timestamp);
                    current[identity] = baseline;
                    double? normalized = null;
                    double? coreEquivalent = null;
                    if (_previous.TryGetValue(identity, out var previous))
                    {
                        var elapsed = context.TimeProvider.GetElapsedTime(
                            previous.Timestamp,
                            context.Timestamp).TotalSeconds;
                        (normalized, coreEquivalent) =
                            CalculateCpuPercentages(
                                totalProcessorTicks - previous.TotalProcessorTicks,
                                elapsed,
                                context.LogicalProcessorCount);
                    }

                    rows.Add(
                        new ProcessRow(
                            identity,
                            process.ProcessName,
                            normalized is not null,
                            normalized,
                            coreEquivalent,
                            process.WorkingSet64,
                            process.PrivateMemorySize64));
                }
                catch (Exception exception) when (
                    exception is Win32Exception or
                    InvalidOperationException or
                    NotSupportedException or
                    UnauthorizedAccessException or
                    ArgumentException)
                {
                    Increment(reasons, ClassifyProcessException(exception));
                }
            }
        }

        _previous = current;
        rows.Sort(
            static (left, right) =>
                left.Identity.Pid.CompareTo(right.Identity.Pid));
        var data = new JsonArray();
        foreach (var row in rows)
        {
            data.Add(ToJson(row));
        }

        var readable = rows.Count;
        var skipped = Math.Max(0, enumerated - readable);
        var availability = readable == 0
            ? AvailabilityStates.Error
            : skipped == 0
                ? AvailabilityStates.Available
                : AvailabilityStates.Partial;
        var coverage = new ProviderCoverage(
            skipped == 0 ? "complete" : "limited",
            enumerated,
            readable,
            skipped,
            MetricJson.ReadOnlyReasons(reasons));
        var errors = reasons.Keys
            .Order(StringComparer.Ordinal)
            .Select(code => new ProviderError(code, null))
            .ToArray();
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                availability,
                coverage,
                errors,
                readable == 0 ? null : data));
    }

    public static (double? Normalized, double? CoreEquivalent)
        CalculateCpuPercentages(
            long deltaProcessorTicks,
            double elapsedSeconds,
            int logicalProcessorCount)
    {
        if (deltaProcessorTicks < 0 ||
            elapsedSeconds <= 0 ||
            logicalProcessorCount <= 0)
        {
            return (null, null);
        }

        var processorSeconds =
            deltaProcessorTicks / (double)TimeSpan.TicksPerSecond;
        var coreEquivalent = Math.Max(
            0,
            100.0 * processorSeconds / elapsedSeconds);
        var normalized = Math.Clamp(
            coreEquivalent / logicalProcessorCount,
            0,
            100);
        return (normalized, coreEquivalent);
    }

    private static JsonObject ToJson(ProcessRow row) =>
        new()
        {
            ["identity"] = new JsonObject
            {
                ["pid"] = row.Identity.Pid,
                ["creationTimeTicks"] = row.Identity.CreationTimeTicks,
            },
            ["name"] = row.Name,
            ["cpuReady"] = row.CpuReady,
            ["metrics"] = new JsonObject
            {
                [MetricIds.ProcessCpuNormalized] = MetricJson.Value(
                    row.CpuNormalizedPercent,
                    Units.Percent,
                    SourceIds.ProcessCpu),
                [MetricIds.ProcessCpuCoreEquivalent] = MetricJson.Value(
                    row.CpuCoreEquivalentPercent,
                    Units.Percent,
                    SourceIds.ProcessCpu),
                [MetricIds.ProcessWorkingSetBytes] = MetricJson.Value(
                    row.WorkingSetBytes,
                    Units.Byte,
                    SourceIds.ProcessMemory),
                [MetricIds.ProcessPrivateBytes] = MetricJson.Value(
                    row.PrivateBytes,
                    Units.Byte,
                    SourceIds.ProcessMemory),
            },
        };

    private static string ClassifyProcessException(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                StableErrorCodes.AccessDenied,
            Win32Exception { NativeErrorCode: 5 } =>
                StableErrorCodes.AccessDenied,
            InvalidOperationException or ArgumentException =>
                StableErrorCodes.ProcessExited,
            NotSupportedException =>
                StableErrorCodes.NotSupported,
            _ => StableErrorCodes.ProviderFailure,
        };

    private static void Increment(
        IDictionary<string, int> reasons,
        string reason)
    {
        reasons.TryGetValue(reason, out var count);
        reasons[reason] = count + 1;
    }

    public readonly record struct ProcessIdentity(
        int Pid,
        long CreationTimeTicks);

    private readonly record struct ProcessBaseline(
        long TotalProcessorTicks,
        long Timestamp);

    private sealed record ProcessRow(
        ProcessIdentity Identity,
        string Name,
        bool CpuReady,
        double? CpuNormalizedPercent,
        double? CpuCoreEquivalentPercent,
        long WorkingSetBytes,
        long PrivateBytes);
}
