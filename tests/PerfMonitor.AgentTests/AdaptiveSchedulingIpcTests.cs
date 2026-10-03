using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class AdaptiveSchedulingIpcTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10000)]
    public async Task ClientGatesLegacyCapabilityAndSupportedRequestsRoundTrip(bool supported)
    {
        await using var fixture = new Fixture(supported); fixture.Server.Start();
        await using var client = await NamedPipeAgentClient.ConnectAsync(fixture.Endpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        var initial = await client.GetAdaptiveSchedulingAsync(CancellationToken.None);
        Assert.AreEqual(supported, initial.Supported); Assert.IsFalse(initial.Enabled);
        foreach (var preference in AdaptiveSchedulingPreferences.All)
        {
            var state = await client.SetAdaptiveSchedulingAsync(new() { InstanceId = client.InstanceId, Preference = preference, Enabled = true, DurationSeconds = 900 }, CancellationToken.None);
            Assert.AreEqual(client.InstanceId, state.InstanceId); Assert.AreEqual(supported, state.Enabled);
            if (supported) Assert.AreEqual(preference, state.Preference);
        }
        var off = await client.SetAdaptiveSchedulingAsync(new() { InstanceId = client.InstanceId, Preference = AdaptiveSchedulingPreferences.EnergySaving, Enabled = false, DurationSeconds = 900 }, CancellationToken.None);
        Assert.IsFalse(off.Enabled);
        Assert.AreEqual(supported ? 1 : 0, fixture.Service.ReadRequests);
        Assert.AreEqual(supported ? 4 : 0, fixture.Service.SetRequests);
        Assert.AreEqual(fixture.Service.InstanceId, (await client.GetSnapshotAsync(CancellationToken.None)).InstanceId);
        Assert.IsFalse(fixture.Server.Completion.IsCompleted);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("missing_instance")]
    [DataRow("wrong_instance")]
    [DataRow("missing_preference")]
    [DataRow("bad_preference")]
    [DataRow("missing_duration")]
    [DataRow("bad_duration")]
    [DataRow("fractional_duration")]
    [DataRow("text_duration")]
    [DataRow("missing_enabled")]
    [DataRow("null_enabled")]
    [Timeout(10000)]
    public async Task MalformedMutationReturnsStableErrorAndKeepsPipeAndServiceAlive(string invalid)
    {
        await using var fixture = new Fixture(); fixture.Server.Start();
        await using var pipe = await RawConnect(fixture.Endpoint);
        var command = new JsonObject { ["instanceId"] = fixture.Service.InstanceId, ["preference"] = AdaptiveSchedulingPreferences.Responsiveness, ["enabled"] = true, ["durationSeconds"] = 900 };
        switch (invalid)
        {
            case "missing_instance": command.Remove("instanceId"); break;
            case "wrong_instance": command["instanceId"] = Guid.NewGuid().ToString("N"); break;
            case "missing_preference": command.Remove("preference"); break;
            case "bad_preference": command["preference"] = "other"; break;
            case "missing_duration": command.Remove("durationSeconds"); break;
            case "bad_duration": command["durationSeconds"] = 901; break;
            case "fractional_duration": command["durationSeconds"] = 900.5; break;
            case "text_duration": command["durationSeconds"] = "900"; break;
            case "missing_enabled": command.Remove("enabled"); break;
            case "null_enabled": command["enabled"] = null; break;
        }
        var requestId = Guid.NewGuid().ToString("N");
        await LengthPrefixedJson.WriteAsync(pipe, new IpcRequestMessage
        {
            Type = "setAdaptiveScheduling", RequestId = requestId,
            AdaptiveScheduling = invalid == "null" ? null : JsonSerializer.SerializeToElement(command),
        }, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        using var error = await LengthPrefixedJson.ReadAsync(pipe, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        Assert.IsNotNull(error);
        Assert.AreEqual(requestId, error.RootElement.GetProperty("requestId").GetString());
        Assert.AreEqual("error", error.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(IpcErrorCodes.InvalidRequest, error.RootElement.GetProperty("error").GetProperty("errorCode").GetString());
        Assert.AreEqual(0, fixture.Service.SetRequests);
        await AssertSnapshotReply(pipe, fixture.Service.InstanceId);
        Assert.IsFalse(fixture.Server.Completion.IsCompleted);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task ControllerArgumentErrorIsStableAndWrongResponseInstanceIsNeverAccepted()
    {
        await using var fixture = new Fixture(); fixture.Server.Start();
        await using var client = await NamedPipeAgentClient.ConnectAsync(fixture.Endpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        fixture.Service.RejectMutation = true;
        var invalid = await Assert.ThrowsExactlyAsync<IpcRemoteException>(async () => _ = await client.SetAdaptiveSchedulingAsync(new()
        {
            InstanceId = client.InstanceId, Preference = AdaptiveSchedulingPreferences.Throughput, Enabled = true, DurationSeconds = 1800,
        }, CancellationToken.None));
        Assert.AreEqual(IpcErrorCodes.InvalidRequest, invalid.ErrorCode);
        Assert.AreEqual(client.InstanceId, (await client.GetSnapshotAsync(CancellationToken.None)).InstanceId);
        fixture.Service.WrongResponseInstance = true;
        var mismatch = await Assert.ThrowsExactlyAsync<IpcRemoteException>(async () => _ = await client.GetAdaptiveSchedulingAsync(CancellationToken.None));
        Assert.AreEqual(IpcErrorCodes.ServiceUnavailable, mismatch.ErrorCode);
        Assert.AreEqual(client.InstanceId, (await client.GetSnapshotAsync(CancellationToken.None)).InstanceId);
        fixture.Service.WrongResponseInstance = false; fixture.Service.MissingRequiredResponseField = true;
        var malformed = await Assert.ThrowsExactlyAsync<IpcProtocolException>(async () => _ = await client.GetAdaptiveSchedulingAsync(CancellationToken.None));
        Assert.AreEqual(IpcErrorCodes.InvalidRequest, malformed.ErrorCode);
        Assert.AreEqual(client.InstanceId, (await client.GetSnapshotAsync(CancellationToken.None)).InstanceId);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task CancelledRealConnectAndHandshakeDoNotLeaveDesktopOperationWaiting()
    {
        var endpoint = PipeEndpoint.ForCurrentUser() with { PipeName = $"PerfMonitor.adaptive-connect-test.{Guid.NewGuid():N}" };
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await NamedPipeAgentClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(3), cancellation.Token));

        // Accept a pipe without acknowledging hello: the caller's total deadline
        // must cancel negotiation as well as the initial OS connect.
        await using var silent = new NamedPipeServerStream(endpoint.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = silent.WaitForConnectionAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var connect = NamedPipeAgentClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(3), deadline.Token);
        await accepted;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await connect);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task RealMutationResponseWaitCanBeCancelledWithoutStoppingService()
    {
        await using var fixture = new Fixture(); fixture.Server.Start();
        await using var client = await NamedPipeAgentClient.ConnectAsync(fixture.Endpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Service.BeforeMutation = token => { entered.TrySetResult(); release.Wait(token); };
        using var cancellation = new CancellationTokenSource();
        var operation = client.SetAdaptiveSchedulingAsync(new()
        {
            InstanceId = client.InstanceId, Preference = AdaptiveSchedulingPreferences.EnergySaving, Enabled = true, DurationSeconds = 900,
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await operation);
        }
        finally { release.Set(); }
        await client.DisposeAsync();
        await using var probe = await NamedPipeAgentClient.ConnectAsync(fixture.Endpoint, TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.AreEqual(fixture.Service.InstanceId, (await probe.GetSnapshotAsync(CancellationToken.None)).InstanceId);
        Assert.IsFalse(fixture.Server.Completion.IsCompleted);
    }

    [TestMethod]
    public void CapabilityFactoryAdvertisesAdaptiveSchedulingOnlyWithController()
    {
        var method = typeof(AgentQueryService).GetMethod("BuildCapabilities", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var supported in new[] { false, true })
        {
            var capabilities = (CapabilitiesContract)method.Invoke(null,
                [Guid.NewGuid().ToString("N"), new DiagnosticsCapabilityContract { Rules = [] }, Array.Empty<ProviderDescriptor>(), PipeEndpoint.ForCurrentUser(), TimeSpan.FromSeconds(1), false, supported])!;
            Assert.AreEqual(supported, capabilities.Endpoints.ContainsKey("adaptiveScheduling"));
        }
    }

    private static async Task<NamedPipeClientStream> RawConnect(PipeEndpoint endpoint)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(3000, CancellationToken.None);
        await LengthPrefixedJson.WriteAsync(pipe, new IpcRequestMessage
        {
            Type = "hello", RequestId = Guid.NewGuid().ToString("N"), SupportedContractVersions = [ContractVersions.V1], MaxMessageSize = IpcProtocol.AbsoluteMaxMessageSize,
        }, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        using var hello = await LengthPrefixedJson.ReadAsync(pipe, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        Assert.IsNotNull(hello); Assert.AreEqual("helloAck", hello.RootElement.GetProperty("type").GetString());
        return pipe;
    }
    private static async Task AssertSnapshotReply(Stream pipe, string instance)
    {
        await LengthPrefixedJson.WriteAsync(pipe, new IpcRequestMessage { Type = "getSnapshot", RequestId = Guid.NewGuid().ToString("N") }, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        using var snapshot = await LengthPrefixedJson.ReadAsync(pipe, IpcProtocol.AbsoluteMaxMessageSize, CancellationToken.None);
        Assert.IsNotNull(snapshot); Assert.AreEqual("snapshot", snapshot.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(instance, snapshot.RootElement.GetProperty("payload").GetProperty("instanceId").GetString());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public PipeEndpoint Endpoint { get; } = PipeEndpoint.ForCurrentUser() with { PipeName = $"PerfMonitor.adaptive-ipc-test.{Guid.NewGuid():N}" };
        public FakeService Service { get; }
        public SnapshotSubscriptionHub Hub { get; } = new();
        public NamedPipeAgentServer Server { get; }
        public Fixture(bool supported = true) { Service = new(supported); Server = new(Endpoint, Service, Hub); }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await Hub.DisposeAsync(); }
    }
    private sealed class FakeService(bool supported) : IAgentIpcService
    {
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        public int ReadRequests { get; private set; }
        public int SetRequests { get; private set; }
        public bool RejectMutation { get; set; }
        public bool WrongResponseInstance { get; set; }
        public bool MissingRequiredResponseField { get; set; }
        public Action<CancellationToken>? BeforeMutation { get; set; }
        public AgentSnapshot ReadLatestSnapshot() => new(ContractVersions.V1, ProductVersions.Agent, InstanceId, 1, null, null, DateTimeOffset.UtcNow, 0,
            new(AvailabilityStates.Available, FreshnessStates.Fresh), new(300, 300), new Dictionary<string, SnapshotGroup>());
        public HealthContract ReadHealth() => throw new NotSupportedException();
        public CapabilitiesContract ReadCapabilities() => new()
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, InstanceId = InstanceId, Groups = [], StableErrorCodes = [],
            History = new() { MetricIds = [], Aggregations = [] },
            Endpoints = supported ? new Dictionary<string, string> { ["adaptiveScheduling"] = "pipe:test/adaptive-scheduling" } : new Dictionary<string, string>(),
        };
        public AdaptiveSchedulingContract ReadAdaptiveScheduling() { ReadRequests++; return State(false, AdaptiveSchedulingPreferences.Responsiveness); }
        public AdaptiveSchedulingContract SetAdaptiveScheduling(AdaptiveSchedulingRequestContract request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); SetRequests++;
            if (RejectMutation) throw new ArgumentException("test-rejected-request");
            BeforeMutation?.Invoke(token);
            return State(request.Enabled, request.Preference);
        }
        private AdaptiveSchedulingContract State(bool enabled, string preference) => new()
        {
            Supported = supported, Enabled = enabled, CanRestore = enabled,
            InstanceId = WrongResponseInstance ? Guid.NewGuid().ToString("N") : InstanceId,
            Preference = preference, DecisionCode = enabled ? AdaptiveSchedulingDecisionCodes.Observing : AdaptiveSchedulingDecisionCodes.Disabled,
            Reason = MissingRequiredResponseField ? null! : "测试状态", ImpactDescription = "仅查询周期", RemainingSeconds = enabled ? 900 : null,
            ExpiresAtUtc = enabled ? DateTimeOffset.UtcNow.AddMinutes(15) : null,
        };
        public ValueTask<HistoryContract> QueryHistoryAsync(HistoryQueryContract query, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(DiagnosticQueryContract query, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ActionResultContract> ExecuteActionAsync(UserActionRequestContract request, CancellationToken token) => throw new NotSupportedException();
    }
}
