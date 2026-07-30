using System.Buffers.Binary;
using System.Text.Json;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.ActionsTests;

[TestClass]
public sealed class BrokerProtocolTests
{
    [TestMethod]
    public async Task FramingRoundTripsTypedRequest()
    {
        var request = ExecuteRequest();
        await using var stream = new MemoryStream();
        await BrokerFraming.WriteAsync(
            stream,
            request,
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        stream.Position = 0;

        using var document = await BrokerFraming.ReadAsync(
            stream,
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        Assert.IsNotNull(document);
        var parsed = BrokerRequestParser.Parse(document);

        Assert.AreEqual(
            BrokerMessageTypes.ExecuteAction,
            parsed.Type);
        Assert.AreEqual(
            ActionTypes.TerminateProcess,
            parsed.Action?.ActionType);
        Assert.AreEqual(321, parsed.Action?.Pid);
    }

    [TestMethod]
    public async Task OversizedLengthIsRejectedBeforePayloadRead()
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            BrokerProtocol.AbsoluteMaxMessageSize + 1U);
        await using var stream = new MemoryStream(bytes);

        var exception =
            await Assert.ThrowsExactlyAsync<BrokerProtocolException>(
                async () =>
                {
                    using var unused =
                        await BrokerFraming.ReadAsync(
                            stream,
                            BrokerProtocol.AbsoluteMaxMessageSize,
                            CancellationToken.None);
                });
        Assert.AreEqual(
            ActionErrorCodes.MessageTooLarge,
            exception.ErrorCode);
    }

    [TestMethod]
    public async Task TruncatedPayloadIsRejected()
    {
        var bytes = new byte[sizeof(uint) + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 10);
        await using var stream = new MemoryStream(bytes);

        var exception =
            await Assert.ThrowsExactlyAsync<BrokerProtocolException>(
                async () =>
                {
                    using var unused =
                        await BrokerFraming.ReadAsync(
                            stream,
                            BrokerProtocol.AbsoluteMaxMessageSize,
                            CancellationToken.None);
                });
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            exception.ErrorCode);
    }

    [TestMethod]
    [DataRow("callerSid")]
    [DataRow("clientPid")]
    [DataRow("clientPath")]
    [DataRow("command")]
    [DataRow("arguments")]
    [DataRow("script")]
    [DataRow("registryPath")]
    public void ExecutionOrIdentityFieldIsRejected(string field)
    {
        var json = $$"""
            {
              "type": "executeAction",
              "requestId": "request-1",
              "idempotencyKey": "intent-1",
              "deadlineUtc": "2026-07-30T06:00:10Z",
              "dryRun": true,
              "agentPolicyVersion": "test-v1",
              "{{field}}": "untrusted",
              "action": {
                "actionType": "terminate_process",
                "pid": 321,
                "creationTimeTicks": 123456789
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var exception =
            Assert.ThrowsExactly<BrokerProtocolException>(
                () => BrokerRequestParser.Parse(document));
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            exception.ErrorCode);
    }

    [TestMethod]
    public void UnknownActionFieldIsRejected()
    {
        const string json = """
            {
              "type": "executeAction",
              "requestId": "request-1",
              "idempotencyKey": "intent-1",
              "deadlineUtc": "2026-07-30T06:00:10Z",
              "dryRun": true,
              "agentPolicyVersion": "test-v1",
              "action": {
                "actionType": "terminate_process",
                "pid": 321,
                "creationTimeTicks": 123456789,
                "path": "C:\\untrusted.exe"
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        Assert.ThrowsExactly<BrokerProtocolException>(
            () => BrokerRequestParser.Parse(document));
    }

    [TestMethod]
    public void CallerCannotRequestRealTimePriority()
    {
        const string json = """
            {
              "type": "executeAction",
              "requestId": "request-1",
              "idempotencyKey": "intent-1",
              "deadlineUtc": "2026-07-30T06:00:10Z",
              "dryRun": true,
              "agentPolicyVersion": "test-v1",
              "action": {
                "actionType": "set_process_priority",
                "pid": 321,
                "creationTimeTicks": 123456789,
                "priority": "real_time"
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var exception =
            Assert.ThrowsExactly<BrokerProtocolException>(
                () => BrokerRequestParser.Parse(document));
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            exception.ErrorCode);
    }

    [TestMethod]
    [DataRow("Broker.self_check")]
    [DataRow("broker:self_check")]
    [DataRow("-broker.self_check")]
    [DataRow("broker/self_check")]
    public void ApprovedIdUsesClosedLowercaseGrammar(string approvedId)
    {
        var json = $$"""
            {
              "type": "executeAction",
              "requestId": "request-1",
              "idempotencyKey": "intent-1",
              "deadlineUtc": "2026-07-30T06:00:10Z",
              "dryRun": true,
              "agentPolicyVersion": "test-v1",
              "action": {
                "actionType": "start_approved_diagnostic",
                "diagnosticId": "{{approvedId}}"
              }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var exception =
            Assert.ThrowsExactly<BrokerProtocolException>(
                () => BrokerRequestParser.Parse(document));
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            exception.ErrorCode);
    }

    [TestMethod]
    public void FourWhitelistedShapesParse()
    {
        string[] actionJson =
        [
            """
            {
              "actionType": "set_process_priority",
              "pid": 321,
              "creationTimeTicks": 123456789,
              "priority": "below_normal"
            }
            """,
            """
            {
              "actionType": "terminate_process",
              "pid": 321,
              "creationTimeTicks": 123456789
            }
            """,
            """
            {
              "actionType": "start_approved_diagnostic",
              "diagnosticId": "broker.self_check"
            }
            """,
            """
            {
              "actionType": "apply_approved_power_profile",
              "powerProfileId": "balanced"
            }
            """,
        ];

        foreach (var action in actionJson)
        {
            var json = $$"""
                {
                  "type": "executeAction",
                  "requestId": "request-1",
                  "idempotencyKey": "intent-1",
                  "deadlineUtc": "2026-07-30T06:00:10Z",
                  "dryRun": true,
                  "agentPolicyVersion": "test-v1",
                  "action": {{action}}
                }
                """;
            using var document = JsonDocument.Parse(json);
            Assert.IsNotNull(
                BrokerRequestParser.Parse(document).Action);
        }
    }

    private static BrokerRequestMessage ExecuteRequest() =>
        new()
        {
            Type = BrokerMessageTypes.ExecuteAction,
            RequestId = "request-1",
            IdempotencyKey = "intent-1",
            DeadlineUtc = new DateTimeOffset(
                2026,
                7,
                30,
                6,
                0,
                10,
                TimeSpan.Zero),
            DryRun = true,
            AgentPolicyVersion = "test-v1",
            Action = new ActionRequestContract
            {
                ActionType = ActionTypes.TerminateProcess,
                Pid = 321,
                CreationTimeTicks = 123456789,
            },
        };
}
