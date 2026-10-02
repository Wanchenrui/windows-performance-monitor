using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Storage.Sqlite;

internal sealed record PersistedMetric(
    string MetricId,
    string Unit,
    double? Value,
    string GroupId,
    string SourceId,
    SnapshotGroup Group,
    string ObservationKey);

internal static class SnapshotMetricExtractor
{
    private static readonly HashSet<string> AllowedMetricIds =
        new(HistoryPolicy.SupportedMetricIds, StringComparer.Ordinal);

    public static IReadOnlyList<PersistedMetric> Extract(
        AgentSnapshot snapshot)
    {
        var metrics = new List<PersistedMetric>();
        foreach (var (groupId, group) in snapshot.Groups)
        {
            if (group.ObservedAtUtc is null || group.Data is not JsonObject data ||
                data["metrics"] is not JsonObject metricObject)
            {
                continue;
            }

            foreach (var entry in metricObject)
            {
                if (!AllowedMetricIds.Contains(entry.Key) ||
                    entry.Value is not JsonObject metric ||
                    metric["unit"] is not JsonValue unitNode ||
                    !unitNode.TryGetValue<string>(out var unit) ||
                    metric["sourceId"] is not JsonValue sourceNode ||
                    !sourceNode.TryGetValue<string>(out var source))
                {
                    continue;
                }

                var usable = group.Availability is (AvailabilityStates.Available or AvailabilityStates.Partial) &&
                    group.Freshness == FreshnessStates.Fresh &&
                    !group.Errors.Any(error => error.MetricId == entry.Key) &&
                    !(data["sampleReady"] is JsonValue ready && ready.TryGetValue<bool>(out var isReady) && !isReady);
                double? value = usable && TryReadFiniteDouble(metric["value"], out var number) ? number : null;
                var identity = group.ObservationSequence is { } sequence
                    ? $"sequence:{sequence}"
                    : $"utc:{group.ObservedAtUtc.Value.UtcTicks}";
                metrics.Add(new(entry.Key, unit, value, groupId, source, group, identity));
            }
        }

        return metrics;
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

        if (jsonValue.TryGetValue<double>(out value) ||
            jsonValue.TryGetValue<long>(out var longValue) &&
            Assign(longValue, out value) ||
            jsonValue.TryGetValue<int>(out var intValue) &&
            Assign(intValue, out value))
        {
            return double.IsFinite(value);
        }

        return false;
    }

    private static bool Assign(long source, out double value)
    {
        value = source;
        return true;
    }
}
