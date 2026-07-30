using System.Globalization;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

internal sealed class DiagnosticSnapshotReader
{
    private readonly DiagnosticPolicy _policy;
    private readonly Dictionary<string, DateTimeOffset>
        _previousSnapshotTimes = new(StringComparer.Ordinal);

    public DiagnosticSnapshotReader(DiagnosticPolicy policy)
    {
        _policy = policy;
    }

    public IReadOnlyList<DiagnosticObservation> Read(
        AgentSnapshot snapshot)
    {
        if (snapshot.CompletedAtUtc is null ||
            snapshot.Sequence <= 0)
        {
            return [];
        }

        var observations = new List<DiagnosticObservation>(16);
        AddScalar(
            observations,
            snapshot,
            GroupIds.SystemCpu,
            MetricIds.SystemCpuUtilization,
            "system",
            _policy.HighCpu,
            DiagnosticRuleIds.HighCpu);
        AddScalar(
            observations,
            snapshot,
            GroupIds.Memory,
            MetricIds.MemoryUtilization,
            "system",
            _policy.MemoryPressure,
            DiagnosticRuleIds.MemoryPressure);
        AddSystemDisk(observations, snapshot);
        AddWatchedProcesses(observations, snapshot);
        AddSamplingGap(observations, snapshot);
        AddProviderAvailability(observations, snapshot);
        AddAgentResource(observations, snapshot);

        observations.Sort(
            static (left, right) =>
            {
                var result = left.ObservedAtUtc.CompareTo(
                    right.ObservedAtUtc);
                if (result != 0)
                {
                    return result;
                }

                result = StringComparer.Ordinal.Compare(
                    left.RuleId,
                    right.RuleId);
                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(
                        left.SubjectId,
                        right.SubjectId);
            });
        return observations;
    }

    private static void AddScalar(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot,
        string groupId,
        string metricId,
        string subjectId,
        ThresholdRulePolicy policy,
        string ruleId)
    {
        if (!policy.Enabled ||
            !TryReadScalar(
                snapshot,
                groupId,
                metricId,
                out var observedAtUtc,
                out var value,
                out var unit))
        {
            return;
        }

        destination.Add(ThresholdObservation(
            snapshot.InstanceId,
            ruleId,
            subjectId,
            metricId,
            observedAtUtc,
            value,
            unit,
            policy));
    }

    private void AddSystemDisk(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var policy = _policy.SystemDiskLow;
        if (!policy.Enabled ||
            !snapshot.Groups.TryGetValue(
                GroupIds.Volumes,
                out var group) ||
            !IsReadable(group) ||
            group.Data is not JsonArray volumes)
        {
            return;
        }

        foreach (var node in volumes)
        {
            if (node is not JsonObject volume ||
                !TryReadBoolean(volume["isSystem"], out var isSystem) ||
                !isSystem ||
                volume["metrics"] is not JsonObject metrics ||
                !TryReadMetric(
                    metrics[MetricIds.VolumeUtilization],
                    out var value,
                    out var unit))
            {
                continue;
            }

            destination.Add(ThresholdObservation(
                snapshot.InstanceId,
                DiagnosticRuleIds.SystemDiskLow,
                "system-volume",
                MetricIds.VolumeUtilization,
                group.ObservedAtUtc ??
                    snapshot.CompletedAtUtc!.Value,
                value,
                unit,
                policy));
            return;
        }
    }

    private void AddWatchedProcesses(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var policy = _policy.ProcessCpuSpike;
        if (!policy.Enabled ||
            _policy.WatchedProcessNames.Count == 0 ||
            !snapshot.Groups.TryGetValue(
                GroupIds.Processes,
                out var group) ||
            !IsReadable(group) ||
            group.Data is not JsonArray processes)
        {
            return;
        }

        var maxima = new Dictionary<string, double?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var watched in _policy.WatchedProcessNames)
        {
            maxima[watched] = null;
        }

        foreach (var node in processes)
        {
            if (node is not JsonObject process ||
                !TryReadString(process["name"], out var name) ||
                !maxima.ContainsKey(name) ||
                process["metrics"] is not JsonObject metrics ||
                !TryReadMetric(
                    metrics[MetricIds.ProcessCpuNormalized],
                    out var value,
                    out _))
            {
                continue;
            }

            maxima[name] = maxima[name] is { } current
                ? Math.Max(current, value)
                : value;
        }

        var observedAt = group.ObservedAtUtc ??
            snapshot.CompletedAtUtc!.Value;
        foreach (var watched in _policy.WatchedProcessNames)
        {
            var value = maxima[watched] ?? 0;
            destination.Add(ThresholdObservation(
                snapshot.InstanceId,
                DiagnosticRuleIds.ProcessCpuSpike,
                $"process:{watched}",
                MetricIds.ProcessCpuNormalized,
                observedAt,
                value,
                Units.Percent,
                policy));
        }
    }

    private void AddSamplingGap(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var policy = _policy.SamplingGap;
        var current = snapshot.CompletedAtUtc!.Value;
        if (_previousSnapshotTimes.TryGetValue(
                snapshot.InstanceId,
                out var previous) &&
            current > previous &&
            policy.Enabled)
        {
            var gapSeconds = (current - previous).TotalSeconds;
            destination.Add(ThresholdObservation(
                snapshot.InstanceId,
                DiagnosticRuleIds.SamplingGap,
                "sampler",
                "snapshot.completed_at.gap_seconds",
                current,
                gapSeconds,
                Units.Second,
                policy));
        }

        if (!_previousSnapshotTimes.TryGetValue(
                snapshot.InstanceId,
                out previous) ||
            current > previous)
        {
            _previousSnapshotTimes[snapshot.InstanceId] = current;
        }
    }

    private void AddProviderAvailability(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var policy = _policy.ProviderUnavailable;
        if (!policy.Enabled)
        {
            return;
        }

        foreach (var entry in snapshot.Groups
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (entry.Key == GroupIds.Sampler ||
                entry.Value.Availability ==
                    AvailabilityStates.NotSupported)
            {
                continue;
            }

            var breach = entry.Value.Availability is
                AvailabilityStates.Unavailable or
                AvailabilityStates.PermissionDenied or
                AvailabilityStates.Timeout or
                AvailabilityStates.Error;
            var recovery = entry.Value.Availability is
                AvailabilityStates.Available or
                AvailabilityStates.Partial;
            if (!breach && !recovery)
            {
                continue;
            }

            destination.Add(new DiagnosticObservation(
                snapshot.InstanceId,
                DiagnosticRuleIds.ProviderUnavailable,
                policy.RuleVersion,
                policy.Severity,
                $"provider:{entry.Key}",
                "provider.availability",
                entry.Value.ObservedAtUtc ??
                    snapshot.CompletedAtUtc!.Value,
                null,
                null,
                breach,
                recovery,
                "provider remains unavailable",
                "provider remains available",
                policy.ActivateDebounceSeconds,
                policy.RecoverDebounceSeconds,
                policy.CooldownSeconds,
                policy.EvidenceWindowSeconds,
                policy.MaxObservationGapSeconds));
        }
    }

    private void AddAgentResource(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var policy = _policy.AgentResourceAnomaly;
        if (!policy.Enabled ||
            !TryReadScalar(
                snapshot,
                GroupIds.Self,
                MetricIds.AgentCpuCoreEquivalent,
                out var cpuObservedAt,
                out var cpu,
                out _) ||
            !TryReadScalar(
                snapshot,
                GroupIds.Self,
                MetricIds.AgentPrivateBytes,
                out var privateObservedAt,
                out var privateBytes,
                out _))
        {
            return;
        }

        var observedAt = cpuObservedAt >= privateObservedAt
            ? cpuObservedAt
            : privateObservedAt;
        var cpuRatio = policy.ActivateCpuCoreEquivalentPercent > 0
            ? cpu / policy.ActivateCpuCoreEquivalentPercent
            : 0;
        var privateRatio = policy.ActivatePrivateBytes > 0
            ? privateBytes / policy.ActivatePrivateBytes
            : 0;
        var breach =
            cpu >= policy.ActivateCpuCoreEquivalentPercent ||
            privateBytes >= policy.ActivatePrivateBytes;
        var recovery =
            cpu <= policy.RecoverCpuCoreEquivalentPercent &&
            privateBytes <= policy.RecoverPrivateBytes;
        destination.Add(new DiagnosticObservation(
            snapshot.InstanceId,
            DiagnosticRuleIds.AgentResourceAnomaly,
            policy.RuleVersion,
            policy.Severity,
            "agent",
            "agent.cpu_or_private_memory.ratio",
            observedAt,
            Math.Max(cpuRatio, privateRatio),
            "ratio",
            breach,
            recovery,
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu >= {policy.ActivateCpuCoreEquivalentPercent:R}% OR private >= {policy.ActivatePrivateBytes} B"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu <= {policy.RecoverCpuCoreEquivalentPercent:R}% AND private <= {policy.RecoverPrivateBytes} B"),
            policy.ActivateDebounceSeconds,
            policy.RecoverDebounceSeconds,
            policy.CooldownSeconds,
            policy.EvidenceWindowSeconds,
            policy.MaxObservationGapSeconds));
    }

    private static DiagnosticObservation ThresholdObservation(
        string instanceId,
        string ruleId,
        string subjectId,
        string signal,
        DateTimeOffset observedAtUtc,
        double value,
        string unit,
        ThresholdRulePolicy policy) =>
        new(
            instanceId,
            ruleId,
            policy.RuleVersion,
            policy.Severity,
            subjectId,
            signal,
            observedAtUtc,
            value,
            unit,
            value >= policy.ActivateAtOrAbove,
            value <= policy.RecoverAtOrBelow,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{signal} >= {policy.ActivateAtOrAbove:R}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{signal} <= {policy.RecoverAtOrBelow:R}"),
            policy.ActivateDebounceSeconds,
            policy.RecoverDebounceSeconds,
            policy.CooldownSeconds,
            policy.EvidenceWindowSeconds,
            policy.MaxObservationGapSeconds);

    private static bool TryReadScalar(
        AgentSnapshot snapshot,
        string groupId,
        string metricId,
        out DateTimeOffset observedAtUtc,
        out double value,
        out string unit)
    {
        observedAtUtc = default;
        value = default;
        unit = string.Empty;
        if (!snapshot.Groups.TryGetValue(groupId, out var group) ||
            !IsReadable(group) ||
            group.Data is not JsonObject data ||
            data["metrics"] is not JsonObject metrics ||
            !TryReadMetric(metrics[metricId], out value, out unit))
        {
            return false;
        }

        observedAtUtc = group.ObservedAtUtc ??
            snapshot.CompletedAtUtc!.Value;
        return true;
    }

    private static bool IsReadable(SnapshotGroup group) =>
        group.Availability is
            AvailabilityStates.Available or
            AvailabilityStates.Partial;

    private static bool TryReadMetric(
        JsonNode? node,
        out double value,
        out string unit)
    {
        value = default;
        unit = string.Empty;
        if (node is not JsonObject metric ||
            !TryReadFiniteDouble(metric["value"], out value) ||
            !TryReadString(metric["unit"], out unit))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadFiniteDouble(
        JsonNode? node,
        out double value)
    {
        value = default;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out value))
        {
            return double.IsFinite(value);
        }

        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            value = longValue;
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }

        return false;
    }

    private static bool TryReadString(
        JsonNode? node,
        out string value)
    {
        value = string.Empty;
        return node is JsonValue jsonValue &&
            jsonValue.TryGetValue(out value) &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadBoolean(
        JsonNode? node,
        out bool value)
    {
        value = default;
        return node is JsonValue jsonValue &&
            jsonValue.TryGetValue(out value);
    }
}
