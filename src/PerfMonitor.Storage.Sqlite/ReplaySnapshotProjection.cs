using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.Storage.Sqlite;

internal static class ReplaySnapshotProjection
{
    private static readonly HashSet<string> Scalars = new(HistoryPolicy.SupportedMetricIds, StringComparer.Ordinal);

    public static AgentSnapshot Create(AgentSnapshot snapshot, DiagnosticPolicy policy)
    {
        if (snapshot.Groups.Count > 128)
            throw new InvalidDataException("sqlite_projection_group_limit");
        var groups = new Dictionary<string, SnapshotGroup>(StringComparer.Ordinal);
        foreach (var (id, group) in snapshot.Groups)
        {
            if (id.Length > 64 || group.ProviderId.Length > 256 || group.Errors.Count > 128 ||
                group.Errors.Any(error => error.ErrorCode.Length > 64 || error.MetricId?.Length > 256) ||
                group.Coverage.SkippedByReason?.Count > 128 ||
                group.Coverage.SkippedByReason?.Keys.Any(key => key.Length > 64) == true)
                throw new InvalidDataException("sqlite_projection_metadata_limit");
            JsonNode? data = null;
            if (id == GroupIds.Processes)
            {
                // Empty watchlists never inspect or persist process details.
                data = policy.ProcessCpuSpike.Enabled && policy.WatchedProcessNames.Count > 0
                    ? DiagnosticProcessProjection.ProjectGroup(group, policy.WatchedProcessNames).Data
                    : null;
            }
            else if (id == GroupIds.Volumes && group.Data is JsonArray volumes)
            {
                var system = volumes.OfType<JsonObject>().FirstOrDefault(volume =>
                    volume["isSystem"] is JsonValue value && value.TryGetValue<bool>(out var isSystem) && isSystem);
                if (system is not null)
                    data = new JsonArray(new JsonObject
                    {
                        ["isSystem"] = true,
                        ["metrics"] = ProjectMetrics(system["metrics"] as JsonObject,
                            [MetricIds.VolumeUtilization]),
                    });
            }
            else if (group.Data is JsonObject original)
            {
                var metrics = ProjectMetrics(original["metrics"] as JsonObject, Scalars);
                if (metrics.Count > 0 || original.ContainsKey("sampleReady"))
                {
                    var scalarData = new JsonObject { ["metrics"] = metrics };
                    if (original["sampleReady"] is JsonValue ready && ready.TryGetValue<bool>(out var isReady))
                        scalarData["sampleReady"] = isReady;
                    data = scalarData;
                }
            }
            // All group envelopes remain available to the availability rule.
            groups[id] = group with { Data = data };
        }

        var metadata = new JsonObject
        {
            ["storageReplay"] = new JsonObject
            {
                ["projectionVersion"] = 1,
                ["policyFingerprint"] = DiagnosticReplay.GetPolicyFingerprint(policy),
            },
        };
        groups[GroupIds.Sampler] = groups.TryGetValue(GroupIds.Sampler, out var sampler)
            ? sampler with { Data = metadata }
            : new(ProviderIds.Sampler, snapshot.CompletedAtUtc, AvailabilityStates.Available,
                FreshnessStates.Fresh, ProviderCoverage.Complete, [], metadata)
            {
                ObservedElapsedSeconds = snapshot.ElapsedSeconds,
                ObservationSequence = snapshot.Sequence,
            };
        return snapshot with { Groups = groups };
    }

    private static JsonObject ProjectMetrics(JsonObject? original, IEnumerable<string> allowlist)
    {
        var result = new JsonObject();
        if (original is null) return result;
        foreach (var id in allowlist)
        {
            if (original[id] is not JsonObject metric) continue;
            var projected = new JsonObject();
            projected["value"] = ReadNumber(metric["value"]);
            foreach (var field in new[] { "unit", "sourceId" })
            {
                if (metric[field] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    if (text.Length > 256) throw new InvalidDataException("sqlite_projection_metadata_limit");
                    projected[field] = text;
                }
                else projected[field] = null;
            }
            result[id] = projected;
        }
        return result;
    }

    private static JsonNode? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return JsonValue.Create(number);
        if (value.TryGetValue<long>(out var integer)) return JsonValue.Create(integer);
        if (value.TryGetValue<int>(out var small)) return JsonValue.Create(small);
        return null;
    }
}
