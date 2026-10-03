using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker.Protocol;

public static class BrokerProtocol
{
    public const string Version = "1.0";
    public const int AbsoluteMaxMessageSize = 256 * 1024;
    public const int MinimumNegotiatedMessageSize = 8 * 1024;
    public const int MaxJsonDepth = 24;
    public const int MaxSupportedVersions = 4;
    public const int MaxRequestIdLength = 128;
    public const int MaxRequestsPerConnection = 1_024;
    public static readonly TimeSpan HelloTimeout =
        TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout =
        ActionContractLimits.MaxDeadline;
}

public static class BrokerMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "helloAck";
    public const string ExecuteAction = "executeAction";
    public const string ActionResult = "actionResult";
    public const string Error = "error";
}

public sealed record BrokerRequestMessage
{
    public required string Type { get; init; }
    public required string RequestId { get; init; }
    public IReadOnlyList<string>? SupportedBrokerProtocolVersions
    {
        get;
        init;
    }
    public int? MaxMessageSize { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public bool? DryRun { get; init; }
    public string? AgentPolicyVersion { get; init; }
    public ActionRequestContract? Action { get; init; }

    public ActionExecutionRequestContract ToExecutionRequest()
    {
        if (IdempotencyKey is null ||
            DeadlineUtc is null ||
            DryRun is null ||
            AgentPolicyVersion is null ||
            Action is null)
        {
            throw new BrokerProtocolException(
                ActionErrorCodes.InvalidRequest);
        }

        return new ActionExecutionRequestContract
        {
            IdempotencyKey = IdempotencyKey,
            DeadlineUtc = DeadlineUtc.Value,
            DryRun = DryRun.Value,
            AgentPolicyVersion = AgentPolicyVersion,
            Action = Action,
        };
    }
}

public sealed record BrokerErrorPayload
{
    public required string ErrorCode { get; init; }
}

public sealed record BrokerResponseMessage
{
    public required string Type { get; init; }
    public required string RequestId { get; init; }
    public string? SelectedBrokerProtocolVersion { get; init; }
    public string? BrokerInstanceId { get; init; }
    public int? MaxMessageSize { get; init; }
    public ActionsCapabilityContract? Capabilities { get; init; }
    public ActionResultContract? Result { get; init; }
    public BrokerErrorPayload? Error { get; init; }
}

public static class BrokerJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling =
            JsonUnmappedMemberHandling.Disallow,
        MaxDepth = BrokerProtocol.MaxJsonDepth,
    };
}

public sealed class BrokerProtocolException : Exception
{
    public BrokerProtocolException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public static class BrokerRequestParser
{
    private static readonly IReadOnlySet<string> HelloFields =
        new HashSet<string>(
            [
                "type",
                "requestId",
                "supportedBrokerProtocolVersions",
                "maxMessageSize",
            ],
            StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ExecuteFields =
        new HashSet<string>(
            [
                "type",
                "requestId",
                "idempotencyKey",
                "deadlineUtc",
                "dryRun",
                "agentPolicyVersion",
                "action",
            ],
            StringComparer.Ordinal);

    public static BrokerRequestMessage Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.RootElement.ValueKind !=
            JsonValueKind.Object)
        {
            Invalid();
        }

        var type = ReadRequiredString(
            document.RootElement,
            "type");
        var allowed = type switch
        {
            BrokerMessageTypes.Hello => HelloFields,
            BrokerMessageTypes.ExecuteAction => ExecuteFields,
            _ => throw new BrokerProtocolException(
                ActionErrorCodes.InvalidRequest),
        };
        RejectUnknownFields(document.RootElement, allowed);
        BrokerRequestMessage request;
        try
        {
            request = document.Deserialize<BrokerRequestMessage>(
                BrokerJson.Options) ?? throw new JsonException(
                "Broker request is null.");
        }
        catch (JsonException)
        {
            throw new BrokerProtocolException(
                ActionErrorCodes.InvalidRequest);
        }

        if (!ActionContractValidation.IsToken(
            request.RequestId,
            BrokerProtocol.MaxRequestIdLength))
        {
            Invalid();
        }

        if (type == BrokerMessageTypes.Hello)
        {
            ValidateHello(request);
            return request;
        }

        ValidateExecute(document.RootElement, request);
        return request;
    }

    private static void ValidateHello(
        BrokerRequestMessage request)
    {
        if (request.SupportedBrokerProtocolVersions is null ||
            request.SupportedBrokerProtocolVersions.Count is 0 or >
                BrokerProtocol.MaxSupportedVersions ||
            request.SupportedBrokerProtocolVersions.Any(version =>
                !ActionContractValidation.IsToken(
                    version,
                    ActionContractLimits.MaxApprovedIdLength)) ||
            request.MaxMessageSize is null or <
                BrokerProtocol.MinimumNegotiatedMessageSize or >
                BrokerProtocol.AbsoluteMaxMessageSize)
        {
            Invalid();
        }
    }

    private static void ValidateExecute(
        JsonElement root,
        BrokerRequestMessage request)
    {
        if (!root.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.Object)
        {
            Invalid();
        }

        var actionType = ReadRequiredString(
            action,
            "actionType");
        var allowedActionFields = actionType switch
        {
            ActionTypes.SetProcessPriority =>
                SetProcessPriorityFields,
            ActionTypes.TerminateProcess =>
                TerminateProcessFields,
            ActionTypes.StartApprovedDiagnostic =>
                StartDiagnosticFields,
            ActionTypes.ApplyApprovedPowerProfile =>
                ApplyPowerProfileFields,
            _ => throw new BrokerProtocolException(
                ActionErrorCodes.ActionNotSupported),
        };
        RejectUnknownFields(action, allowedActionFields);
        var execution = request.ToExecutionRequest();
        if (!ActionContractValidation.IsToken(
            execution.IdempotencyKey,
            ActionContractLimits.MaxIdempotencyKeyLength) ||
            !ActionContractValidation.IsToken(
                execution.AgentPolicyVersion,
                ActionContractLimits.MaxPolicyVersionLength) ||
            execution.DeadlineUtc.Offset != TimeSpan.Zero)
        {
            Invalid();
        }

        try
        {
            ActionContractValidation.ValidateAction(
                execution.Action);
        }
        catch (ArgumentException)
        {
            Invalid();
        }
    }

    private static readonly IReadOnlySet<string>
        SetProcessPriorityFields = new HashSet<string>(
            [
                "actionType",
                "pid",
                "creationTimeTicks",
                "priority",
            ],
            StringComparer.Ordinal);

    private static readonly IReadOnlySet<string>
        TerminateProcessFields = new HashSet<string>(
            [
                "actionType",
                "pid",
                "creationTimeTicks",
            ],
            StringComparer.Ordinal);

    private static readonly IReadOnlySet<string>
        StartDiagnosticFields = new HashSet<string>(
            [
                "actionType",
                "diagnosticId",
            ],
            StringComparer.Ordinal);

    private static readonly IReadOnlySet<string>
        ApplyPowerProfileFields = new HashSet<string>(
            [
                "actionType",
                "powerProfileId",
            ],
            StringComparer.Ordinal);

    private static string ReadRequiredString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            Invalid();
        }

        return value.GetString()!;
    }

    private static void RejectUnknownFields(
        JsonElement element,
        IReadOnlySet<string> allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                Invalid();
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() =>
        throw new BrokerProtocolException(
            ActionErrorCodes.InvalidRequest);
}

public static class BrokerFraming
{
    public static async ValueTask<JsonDocument?> ReadAsync(
        Stream stream,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        ValidateMaximum(maxMessageSize);
        var header = new byte[sizeof(uint)];
        var headerBytes = await ReadExactlyOrEofAsync(
            stream,
            header,
            cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new BrokerProtocolException(
                ActionErrorCodes.InvalidRequest);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maxMessageSize)
        {
            throw new BrokerProtocolException(
                ActionErrorCodes.MessageTooLarge);
        }

        var payload = ArrayPool<byte>.Shared.Rent(
            checked((int)length));
        try
        {
            var memory = payload.AsMemory(
                0,
                checked((int)length));
            var bytes = await ReadExactlyOrEofAsync(
                stream,
                memory,
                cancellationToken).ConfigureAwait(false);
            if (bytes != memory.Length)
            {
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            }

            try
            {
                return JsonDocument.Parse(
                    memory.ToArray(),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling =
                            JsonCommentHandling.Disallow,
                        MaxDepth = BrokerProtocol.MaxJsonDepth,
                    });
            }
            catch (JsonException)
            {
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            }
        }
        finally
        {
            payload.AsSpan(
                0,
                checked((int)length)).Clear();
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        ValidateMaximum(maxMessageSize);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            BrokerJson.Options);
        if (payload.Length == 0 ||
            payload.Length > maxMessageSize)
        {
            throw new BrokerProtocolException(
                ActionErrorCodes.MessageTooLarge);
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)payload.Length));
        await stream.WriteAsync(
            header,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateMaximum(int maxMessageSize)
    {
        if (maxMessageSize is <= 0 or >
            BrokerProtocol.AbsoluteMaxMessageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMessageSize));
        }
    }

    private static async ValueTask<int> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[total..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
        }

        return total;
    }
}
