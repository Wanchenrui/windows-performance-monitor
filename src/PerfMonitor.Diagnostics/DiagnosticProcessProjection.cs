using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        if (group.ReadOnlyData is not { ValueKind: JsonValueKind.Array } processes)
        {
            return watchedNames.Select(static name =>
                new WatchedCpuAggregate(name, null, null, false)).ToArray();
        }

        foreach (var process in processes.EnumerateArray())
        {
            if (process.ValueKind != JsonValueKind.Object ||
                !TryString(Field(process, "name"), out var name))
            {
                complete = false;
                continue;
            }

            if (!aggregates.TryGetValue(name, out var aggregate))
            {
                continue;
            }

            aggregate.Count++;
            if (process.TryGetProperty("diagnosticIdentity", out var projectedIdentity))
            {
                // Only this bounded, versioned projection contains these
                // fields. Use its original instance-set identity directly.
                aggregate.ProjectedIdentity =
                    TryString(projectedIdentity, out var identity)
                        ? identity
                        : null;
                aggregate.Complete &=
                    Field(process, "diagnosticComplete").ValueKind == JsonValueKind.True;
            }
            else if (Field(process, "identity") is { ValueKind: JsonValueKind.Object } identity &&
                TryInteger(Field(identity, "pid"), out var pid) && pid > 0 &&
                TryInteger(Field(identity, "creationTimeTicks"), out var created) &&
                created > 0)
            {
                aggregate.Identities.Add(string.Create(
                    CultureInfo.InvariantCulture, $"{pid}:{created}"));
            }
            else
            {
                aggregate.IdentityKnown = false;
            }

            if (Field(process, "cpuReady").ValueKind == JsonValueKind.False ||
                Field(process, "metrics") is not { ValueKind: JsonValueKind.Object } metrics ||
                Field(metrics, MetricIds.ProcessCpuNormalized) is not { ValueKind: JsonValueKind.Object } metric ||
                !TryString(Field(metric, "unit"), out var unit) || unit != Units.Percent ||
                !TryNumber(Field(metric, "value"), out var cpu))
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

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value : default;

    private static bool TryString(JsonElement node, out string value)
    {
        value = string.Empty;
        return node.ValueKind == JsonValueKind.String &&
            (value = node.GetString()!) is not null &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInteger(JsonElement node, out long value)
    {
        value = 0;
        return node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out value);
    }

    private static bool TryNumber(JsonElement node, out double value)
    {
        value = 0;
        return node.ValueKind == JsonValueKind.Number &&
            node.TryGetDouble(out value) && double.IsFinite(value);
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
