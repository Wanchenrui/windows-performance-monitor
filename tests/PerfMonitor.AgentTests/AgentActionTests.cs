using PerfMonitor.Actions;
using PerfMonitor.Agent;
using PerfMonitor.Broker.Client;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class AgentActionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DefaultPolicyDeniesWithoutContactingBroker()
    {
        var client = new FakeBrokerClient(
            BrokerCapabilities(
                ActionTypes.StartApprovedDiagnostic));
        var gateway = new AgentActionGateway(
            AgentActionPolicy.Default,
            client,
            new ManualTimeProvider(Now));

        var result = await gateway.ExecuteAsync(
            Request("default-denied"),
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.Denied, result.Status);
        Assert.AreEqual(
            ActionErrorCodes.ActionPolicyDenied,
            result.ErrorCode);
        Assert.AreEqual(0, client.ExecuteCount);
    }

    [TestMethod]
    public async Task AgentInjectsPolicyVersionAndForcesDryRun()
    {
        var policy = new AgentActionPolicy
        {
            PolicyVersion = "user-policy-v2",
            DryRunOnly = true,
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
            ],
            AllowedDiagnosticIds =
            [
                ApprovedDiagnosticIds.BrokerSelfCheck,
            ],
        }.Validate();
        var client = new FakeBrokerClient(
            BrokerCapabilities(
                ActionTypes.StartApprovedDiagnostic));
        var gateway = new AgentActionGateway(
            policy,
            client,
            new ManualTimeProvider(Now));
        await gateway.ProbeAsync(CancellationToken.None);

        var result = await gateway.ExecuteAsync(
            Request("forced-dry-run") with
            {
                DryRun = false,
            },
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.DryRun, result.Status);
        Assert.AreEqual(1, client.ExecuteCount);
        Assert.IsNotNull(client.LastRequest);
        Assert.IsTrue(client.LastRequest.DryRun);
        Assert.AreEqual(
            "user-policy-v2",
            client.LastRequest.AgentPolicyVersion);
    }

    [TestMethod]
    public async Task CapabilitiesArePolicyIntersection()
    {
        var policy = new AgentActionPolicy
        {
            PolicyVersion = "intersection-v1",
            DryRunOnly = false,
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
                ActionTypes.ApplyApprovedPowerProfile,
            ],
            AllowedDiagnosticIds =
            [
                ApprovedDiagnosticIds.BrokerSelfCheck,
            ],
            AllowedPowerProfileIds =
            [
                ApprovedPowerProfileIds.Balanced,
            ],
        }.Validate();
        var client = new FakeBrokerClient(
            BrokerCapabilities(
                ActionTypes.StartApprovedDiagnostic));
        var gateway = new AgentActionGateway(
            policy,
            client,
            new ManualTimeProvider(Now));
        await gateway.ProbeAsync(CancellationToken.None);

        var capabilities = gateway.ReadCapabilities();

        Assert.IsTrue(capabilities.BrokerConfigured);
        Assert.IsTrue(capabilities.BrokerAvailable);
        Assert.HasCount(1, capabilities.Actions);
        Assert.AreEqual(
            ActionTypes.StartApprovedDiagnostic,
            capabilities.Actions[0].ActionType);
    }

    [TestMethod]
    public async Task BrokerFailureReturnsStableLocalFailure()
    {
        var policy = new AgentActionPolicy
        {
            PolicyVersion = "enabled-v1",
            DryRunOnly = true,
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
            ],
            AllowedDiagnosticIds =
            [
                ApprovedDiagnosticIds.BrokerSelfCheck,
            ],
        }.Validate();
        var client = new FakeBrokerClient(
            BrokerCapabilities(
                ActionTypes.StartApprovedDiagnostic))
        {
            FailExecution = true,
        };
        var gateway = new AgentActionGateway(
            policy,
            client,
            new ManualTimeProvider(Now));

        var result = await gateway.ExecuteAsync(
            Request("broker-down"),
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.Failed, result.Status);
        Assert.AreEqual(
            ActionErrorCodes.ServiceUnavailable,
            result.ErrorCode);
    }

    private static UserActionRequestContract Request(string key) =>
        new()
        {
            IdempotencyKey = key,
            DeadlineUtc = Now.AddSeconds(10),
            DryRun = true,
            Action = new ActionRequestContract
            {
                ActionType =
                    ActionTypes.StartApprovedDiagnostic,
                DiagnosticId =
                    ApprovedDiagnosticIds.BrokerSelfCheck,
            },
        };

    private static ActionsCapabilityContract BrokerCapabilities(
        params string[] actionTypes) =>
        new()
        {
            BrokerProtocolVersion = BrokerProtocol.Version,
            BrokerConfigured = true,
            BrokerAvailable = true,
            DryRunOnly = true,
            Actions = actionTypes.Select(static action =>
                new ActionCapabilityItemContract
                {
                    ActionType = action,
                    DryRunSupported = true,
                }).ToArray(),
        };

    private sealed class FakeBrokerClient(
        ActionsCapabilityContract capabilities) :
        IBrokerActionClient
    {
        public bool FailExecution { get; init; }
        public int ExecuteCount { get; private set; }
        public ActionExecutionRequestContract? LastRequest
        {
            get;
            private set;
        }

        public bool LastKnownAvailable { get; private set; }

        public ValueTask<ActionsCapabilityContract>
            GetCapabilitiesAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastKnownAvailable = true;
            return ValueTask.FromResult(capabilities);
        }

        public ValueTask<ActionResultContract> ExecuteAsync(
            ActionExecutionRequestContract request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            LastRequest = request;
            if (FailExecution)
            {
                LastKnownAvailable = false;
                throw new BrokerUnavailableException(
                    ActionErrorCodes.ServiceUnavailable);
            }

            LastKnownAvailable = true;
            return ValueTask.FromResult(new ActionResultContract
            {
                ActionId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = request.IdempotencyKey,
                Status = request.DryRun
                    ? ActionStatuses.DryRun
                    : ActionStatuses.Succeeded,
                ReceivedAtUtc = Now,
                CompletedAtUtc = Now,
            });
        }
    }
}
