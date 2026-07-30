using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Storage.Sqlite;

internal sealed record PersistedMetric(
    string MetricId,
    string Unit,
    double Value);

internal static class SnapshotMetricExtractor
{
    private static readonly HashSet<string> AllowedMetricIds =
        new(HistoryPolicy.SupportedMetricIds, StringComparer.Ordinal);

    public static IReadOnlyList<PersistedMetric> Extract(
        AgentSnapshot snapshot)
    {
        var metrics = new List<PersistedMetric>();
        foreach (var group in snapshot.Groups.Values)
        {
            if (group.Data is not JsonObject data ||
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
                    !TryReadFiniteDouble(metric["value"], out var value))
                {
                    continue;
                }

                metrics.Add(new PersistedMetric(entry.Key, unit, value));
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
