using System.Diagnostics.CodeAnalysis;

namespace PerfMonitor.Contracts;

public static class ActionTypes
{
    public const string SetProcessPriority = "set_process_priority";
    public const string TerminateProcess = "terminate_process";
    public const string StartApprovedDiagnostic =
        "start_approved_diagnostic";
    public const string ApplyApprovedPowerProfile =
        "apply_approved_power_profile";

    public static IReadOnlyList<string> All { get; } =
    [
        SetProcessPriority,
        TerminateProcess,
        StartApprovedDiagnostic,
        ApplyApprovedPowerProfile,
    ];
}

public static class ActionPriorities
{
    public const string Idle = "idle";
    public const string BelowNormal = "below_normal";
    public const string Normal = "normal";
    public const string AboveNormal = "above_normal";

    public static IReadOnlyList<string> All { get; } =
    [
        Idle,
        BelowNormal,
        Normal,
        AboveNormal,
    ];
}

public static class ApprovedDiagnosticIds
{
    public const string BrokerSelfCheck = "broker.self_check";

    public static IReadOnlyList<string> All { get; } =
    [
        BrokerSelfCheck,
    ];
}

public static class ApprovedPowerProfileIds
{
    public const string Balanced = "balanced";
    public const string PowerSaver = "power_saver";
    public const string HighPerformance = "high_performance";

    public static IReadOnlyList<string> All { get; } =
    [
        Balanced,
        PowerSaver,
        HighPerformance,
    ];
}

public static class ActionStatuses
{
    public const string Succeeded = "succeeded";
    public const string DryRun = "dry_run";
    public const string Denied = "denied";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Indeterminate = "indeterminate";
    public const string IdempotencyConflict =
        "idempotency_conflict";

    public static IReadOnlyList<string> Terminal { get; } =
    [
        Succeeded,
        DryRun,
        Denied,
        Rejected,
        Failed,
        Indeterminate,
        IdempotencyConflict,
    ];
}

public static class ActionErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string MessageTooLarge = "message_too_large";
    public const string ProtocolVersionUnsupported =
        "protocol_version_unsupported";
    public const string RequestTimedOut = "request_timed_out";
    public const string ActionNotSupported = "action_not_supported";
    public const string ActionPolicyDenied = "action_policy_denied";
    public const string CallerIdentityDenied =
        "caller_identity_denied";
    public const string ClientImageDenied = "client_image_denied";
    public const string TargetNotFound = "target_not_found";
    public const string TargetIdentityChanged =
        "target_identity_changed";
    public const string TargetOwnerMismatch =
        "target_owner_mismatch";
    public const string TargetProtected = "target_protected";
    public const string IdempotencyConflict =
        "idempotency_conflict";
    public const string IdempotencyIndeterminate =
        "idempotency_indeterminate";
    public const string AuditUnavailable = "audit_unavailable";
    public const string ExecutorFailed = "executor_failed";
    public const string ServiceUnavailable = "service_unavailable";

    public static IReadOnlyList<string> All { get; } =
    [
        InvalidRequest,
        MessageTooLarge,
        ProtocolVersionUnsupported,
        RequestTimedOut,
        ActionNotSupported,
        ActionPolicyDenied,
        CallerIdentityDenied,
        ClientImageDenied,
        TargetNotFound,
        TargetIdentityChanged,
        TargetOwnerMismatch,
        TargetProtected,
        IdempotencyConflict,
        IdempotencyIndeterminate,
        AuditUnavailable,
        ExecutorFailed,
        ServiceUnavailable,
    ];
}

public static class ActionContractLimits
{
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxApprovedIdLength = 64;
    public const int MaxPolicyVersionLength = 64;
    public static readonly TimeSpan MaxDeadline =
        TimeSpan.FromSeconds(15);
}

public sealed record ActionRequestContract
{
    public required string ActionType { get; init; }
    public int? Pid { get; init; }
    public long? CreationTimeTicks { get; init; }
    public string? Priority { get; init; }
    public string? DiagnosticId { get; init; }
    public string? PowerProfileId { get; init; }
}

public sealed record ActionExecutionRequestContract
{
    public required string IdempotencyKey { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public bool DryRun { get; init; }
    public required string AgentPolicyVersion { get; init; }
    public required ActionRequestContract Action { get; init; }
}

public sealed record UserActionRequestContract
{
    public required string IdempotencyKey { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public bool DryRun { get; init; }
    public required ActionRequestContract Action { get; init; }
}

public sealed record ActionStateContract
{
    public int? Pid { get; init; }
    public long? CreationTimeTicks { get; init; }
    public bool? IsRunning { get; init; }
    public string? Priority { get; init; }
    public string? DiagnosticId { get; init; }
    public string? DiagnosticRunId { get; init; }
    public string? PowerProfileId { get; init; }
}

public sealed record ActionResultContract
{
    public required string ActionId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public ActionStateContract? Before { get; init; }
    public ActionStateContract? After { get; init; }
}

public sealed record ActionCapabilityItemContract
{
    public required string ActionType { get; init; }
    public bool DryRunSupported { get; init; }
}

public sealed record ActionsCapabilityContract
{
    public required string BrokerProtocolVersion { get; init; }
    public bool BrokerConfigured { get; init; }
    public bool BrokerAvailable { get; init; }
    public bool DryRunOnly { get; init; }
    public required IReadOnlyList<ActionCapabilityItemContract> Actions
    {
        get;
        init;
    }
}

public static class ActionContractValidation
{
    public static void Validate(
        ActionExecutionRequestContract request,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Action);
        if (!IsToken(
            request.IdempotencyKey,
            ActionContractLimits.MaxIdempotencyKeyLength))
        {
            throw new ArgumentException(
                ActionErrorCodes.InvalidRequest,
                nameof(request));
        }

        if (!IsToken(
            request.AgentPolicyVersion,
            ActionContractLimits.MaxPolicyVersionLength))
        {
            throw new ArgumentException(
                ActionErrorCodes.InvalidRequest,
                nameof(request));
        }

        if (request.DeadlineUtc.Offset != TimeSpan.Zero ||
            request.DeadlineUtc <= receivedAtUtc ||
            request.DeadlineUtc - receivedAtUtc >
                ActionContractLimits.MaxDeadline)
        {
            throw new ArgumentException(
                ActionErrorCodes.InvalidRequest,
                nameof(request));
        }

        ValidateAction(request.Action);
    }

    public static void ValidateAction(ActionRequestContract action)
    {
        ArgumentNullException.ThrowIfNull(action);
        switch (action.ActionType)
        {
            case ActionTypes.SetProcessPriority:
                ValidateProcessTarget(action);
                if (!ActionPriorities.All.Contains(
                    action.Priority,
                    StringComparer.Ordinal) ||
                    action.DiagnosticId is not null ||
                    action.PowerProfileId is not null)
                {
                    InvalidAction();
                }
                break;

            case ActionTypes.TerminateProcess:
                ValidateProcessTarget(action);
                if (action.Priority is not null ||
                    action.DiagnosticId is not null ||
                    action.PowerProfileId is not null)
                {
                    InvalidAction();
                }
                break;

            case ActionTypes.StartApprovedDiagnostic:
                ValidateApprovedOnly(
                    action,
                    action.DiagnosticId);
                break;

            case ActionTypes.ApplyApprovedPowerProfile:
                ValidateApprovedOnly(
                    action,
                    action.PowerProfileId);
                break;

            default:
                throw new ArgumentException(
                    ActionErrorCodes.ActionNotSupported,
                    nameof(action));
        }
    }

    public static bool IsApprovedId(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > ActionContractLimits.MaxApprovedIdLength ||
            value[0] is not (>= 'a' and <= 'z' or
                >= '0' and <= '9'))
        {
            return false;
        }

        return value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '.' or '_' or '-');
    }

    public static bool IsToken(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed =
                character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '.' or '_' or '-' or ':';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateProcessTarget(
        ActionRequestContract action)
    {
        if (action.Pid is null or <= 0 ||
            action.CreationTimeTicks is null or <= 0)
        {
            InvalidAction();
        }
    }

    private static void ValidateApprovedOnly(
        ActionRequestContract action,
        string? approvedId)
    {
        if (!IsApprovedId(approvedId) ||
            action.Pid is not null ||
            action.CreationTimeTicks is not null ||
            action.Priority is not null ||
            (
                action.ActionType ==
                    ActionTypes.StartApprovedDiagnostic &&
                action.PowerProfileId is not null
            ) ||
            (
                action.ActionType ==
                    ActionTypes.ApplyApprovedPowerProfile &&
                action.DiagnosticId is not null
            ))
        {
            InvalidAction();
        }
    }

    [DoesNotReturn]
    private static void InvalidAction() =>
        throw new ArgumentException(
            ActionErrorCodes.InvalidRequest,
            "action");
}
