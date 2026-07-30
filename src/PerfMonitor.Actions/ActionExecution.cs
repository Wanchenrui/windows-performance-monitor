using System.Security.Cryptography;
using System.Text.Json;
using PerfMonitor.Contracts;

namespace PerfMonitor.Actions;

public enum ActionAuditReservationKind
{
    New,
    Replay,
    Conflict,
    Indeterminate,
}

public sealed record ActionAuditRequest(
    string CallerSid,
    int ClientPid,
    string ClientImageSha256,
    string RequestSha256,
    string BrokerPolicyVersion,
    DateTimeOffset ReceivedAtUtc,
    ActionExecutionRequestContract Request);

public sealed record ActionAuditReservation(
    ActionAuditReservationKind Kind,
    string ActionId,
    ActionResultContract? StoredResult);

public interface IActionAuditStore : IAsyncDisposable
{
    ValueTask RecoverPendingAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken);

    ValueTask<ActionAuditReservation> ReserveAsync(
        ActionAuditRequest request,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        string callerSid,
        string idempotencyKey,
        ActionResultContract result,
        CancellationToken cancellationToken);

    ValueTask MarkIndeterminateAsync(
        string callerSid,
        string idempotencyKey,
        DateTimeOffset completedAtUtc,
        string errorCode,
        CancellationToken cancellationToken);
}

public interface IPrivilegedActionExecutor
{
    ValueTask<ActionStateContract> CaptureBeforeAsync(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy,
        CancellationToken cancellationToken);

    ValueTask<ActionStateContract> ExecuteAsync(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy,
        ActionStateContract before,
        CancellationToken cancellationToken);
}

public sealed class ActionExecutorException : Exception
{
    public ActionExecutorException(
        string errorCode,
        bool mayHaveMutated = false)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        MayHaveMutated = mayHaveMutated;
    }

    public string ErrorCode { get; }

    public bool MayHaveMutated { get; }
}

public sealed class BrokerActionCoordinator
{
    private readonly BrokerMachinePolicy _policy;
    private readonly IActionAuditStore _audit;
    private readonly IPrivilegedActionExecutor _executor;
    private readonly bool _forceDryRunOnly;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public BrokerActionCoordinator(
        BrokerMachinePolicy policy,
        IActionAuditStore audit,
        IPrivilegedActionExecutor executor,
        bool forceDryRunOnly,
        TimeProvider? timeProvider = null)
    {
        _policy = policy?.Validate() ??
            throw new ArgumentNullException(nameof(policy));
        _audit = audit ??
            throw new ArgumentNullException(nameof(audit));
        _executor = executor ??
            throw new ArgumentNullException(nameof(executor));
        _forceDryRunOnly = forceDryRunOnly;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BrokerMachinePolicy Policy => _policy;

    public async ValueTask<ActionResultContract> ExecuteAsync(
        ActionExecutionRequestContract request,
        BrokerCallerIdentity caller,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            ActionContractValidation.Validate(
                request,
                receivedAtUtc);
        }
        catch (ArgumentException exception)
        {
            return Result(
                actionId: Guid.NewGuid().ToString("N"),
                request.IdempotencyKey ?? string.Empty,
                ActionStatuses.Rejected,
                StableValidationError(exception),
                receivedAtUtc,
                startedAtUtc: null,
                completedAtUtc: receivedAtUtc,
                before: null,
                after: null);
        }

        var requestHash = ActionRequestHash.Compute(request);
        ActionAuditReservation reservation;
        try
        {
            reservation = await _audit.ReserveAsync(
                new ActionAuditRequest(
                    caller.Sid,
                    caller.ClientPid,
                    caller.ClientImageSha256,
                    requestHash,
                    _policy.PolicyVersion,
                    receivedAtUtc,
                    request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            return Result(
                Guid.NewGuid().ToString("N"),
                request.IdempotencyKey,
                ActionStatuses.Failed,
                ActionErrorCodes.AuditUnavailable,
                receivedAtUtc,
                null,
                receivedAtUtc,
                null,
                null);
        }

        switch (reservation.Kind)
        {
            case ActionAuditReservationKind.Replay:
                return reservation.StoredResult ??
                    throw new InvalidDataException(
                        "Replay reservation has no stored result.");

            case ActionAuditReservationKind.Conflict:
                return Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    ActionStatuses.IdempotencyConflict,
                    ActionErrorCodes.IdempotencyConflict,
                    receivedAtUtc,
                    null,
                    receivedAtUtc,
                    null,
                    null);

            case ActionAuditReservationKind.Indeterminate:
                return reservation.StoredResult ?? Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    ActionStatuses.Indeterminate,
                    ActionErrorCodes.IdempotencyIndeterminate,
                    receivedAtUtc,
                    null,
                    receivedAtUtc,
                    null,
                    null);

            case ActionAuditReservationKind.New:
                break;

            default:
                throw new InvalidDataException(
                    "Unknown audit reservation kind.");
        }

        var decision = ActionPolicyEvaluator.Evaluate(
            _policy,
            caller,
            request.Action,
            _forceDryRunOnly);
        if (!decision.Allowed)
        {
            return await CompleteAsync(
                caller,
                request,
                Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    ActionStatuses.Denied,
                    decision.ErrorCode ??
                        ActionErrorCodes.ActionPolicyDenied,
                    receivedAtUtc,
                    null,
                    receivedAtUtc,
                    null,
                    null),
                mutationMayHaveStarted: false,
                cancellationToken).ConfigureAwait(false);
        }

        using var deadlineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var remaining = request.DeadlineUtc - receivedAtUtc;
        if (remaining > ActionContractLimits.MaxDeadline)
        {
            remaining = ActionContractLimits.MaxDeadline;
        }
        deadlineCancellation.CancelAfter(remaining);

        var gateHeld = false;
        var mutationMayHaveStarted = false;
        ActionStateContract? before = null;
        var startedAtUtc = _timeProvider.GetUtcNow();
        try
        {
            await _executionGate.WaitAsync(
                deadlineCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            before = await _executor.CaptureBeforeAsync(
                request.Action,
                caller,
                _policy,
                deadlineCancellation.Token).ConfigureAwait(false);

            if (request.DryRun || decision.DryRunOnly)
            {
                return await CompleteAsync(
                    caller,
                    request,
                    Result(
                        reservation.ActionId,
                        request.IdempotencyKey,
                        ActionStatuses.DryRun,
                        errorCode: null,
                        receivedAtUtc,
                        startedAtUtc,
                        _timeProvider.GetUtcNow(),
                        before,
                        before),
                    mutationMayHaveStarted: false,
                    cancellationToken).ConfigureAwait(false);
            }

            mutationMayHaveStarted = true;
            var after = await _executor.ExecuteAsync(
                request.Action,
                caller,
                _policy,
                before,
                deadlineCancellation.Token).ConfigureAwait(false);
            return await CompleteAsync(
                caller,
                request,
                Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    ActionStatuses.Succeeded,
                    errorCode: null,
                    receivedAtUtc,
                    startedAtUtc,
                    _timeProvider.GetUtcNow(),
                    before,
                    after),
                mutationMayHaveStarted,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ActionExecutorException exception)
        {
            mutationMayHaveStarted |= exception.MayHaveMutated;
            var status = mutationMayHaveStarted
                ? ActionStatuses.Indeterminate
                : IsTargetRejection(exception.ErrorCode)
                    ? ActionStatuses.Rejected
                    : ActionStatuses.Failed;
            var errorCode = mutationMayHaveStarted
                ? ActionErrorCodes.IdempotencyIndeterminate
                : exception.ErrorCode;
            return await CompleteAsync(
                caller,
                request,
                Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    status,
                    errorCode,
                    receivedAtUtc,
                    startedAtUtc,
                    _timeProvider.GetUtcNow(),
                    before,
                    null),
                mutationMayHaveStarted,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var status = mutationMayHaveStarted
                ? ActionStatuses.Indeterminate
                : ActionStatuses.Failed;
            var errorCode = mutationMayHaveStarted
                ? ActionErrorCodes.IdempotencyIndeterminate
                : ActionErrorCodes.RequestTimedOut;
            return await CompleteAsync(
                caller,
                request,
                Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    status,
                    errorCode,
                    receivedAtUtc,
                    startedAtUtc,
                    _timeProvider.GetUtcNow(),
                    before,
                    null),
                mutationMayHaveStarted,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            var status = mutationMayHaveStarted
                ? ActionStatuses.Indeterminate
                : ActionStatuses.Failed;
            var errorCode = mutationMayHaveStarted
                ? ActionErrorCodes.IdempotencyIndeterminate
                : ActionErrorCodes.ExecutorFailed;
            return await CompleteAsync(
                caller,
                request,
                Result(
                    reservation.ActionId,
                    request.IdempotencyKey,
                    status,
                    errorCode,
                    receivedAtUtc,
                    startedAtUtc,
                    _timeProvider.GetUtcNow(),
                    before,
                    null),
                mutationMayHaveStarted,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (gateHeld)
            {
                _executionGate.Release();
            }
        }
    }

    private async ValueTask<ActionResultContract> CompleteAsync(
        BrokerCallerIdentity caller,
        ActionExecutionRequestContract request,
        ActionResultContract result,
        bool mutationMayHaveStarted,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.CompleteAsync(
                caller.Sid,
                request.IdempotencyKey,
                result,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            if (mutationMayHaveStarted)
            {
                try
                {
                    await _audit.MarkIndeterminateAsync(
                        caller.Sid,
                        request.IdempotencyKey,
                        _timeProvider.GetUtcNow(),
                        ActionErrorCodes.IdempotencyIndeterminate,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception markException) when (
                    markException is not OutOfMemoryException and
                    not StackOverflowException)
                {
                    // The durable pending row is recovered as
                    // indeterminate at the next Broker start.
                }
            }

            return result with
            {
                Status = mutationMayHaveStarted
                    ? ActionStatuses.Indeterminate
                    : ActionStatuses.Failed,
                ErrorCode = mutationMayHaveStarted
                    ? ActionErrorCodes.IdempotencyIndeterminate
                    : ActionErrorCodes.AuditUnavailable,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
                After = mutationMayHaveStarted ? null : result.After,
            };
        }
    }

    private static bool IsTargetRejection(string errorCode) =>
        errorCode is
            ActionErrorCodes.TargetNotFound or
            ActionErrorCodes.TargetIdentityChanged or
            ActionErrorCodes.TargetOwnerMismatch or
            ActionErrorCodes.TargetProtected or
            ActionErrorCodes.ActionNotSupported;

    private static string StableValidationError(
        ArgumentException exception) =>
        exception.Message.StartsWith(
            ActionErrorCodes.ActionNotSupported,
            StringComparison.Ordinal)
            ? ActionErrorCodes.ActionNotSupported
            : ActionErrorCodes.InvalidRequest;

    private static ActionResultContract Result(
        string actionId,
        string idempotencyKey,
        string status,
        string? errorCode,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        ActionStateContract? before,
        ActionStateContract? after) =>
        new()
        {
            ActionId = actionId,
            IdempotencyKey = idempotencyKey,
            Status = status,
            ErrorCode = errorCode,
            ReceivedAtUtc = receivedAtUtc,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            Before = before,
            After = after,
        };
}

public static class ActionRequestHash
{
    public static string Compute(
        ActionExecutionRequestContract request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream(capacity: 512);
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("dryRun", request.DryRun);
            writer.WriteString(
                "agentPolicyVersion",
                request.AgentPolicyVersion);
            writer.WritePropertyName("action");
            writer.WriteStartObject();
            writer.WriteString(
                "actionType",
                request.Action.ActionType);
            WriteOptional(
                writer,
                "pid",
                request.Action.Pid);
            WriteOptional(
                writer,
                "creationTimeTicks",
                request.Action.CreationTimeTicks);
            WriteOptional(
                writer,
                "priority",
                request.Action.Priority);
            WriteOptional(
                writer,
                "diagnosticId",
                request.Action.DiagnosticId);
            WriteOptional(
                writer,
                "powerProfileId",
                request.Action.PowerProfileId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(
            SHA256.HashData(stream.GetBuffer().AsSpan(
                0,
                checked((int)stream.Length))));
    }

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string name,
        int? value)
    {
        if (value is not null)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string name,
        long? value)
    {
        if (value is not null)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteOptional(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
