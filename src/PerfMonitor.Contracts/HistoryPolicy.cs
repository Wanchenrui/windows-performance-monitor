namespace PerfMonitor.Contracts;

public static class HistoryPolicy
{
    public const int DefaultMaxPoints = 2_000;
    public const int MaxPoints = 5_000;
    public const int MaxMetricIds = 16;
    public const int RamPointLimit = 86_400;
    public static readonly TimeSpan MaxQueryRange = TimeSpan.FromDays(366);

    public static IReadOnlyList<string> MetricIds { get; } =
    [
        MetricIds.SystemCpuUtilization,
        MetricIds.MemoryUtilization,
        MetricIds.MemoryUsedBytes,
        MetricIds.MemoryAvailableBytes,
        MetricIds.MemoryTotalBytes,
        MetricIds.UptimeSeconds,
        MetricIds.AgentCpuCoreEquivalent,
        MetricIds.AgentWorkingSetBytes,
        MetricIds.AgentPrivateBytes,
        MetricIds.AgentGcHeapBytes,
    ];

    public static IReadOnlyList<string> Aggregations { get; } =
        ["min", "max", "avg", "last"];
}

public static class IpcErrorCodes
{
    public const string ContractVersionUnsupported =
        "contract_version_unsupported";
    public const string InvalidRequest = "invalid_request";
    public const string MessageTooLarge = "message_too_large";
    public const string RequestTimedOut = "request_timed_out";
    public const string ServiceUnavailable = "service_unavailable";
}
