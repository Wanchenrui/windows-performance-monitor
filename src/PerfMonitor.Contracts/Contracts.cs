using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfMonitor.Contracts;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}

public sealed record SummaryContract
{
    public required string Availability { get; init; }
    public required string Freshness { get; init; }
}

public sealed record CoverageContract
{
    public required string Status { get; init; }
    public int? Enumerated { get; init; }
    public int? Readable { get; init; }
    public int? Skipped { get; init; }
    public Dictionary<string, int>? SkippedByReason { get; init; }
}

public sealed record ContractError
{
    public required string ErrorCode { get; init; }
    public string? MetricId { get; init; }
}

public sealed record MetricGroupContract
{
    public required string ProviderId { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public required string Availability { get; init; }
    public required string Freshness { get; init; }
    public required CoverageContract Coverage { get; init; }
    public required IReadOnlyList<ContractError> Errors { get; init; }
    public required JsonElement Data { get; init; }
}

public sealed record RetentionContract
{
    public double HistoryWindowSeconds { get; init; }
    public int HistoryPointLimit { get; init; }
}

public sealed record SnapshotContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string InstanceId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? ScheduledAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public double? DataAgeSeconds { get; init; }
    public required SummaryContract Summary { get; init; }
    public required RetentionContract Retention { get; init; }
    public required IReadOnlyDictionary<string, MetricGroupContract> Groups { get; init; }
}

public sealed record HistoryQueryContract
{
    public required IReadOnlyList<string> MetricIds { get; init; }
    public long? FromEpochMs { get; init; }
    public long? ToEpochMs { get; init; }
    public int MaxPoints { get; init; }
}

public sealed record HistoryMetricStatsContract
{
    public required string Unit { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Avg { get; init; }
    public double? Last { get; init; }
}

public sealed record HistoryPointContract
{
    public long StartEpochMs { get; init; }
    public long EndEpochMs { get; init; }
    public long Sequence { get; init; }
    public required IReadOnlyDictionary<string, HistoryMetricStatsContract> Metrics { get; init; }
}

public sealed record HistoryContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string InstanceId { get; init; }
    public required HistoryQueryContract Query { get; init; }
    public int SourcePointCount { get; init; }
    public int PointCount { get; init; }
    public bool Downsampled { get; init; }
    public required IReadOnlyList<HistoryPointContract> Points { get; init; }
}

public sealed record ProviderCapabilityContract
{
    public required string GroupId { get; init; }
    public required string ProviderId { get; init; }
    public int DefaultPeriodMs { get; init; }
    public required string RequiredPrivilege { get; init; }
    public required string CostClass { get; init; }
}

public sealed record HistoryCapabilityContract
{
    public required IReadOnlyList<string> MetricIds { get; init; }
    public int DefaultMaxPoints { get; init; }
    public int MaxPoints { get; init; }
    public int RamPointLimit { get; init; }
    public required IReadOnlyList<string> Aggregations { get; init; }
}

public sealed record CapabilitiesContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string InstanceId { get; init; }
    public required IReadOnlyList<ProviderCapabilityContract> Groups { get; init; }
    public required HistoryCapabilityContract History { get; init; }
    public DiagnosticsCapabilityContract? Diagnostics { get; init; }
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }
    public required IReadOnlyList<string> StableErrorCodes { get; init; }
}

public sealed record HealthContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string Service { get; init; }
    public required string InstanceId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public required SummaryContract Summary { get; init; }
}

public sealed record ContractScenario
{
    public required string ContractVersion { get; init; }
    public required string ScenarioId { get; init; }
    public required IReadOnlyList<SnapshotContract> Snapshots { get; init; }
}
