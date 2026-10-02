using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

/// <summary>
/// Shares the watched-name CPU maximum and instance continuity semantics with
/// persisted replay. The projection stores at most one row per watched name;
/// no PID, creation time, or unrelated process data is persisted.
/// </summary>
public static class DiagnosticProcessProjection
{
    public static SnapshotGroup ProjectGroup(
        SnapshotGroup group,
        IReadOnlyList<string> watchedNames)
    {
        var data = new JsonArray();
        foreach (var aggregate in Read(group, watchedNames))
        {
            data.Add(new JsonObject
            {
                ["name"] = aggregate.Name,
                ["diagnosticIdentity"] = aggregate.ContinuityId,
                ["diagnosticComplete"] = aggregate.Complete,
                ["cpuReady"] = aggregate.CpuPercent is not null,
                ["metrics"] = new JsonObject
                {
                    [MetricIds.ProcessCpuNormalized] = MetricJson.Value(
                        aggregate.CpuPercent,
                        Units.Percent,
                        SourceIds.ProcessCpu),
                },
            });
        }

        return group with { Data = data };
    }

    internal static IReadOnlyList<WatchedCpuAggregate> Read(
        SnapshotGroup group,
        IReadOnlyList<string> watchedNames)
    {
        var aggregates = watchedNames.ToDictionary(
            static name => name,
            static name => new AggregateBuilder(name),
            StringComparer.OrdinalIgnoreCase);
        var complete = group.Coverage.Status == "complete";
        if (group.Data is not JsonArray processes)
        {
            return watchedNames.Select(static name =>
                new WatchedCpuAggregate(name, null, null, false)).ToArray();
        }

        foreach (var node in processes)
        {
            if (node is not JsonObject process ||
                !TryString(process["name"], out var name))
            {
                complete = false;
                continue;
            }

            if (!aggregates.TryGetValue(name, out var aggregate))
            {
                continue;
            }

            aggregate.Count++;
            if (process.ContainsKey("diagnosticIdentity"))
            {
                // Only this bounded, versioned projection contains these
                // fields. Use its original instance-set identity directly.
                aggregate.ProjectedIdentity =
                    TryString(process["diagnosticIdentity"], out var identity)
                        ? identity
                        : null;
                aggregate.Complete &=
                    process["diagnosticComplete"] is JsonValue projected &&
                    projected.TryGetValue<bool>(out var isComplete) &&
                    isComplete;
            }
            else if (process["identity"] is JsonObject identity &&
                TryInteger(identity["pid"], out var pid) && pid > 0 &&
                TryInteger(identity["creationTimeTicks"], out var created) &&
                created > 0)
            {
                aggregate.Identities.Add(string.Create(
                    CultureInfo.InvariantCulture, $"{pid}:{created}"));
            }
            else
            {
                aggregate.IdentityKnown = false;
            }

            if (process["cpuReady"] is JsonValue ready &&
                    ready.TryGetValue<bool>(out var cpuReady) && !cpuReady ||
                process["metrics"] is not JsonObject metrics ||
                metrics[MetricIds.ProcessCpuNormalized] is not JsonObject metric ||
                !TryString(metric["unit"], out var unit) || unit != Units.Percent ||
                !TryNumber(metric["value"], out var cpu))
            {
                aggregate.Complete = false;
                continue;
            }

            aggregate.Cpu = aggregate.Cpu is { } previous
                ? Math.Max(previous, cpu)
                : cpu;
        }

        return watchedNames.Select(name =>
        {
            var aggregate = aggregates[name];
            var identity = aggregate.Count == 0
                ? complete ? "absent" : null
                : aggregate.ProjectedIdentity ??
                    (aggregate.IdentityKnown && aggregate.Identities.Count > 0
                        ? Fingerprint(aggregate.Identities)
                        : null);
            return new WatchedCpuAggregate(
                name,
                aggregate.Cpu,
                identity,
                complete && aggregate.Complete);
        }).ToArray();
    }

    private static string Fingerprint(IEnumerable<string> identities) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', identities.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))))).ToLowerInvariant();

    private static bool TryString(JsonNode? node, out string value)
    {
        value = string.Empty;
        return node is JsonValue json &&
            json.TryGetValue<string>(out value!) &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue<long>(out value))
        {
            return true;
        }

        if (json.TryGetValue<int>(out var integer))
        {
            value = integer;
            return true;
        }

        return false;
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue<double>(out value))
        {
            return double.IsFinite(value);
        }

        if (TryInteger(node, out var integer))
        {
            value = integer;
            return true;
        }

        return false;
    }

    private sealed class AggregateBuilder(string name)
    {
        public string Name { get; } = name;
        public int Count { get; set; }
        public double? Cpu { get; set; }
        public bool Complete { get; set; } = true;
        public bool IdentityKnown { get; set; } = true;
        public string? ProjectedIdentity { get; set; }
        public List<string> Identities { get; } = [];
    }
}

internal sealed record WatchedCpuAggregate(
    string Name,
    double? CpuPercent,
    string? ContinuityId,
    bool Complete);
