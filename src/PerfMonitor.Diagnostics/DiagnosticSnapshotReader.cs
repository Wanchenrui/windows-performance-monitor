using System.Globalization;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

internal sealed class DiagnosticSnapshotReader(DiagnosticPolicy policy)
{
    private string? _previousInstanceId;
    private double? _previousElapsedSeconds;

    public void Reset()
    {
        _previousInstanceId = null;
        _previousElapsedSeconds = null;
    }

    public IReadOnlyList<DiagnosticObservation> Read(AgentSnapshot snapshot)
    {
        if (snapshot.ElapsedSeconds is not { } elapsed ||
            !double.IsFinite(elapsed) || elapsed < 0 ||
            snapshot.CompletedAtUtc is null || snapshot.Sequence <= 0)
        {
            _previousElapsedSeconds = null;
            return [];
        }

        var observations = new List<DiagnosticObservation>(16);
        AddScalar(observations, snapshot, GroupIds.SystemCpu,
            MetricIds.SystemCpuUtilization, Units.Percent, "system",
            policy.HighCpu, DiagnosticRuleIds.HighCpu);
        AddScalar(observations, snapshot, GroupIds.Memory,
            MetricIds.MemoryUtilization, Units.Percent, "system",
            policy.MemoryPressure, DiagnosticRuleIds.MemoryPressure);
        AddSystemDisk(observations, snapshot);
        AddWatchedProcesses(observations, snapshot);
        AddSamplingGap(observations, snapshot);
        AddProviderAvailability(observations, snapshot);
        AddAgentResource(observations, snapshot);
        observations.Sort(static (left, right) =>
        {
            var result = left.ElapsedSeconds.CompareTo(right.ElapsedSeconds);
            if (result != 0)
            {
                return result;
            }

            result = StringComparer.Ordinal.Compare(left.RuleId, right.RuleId);
            return result != 0 ? result :
                StringComparer.Ordinal.Compare(left.SubjectId, right.SubjectId);
        });
        return observations;
    }

    private static void AddScalar(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot,
        string groupId,
        string metricId,
        string expectedUnit,
        string subjectId,
        ThresholdRulePolicy rulePolicy,
        string ruleId)
    {
        if (rulePolicy.Enabled &&
            TryReadScalar(snapshot, groupId, metricId, expectedUnit,
                out var group, out var value))
        {
            destination.Add(ThresholdObservation(snapshot, group, ruleId,
                subjectId, metricId, value, expectedUnit, rulePolicy));
        }
    }

    private void AddSystemDisk(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        if (!policy.SystemDiskLow.Enabled ||
            !snapshot.Groups.TryGetValue(GroupIds.Volumes, out var group) ||
            !IsReadable(snapshot, group) || group.Data is not JsonArray volumes)
        {
            return;
        }

        foreach (var node in volumes)
        {
            if (node is JsonObject volume &&
                volume["isSystem"] is JsonValue system &&
                system.TryGetValue<bool>(out var isSystem) && isSystem &&
                volume["metrics"] is JsonObject metrics &&
                TryReadMetric(metrics[MetricIds.VolumeUtilization],
                    Units.Percent, out var value))
            {
                destination.Add(ThresholdObservation(snapshot, group,
                    DiagnosticRuleIds.SystemDiskLow, "system-volume",
                    MetricIds.VolumeUtilization, value, Units.Percent,
                    policy.SystemDiskLow));
                return;
            }
        }
    }

    private void AddWatchedProcesses(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        if (!policy.ProcessCpuSpike.Enabled ||
            policy.WatchedProcessNames.Count == 0 ||
            !snapshot.Groups.TryGetValue(GroupIds.Processes, out var group) ||
            !IsReadable(snapshot, group))
        {
            return;
        }

        foreach (var aggregate in DiagnosticProcessProjection.Read(
            group, policy.WatchedProcessNames))
        {
            var value = aggregate.CpuPercent;
            // A partial enumeration can establish a high value, but cannot
            // establish recovery for the maximum of every matching process.
            var known = aggregate.ContinuityId is not null &&
                value is not null && (aggregate.Complete ||
                    value >= policy.ProcessCpuSpike.ActivateAtOrAbove);
            destination.Add(ThresholdObservation(snapshot, group,
                DiagnosticRuleIds.ProcessCpuSpike,
                $"process:{aggregate.Name}", MetricIds.ProcessCpuNormalized,
                value, Units.Percent, policy.ProcessCpuSpike) with
            {
                IsKnown = known,
                ContinuityId = aggregate.ContinuityId,
            });
        }
    }

    private void AddSamplingGap(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var sampler = snapshot.Groups.GetValueOrDefault(GroupIds.Sampler);
        if (sampler is not null && !IsFresh(snapshot, sampler))
        {
            return;
        }

        var current = sampler?.ObservedElapsedSeconds ?? snapshot.ElapsedSeconds!.Value;
        if (StringComparer.Ordinal.Equals(
                _previousInstanceId, snapshot.InstanceId) &&
            _previousElapsedSeconds is { } previous && current >= previous &&
            policy.SamplingGap.Enabled)
        {
            var group = sampler ?? new SnapshotGroup(ProviderIds.Sampler,
                snapshot.CompletedAtUtc, AvailabilityStates.Available,
                FreshnessStates.Fresh, ProviderCoverage.Complete, [], null)
            {
                ObservationSequence = snapshot.Sequence,
                ObservedElapsedSeconds = current,
            };
            destination.Add(ThresholdObservation(snapshot, group,
                DiagnosticRuleIds.SamplingGap, "sampler",
                "snapshot.completed_at.gap_seconds", current - previous,
                Units.Second, policy.SamplingGap));
        }

        _previousInstanceId = snapshot.InstanceId;
        _previousElapsedSeconds = current;
    }

    private void AddProviderAvailability(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        if (!policy.ProviderUnavailable.Enabled)
        {
            return;
        }

        var rulePolicy = policy.ProviderUnavailable;
        foreach (var entry in snapshot.Groups.OrderBy(
            static entry => entry.Key, StringComparer.Ordinal))
        {
            var group = entry.Value;
            if (entry.Key == GroupIds.Sampler || !IsFresh(snapshot, group) ||
                group.Availability == AvailabilityStates.NotSupported)
            {
                continue;
            }

            var breach = group.Availability is AvailabilityStates.Unavailable or
                AvailabilityStates.PermissionDenied or AvailabilityStates.Timeout or
                AvailabilityStates.Error;
            var recovery = group.Availability is AvailabilityStates.Available or
                AvailabilityStates.Partial;
            if (breach || recovery)
            {
                destination.Add(new DiagnosticObservation(snapshot.InstanceId,
                    DiagnosticRuleIds.ProviderUnavailable, rulePolicy.RuleVersion,
                    rulePolicy.Severity, $"provider:{entry.Key}",
                    "provider.availability", group.ObservedAtUtc!.Value,
                    null, null, breach, recovery, "provider remains unavailable",
                    "provider remains available", rulePolicy.ActivateDebounceSeconds,
                    rulePolicy.RecoverDebounceSeconds, rulePolicy.CooldownSeconds,
                    rulePolicy.EvidenceWindowSeconds, rulePolicy.MaxObservationGapSeconds)
                {
                    ElapsedSeconds = group.ObservedElapsedSeconds!.Value,
                    ObservationSequence = group.ObservationSequence!.Value,
                });
            }
        }
    }

    private void AddAgentResource(
        ICollection<DiagnosticObservation> destination,
        AgentSnapshot snapshot)
    {
        var rulePolicy = policy.AgentResourceAnomaly;
        if (!rulePolicy.Enabled ||
            !TryReadScalar(snapshot, GroupIds.Self, MetricIds.AgentCpuCoreEquivalent,
                Units.Percent, out var group, out var cpu) ||
            !TryReadScalar(snapshot, GroupIds.Self, MetricIds.AgentPrivateBytes,
                Units.Byte, out _, out var privateBytes))
        {
            return;
        }

        var cpuRatio = cpu / rulePolicy.ActivateCpuCoreEquivalentPercent;
        var privateRatio = privateBytes / rulePolicy.ActivatePrivateBytes;
        destination.Add(new DiagnosticObservation(snapshot.InstanceId,
            DiagnosticRuleIds.AgentResourceAnomaly, rulePolicy.RuleVersion,
            rulePolicy.Severity, "agent", "agent.cpu_or_private_memory.ratio",
            group.ObservedAtUtc!.Value, Math.Max(cpuRatio, privateRatio), "ratio",
            cpu >= rulePolicy.ActivateCpuCoreEquivalentPercent ||
                privateBytes >= rulePolicy.ActivatePrivateBytes,
            cpu <= rulePolicy.RecoverCpuCoreEquivalentPercent &&
                privateBytes <= rulePolicy.RecoverPrivateBytes,
            string.Create(CultureInfo.InvariantCulture,
                $"cpu >= {rulePolicy.ActivateCpuCoreEquivalentPercent:R}% OR private >= {rulePolicy.ActivatePrivateBytes} B"),
            string.Create(CultureInfo.InvariantCulture,
                $"cpu <= {rulePolicy.RecoverCpuCoreEquivalentPercent:R}% AND private <= {rulePolicy.RecoverPrivateBytes} B"),
            rulePolicy.ActivateDebounceSeconds, rulePolicy.RecoverDebounceSeconds,
            rulePolicy.CooldownSeconds, rulePolicy.EvidenceWindowSeconds,
            rulePolicy.MaxObservationGapSeconds)
        {
            ElapsedSeconds = group.ObservedElapsedSeconds!.Value,
            ObservationSequence = group.ObservationSequence!.Value,
        });
    }

    private static DiagnosticObservation ThresholdObservation(
        AgentSnapshot snapshot,
        SnapshotGroup group,
        string ruleId,
        string subjectId,
        string signal,
        double? value,
        string unit,
        ThresholdRulePolicy rulePolicy) =>
        new(snapshot.InstanceId, ruleId, rulePolicy.RuleVersion,
            rulePolicy.Severity, subjectId, signal, group.ObservedAtUtc!.Value,
            value, unit, value >= rulePolicy.ActivateAtOrAbove,
            value <= rulePolicy.RecoverAtOrBelow,
            string.Create(CultureInfo.InvariantCulture,
                $"{signal} >= {rulePolicy.ActivateAtOrAbove:R}"),
            string.Create(CultureInfo.InvariantCulture,
                $"{signal} <= {rulePolicy.RecoverAtOrBelow:R}"),
            rulePolicy.ActivateDebounceSeconds, rulePolicy.RecoverDebounceSeconds,
            rulePolicy.CooldownSeconds, rulePolicy.EvidenceWindowSeconds,
            rulePolicy.MaxObservationGapSeconds)
        {
            ElapsedSeconds = group.ObservedElapsedSeconds!.Value,
            ObservationSequence = group.ObservationSequence!.Value,
        };

    private static bool TryReadScalar(
        AgentSnapshot snapshot,
        string groupId,
        string metricId,
        string unit,
        out SnapshotGroup group,
        out double value)
    {
        value = 0;
        group = null!;
        return snapshot.Groups.TryGetValue(groupId, out group!) &&
            IsReadable(snapshot, group) && group.Data is JsonObject data &&
            data["metrics"] is JsonObject metrics &&
            TryReadMetric(metrics[metricId], unit, out value);
    }

    private static bool IsReadable(AgentSnapshot snapshot, SnapshotGroup group) =>
        IsFresh(snapshot, group) &&
        group.Availability is AvailabilityStates.Available or AvailabilityStates.Partial;

    private static bool IsFresh(AgentSnapshot snapshot, SnapshotGroup group) =>
        group.Freshness == FreshnessStates.Fresh && group.ObservedAtUtc is not null &&
        group.ObservationSequence is > 0 && group.ObservationSequence <= snapshot.Sequence &&
        group.ObservedElapsedSeconds is { } elapsed && double.IsFinite(elapsed) &&
        elapsed >= 0 && elapsed <= snapshot.ElapsedSeconds;

    private static bool TryReadMetric(JsonNode? node, string expectedUnit, out double value)
    {
        value = 0;
        if (node is not JsonObject metric ||
            metric["unit"] is not JsonValue unit ||
            !unit.TryGetValue<string>(out var actualUnit) || actualUnit != expectedUnit ||
            metric["value"] is not JsonValue number)
        {
            return false;
        }

        if (number.TryGetValue<double>(out value))
        {
            return double.IsFinite(value);
        }

        if (number.TryGetValue<long>(out var integer))
        {
            value = integer;
            return true;
        }

        if (number.TryGetValue<int>(out var smallInteger))
        {
            value = smallInteger;
            return true;
        }

        return false;
    }
}
