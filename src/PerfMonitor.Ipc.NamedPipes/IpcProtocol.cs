using System.Text.Json;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;

namespace PerfMonitor.Ipc.NamedPipes;

public static class IpcProtocol
{
    public const int AbsoluteMaxMessageSize = 4 * 1024 * 1024;
    public const int MinimumNegotiatedMessageSize = 16 * 1024;
    public const int MaxJsonDepth = 32;
    public const int MaxSupportedVersions = 8;
    public const int MaxRequestIdLength = 128;
    public const int MaxRequestsPerConnection = 4_096;
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ActionRequestTimeout =
        TimeSpan.FromSeconds(15);
}

public sealed record IpcRequestMessage
{
    public required string Type { get; init; }
    public required string RequestId { get; init; }
    public IReadOnlyList<string>? SupportedContractVersions { get; init; }
    public int? MaxMessageSize { get; init; }
    public IReadOnlyList<string>? MetricIds { get; init; }
    public long? FromEpochMs { get; init; }
    public long? ToEpochMs { get; init; }
    public int? MaxPoints { get; init; }
    public IReadOnlyList<string>? RuleIds { get; init; }
    public IReadOnlyList<string>? States { get; init; }
    public int? MaxEvents { get; init; }
    public string? SubscriptionId { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public bool? DryRun { get; init; }
    public ActionRequestContract? Action { get; init; }
}

public sealed record IpcErrorPayload
{
    public required string ErrorCode { get; init; }
    public string? Detail { get; init; }
}

public sealed record IpcResponseMessage
{
    public required string Type { get; init; }
    public required string RequestId { get; init; }
    public string? SelectedContractVersion { get; init; }
    public string? ProductVersion { get; init; }
    public string? InstanceId { get; init; }
    public int? MaxMessageSize { get; init; }
    public string? SubscriptionId { get; init; }
    public JsonElement? Capabilities { get; init; }
    public JsonElement? Payload { get; init; }
    public IpcErrorPayload? Error { get; init; }
}

internal static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = IpcProtocol.MaxJsonDepth,
    };
}

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
