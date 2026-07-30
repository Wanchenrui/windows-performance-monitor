using PerfMonitor.Actions;
using PerfMonitor.Broker.Client;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.Agent;

internal sealed class AgentActionGateway
{
    private readonly AgentActionPolicy _policy;
    private readonly IBrokerActionClient _client;
    private readonly TimeProvider _timeProvider;
    private ActionsCapabilityContract? _brokerCapabilities;

    public AgentActionGateway(
        AgentActionPolicy policy,
        IBrokerActionClient client,
        TimeProvider? timeProvider = null)
    {
        _policy = policy?.Validate() ??
            throw new ArgumentNullException(nameof(policy));
        _client = client ??
            throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ProbeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var capabilities =
                await _client.GetCapabilitiesAsync(
                    cancellationToken).ConfigureAwait(false);
            Volatile.Write(
                ref _brokerCapabilities,
                capabilities);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            Volatile.Write(ref _brokerCapabilities, null);
        }
    }

    public ActionsCapabilityContract ReadCapabilities()
    {
        var broker = Volatile.Read(ref _brokerCapabilities);
        var brokerActions = broker?.Actions
            .Select(static item => item.ActionType)
            .ToHashSet(StringComparer.Ordinal) ??
            [];
        var actions = _policy.EnabledActionTypes
            .Where(brokerActions.Contains)
            .Select(static action =>
                new ActionCapabilityItemContract
                {
                    ActionType = action,
                    DryRunSupported = true,
                })
            .ToArray();
        return new ActionsCapabilityContract
        {
            BrokerProtocolVersion = BrokerProtocol.Version,
            BrokerConfigured =
                _policy.EnabledActionTypes.Count > 0,
            BrokerAvailable =
                _client.LastKnownAvailable &&
                broker?.BrokerAvailable == true,
            DryRunOnly = _policy.DryRunOnly ||
                broker?.DryRunOnly != false,
            Actions = actions,
        };
    }

    public async ValueTask<ActionResultContract> ExecuteAsync(
        UserActionRequestContract request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receivedAtUtc = _timeProvider.GetUtcNow();
        var brokerRequest = new ActionExecutionRequestContract
        {
            IdempotencyKey = request.IdempotencyKey,
            DeadlineUtc = request.DeadlineUtc,
            DryRun = request.DryRun || _policy.DryRunOnly,
            AgentPolicyVersion = _policy.PolicyVersion,
            Action = request.Action,
        };
        try
        {
            ActionContractValidation.Validate(
                brokerRequest,
                receivedAtUtc);
        }
        catch (ArgumentException)
        {
            return LocalResult(
                request.IdempotencyKey,
                ActionStatuses.Rejected,
                ActionErrorCodes.InvalidRequest,
                receivedAtUtc);
        }

        var decision = ActionPolicyEvaluator.Evaluate(
            _policy,
            request.Action);
        if (!decision.Allowed)
        {
            return LocalResult(
                request.IdempotencyKey,
                ActionStatuses.Denied,
                decision.ErrorCode ??
                    ActionErrorCodes.ActionPolicyDenied,
                receivedAtUtc);
        }

        try
        {
            return await _client.ExecuteAsync(
                brokerRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException exception)
        {
            return LocalResult(
                request.IdempotencyKey,
                ActionStatuses.Failed,
                exception.ErrorCode,
                receivedAtUtc);
        }
    }

    private ActionResultContract LocalResult(
        string idempotencyKey,
        string status,
        string errorCode,
        DateTimeOffset receivedAtUtc) =>
        new()
        {
            ActionId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = idempotencyKey ?? string.Empty,
            Status = status,
            ErrorCode = errorCode,
            ReceivedAtUtc = receivedAtUtc,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
        };
}
