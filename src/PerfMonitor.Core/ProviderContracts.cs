using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

public static class AvailabilityStates
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
    public const string NotSupported = "not_supported";
    public const string PermissionDenied = "permission_denied";
    public const string Timeout = "timeout";
    public const string Error = "error";
}

public static class FreshnessStates
{
    public const string WarmingUp = "warming_up";
    public const string Fresh = "fresh";
    public const string Stale = "stale";
}

public sealed record ProviderDescriptor(
    string GroupId,
    string ProviderId,
    TimeSpan DefaultPeriod,
    TimeSpan Timeout,
    string RequiredPrivilege,
    string CostClass);

public sealed record ProviderContext(
    TimeProvider TimeProvider,
    DateTimeOffset UtcNow,
    long Timestamp,
    int LogicalProcessorCount);

public sealed record ProviderCoverage(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Enumerated = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Readable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Skipped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, int>? SkippedByReason = null)
{
    public static ProviderCoverage Complete { get; } = new("complete");

    public static ProviderCoverage Limited { get; } = new("limited");
}

public sealed record ProviderError(string ErrorCode, string? MetricId);

public sealed record ProviderResult(
    string GroupId,
    string ProviderId,
    DateTimeOffset? ObservedAtUtc,
    string Availability,
    ProviderCoverage Coverage,
    IReadOnlyList<ProviderError> Errors,
    JsonNode? Data)
{
    public static ProviderResult Failure(
        ProviderDescriptor descriptor,
        DateTimeOffset observedAtUtc,
        string availability,
        string errorCode,
        string? metricId = null) =>
        new(
            descriptor.GroupId,
            descriptor.ProviderId,
            observedAtUtc,
            availability,
            ProviderCoverage.Limited,
            [new ProviderError(errorCode, metricId)],
            null);

    public static ProviderResult Timeout(
        ProviderDescriptor descriptor,
        DateTimeOffset observedAtUtc) =>
        Failure(
            descriptor,
            observedAtUtc,
            AvailabilityStates.Timeout,
            StableErrorCodes.Timeout);
}

public sealed record ProviderExecution(
    ProviderDescriptor Descriptor,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double DurationMilliseconds,
    double JitterMilliseconds,
    long MissedIntervalsTotal,
    long SkippedBusyIntervalsTotal);

public interface IMetricProvider
{
    ProviderDescriptor Descriptor { get; }

    ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken);
}

public interface IProviderResultSink
{
    ValueTask PublishAsync(
        ProviderResult result,
        ProviderExecution execution,
        CancellationToken cancellationToken);
}

public static class MetricJson
{
    public static JsonObject Value(
        double? value,
        string unit,
        string sourceId) =>
        new()
        {
            ["value"] = value is null ? null : JsonValue.Create(value.Value),
            ["unit"] = unit,
            ["sourceId"] = sourceId,
        };

    public static JsonObject Value(
        long? value,
        string unit,
        string sourceId) =>
        new()
        {
            ["value"] = value is null ? null : JsonValue.Create(value.Value),
            ["unit"] = unit,
            ["sourceId"] = sourceId,
        };

    public static JsonObject Value(
        int value,
        string unit,
        string sourceId) =>
        new()
        {
            ["value"] = value,
            ["unit"] = unit,
            ["sourceId"] = sourceId,
        };

    public static IReadOnlyDictionary<string, int> ReadOnlyReasons(
        IDictionary<string, int> reasons) =>
        new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(reasons, StringComparer.Ordinal));
}

public static class ExceptionClassifier
{
    public static string StableCode(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => StableErrorCodes.AccessDenied,
            TimeoutException => StableErrorCodes.Timeout,
            NotSupportedException =>
                StableErrorCodes.NotSupported,
            InvalidDataException or FormatException =>
                StableErrorCodes.InvalidData,
            OutOfMemoryException => StableErrorCodes.ResourceExhausted,
            _ => StableErrorCodes.ProviderFailure,
        };

    public static string Availability(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                AvailabilityStates.PermissionDenied,
            TimeoutException => AvailabilityStates.Timeout,
            NotSupportedException =>
                AvailabilityStates.NotSupported,
            _ => AvailabilityStates.Error,
        };
}
