using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PerfMonitor.Actions;
using PerfMonitor.Broker;
using PerfMonitor.Broker.Client;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class BrokerPenetrationTests
{
    private const string CallerSid = "S-1-5-21-1000";
    private const string ImageHash =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" +
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [TestMethod]
    [Timeout(30000)]
    public async Task MalformedClientsAreIsolatedFromListener()
    {
        await using var harness = await BrokerHarness.CreateAsync();
        await SendHeaderOnlyAsync(harness.PipeName, length: 0);
        await harness.AssertListenerHealthyAsync();
        await SendHeaderOnlyAsync(
            harness.PipeName,
            checked((uint)BrokerProtocol.AbsoluteMaxMessageSize + 1));
        await harness.AssertListenerHealthyAsync();
        await SendTruncatedFrameAsync(harness.PipeName);
        await harness.AssertListenerHealthyAsync();
        await SendRawJsonAsync(
            harness.PipeName,
            "{not-json");
        await harness.AssertListenerHealthyAsync();
        await SendRawJsonAsync(
            harness.PipeName,
            """
            {
              "type":"hello",
              "requestId":"identity-injection",
              "supportedBrokerProtocolVersions":["1.0"],
              "maxMessageSize":262144,
              "callerSid":"S-1-5-18"
            }
            """);
        await harness.AssertListenerHealthyAsync();
        await SendActionInjectionAsync(harness.PipeName);
        await harness.AssertListenerHealthyAsync();
        await AbortAfterHelloAsync(harness.PipeName);
        await harness.AssertListenerHealthyAsync();

        Assert.AreEqual(0, harness.Executor.CaptureCount);
        Assert.AreEqual(0, harness.Executor.ExecuteCount);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task ConcurrentReplayAndConflictNeverDoubleExecute()
    {
        await using var harness = await BrokerHarness.CreateAsync();
        var request = Request(
            "penetration-concurrent",
            "agent-policy-a");
        var firstClient = harness.CreateClient();
        var secondClient = harness.CreateClient();
        var firstTask = firstClient.ExecuteAsync(
            request,
            CancellationToken.None).AsTask();
        var secondTask = secondClient.ExecuteAsync(
            request with
            {
                DeadlineUtc =
                    DateTimeOffset.UtcNow.AddSeconds(10),
            },
            CancellationToken.None).AsTask();
        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.IsTrue(results.Any(
            result => result.Status == ActionStatuses.DryRun));
        Assert.IsTrue(results.All(result =>
            result.Status is
                ActionStatuses.DryRun or
                ActionStatuses.Indeterminate));

        var replay = await harness.CreateClient().ExecuteAsync(
            request with
            {
                DeadlineUtc =
                    DateTimeOffset.UtcNow.AddSeconds(10),
            },
            CancellationToken.None);
        Assert.AreEqual(ActionStatuses.DryRun, replay.Status);
        var conflict = await harness.CreateClient().ExecuteAsync(
            Request(
                "penetration-concurrent",
                "agent-policy-b"),
            CancellationToken.None);
        Assert.AreEqual(
            ActionStatuses.IdempotencyConflict,
            conflict.Status);
        Assert.AreEqual(1, harness.Executor.CaptureCount);
        Assert.AreEqual(0, harness.Executor.ExecuteCount);
    }

    private static ActionExecutionRequestContract Request(
        string idempotencyKey,
        string agentPolicyVersion) =>
        new()
        {
            IdempotencyKey = idempotencyKey,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            DryRun = false,
            AgentPolicyVersion = agentPolicyVersion,
            Action = new ActionRequestContract
            {
                ActionType =
                    ActionTypes.StartApprovedDiagnostic,
                DiagnosticId =
                    ApprovedDiagnosticIds.BrokerSelfCheck,
            },
        };

    private static async Task SendHeaderOnlyAsync(
        string pipeName,
        uint length)
    {
        await using var pipe = await ConnectAsync(pipeName);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();
    }

    private static async Task SendTruncatedFrameAsync(
        string pipeName)
    {
        await using var pipe = await ConnectAsync(pipeName);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 100);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync("{}"u8.ToArray());
        await pipe.FlushAsync();
    }

    private static async Task SendRawJsonAsync(
        string pipeName,
        string json)
    {
        await using var pipe = await ConnectAsync(pipeName);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)bytes.Length));
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task SendActionInjectionAsync(
        string pipeName)
    {
        await using var pipe = await ConnectAsync(pipeName);
        await BrokerFraming.WriteAsync(
            pipe,
            new BrokerRequestMessage
            {
                Type = BrokerMessageTypes.Hello,
                RequestId = "hello-injection",
                SupportedBrokerProtocolVersions =
                [
                    BrokerProtocol.Version,
                ],
                MaxMessageSize =
                    BrokerProtocol.AbsoluteMaxMessageSize,
            },
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        using var hello = await BrokerFraming.ReadAsync(
            pipe,
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        Assert.IsNotNull(hello);

        await SendJsonFrameAsync(
            pipe,
            """
            {
              "type":"executeAction",
              "requestId":"action-injection",
              "idempotencyKey":"action-injection",
              "deadlineUtc":"2099-01-01T00:00:00.0000000+00:00",
              "dryRun":true,
              "agentPolicyVersion":"test",
              "callerSid":"S-1-5-18",
              "action":{
                "actionType":"start_approved_diagnostic",
                "diagnosticId":"broker.self_check",
                "command":"powershell.exe"
              }
            }
            """);
        using var response = await BrokerFraming.ReadAsync(
            pipe,
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        Assert.IsNotNull(response);
        var message =
            response.Deserialize<BrokerResponseMessage>(
                BrokerJson.Options);
        Assert.IsNotNull(message);
        Assert.AreEqual(BrokerMessageTypes.Error, message.Type);
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            message.Error?.ErrorCode);
    }

    private static async Task AbortAfterHelloAsync(
        string pipeName)
    {
        await using var pipe = await ConnectAsync(pipeName);
        await BrokerFraming.WriteAsync(
            pipe,
            new BrokerRequestMessage
            {
                Type = BrokerMessageTypes.Hello,
                RequestId = "hello-abort",
                SupportedBrokerProtocolVersions =
                [
                    BrokerProtocol.Version,
                ],
                MaxMessageSize =
                    BrokerProtocol.AbsoluteMaxMessageSize,
            },
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        using var hello = await BrokerFraming.ReadAsync(
            pipe,
            BrokerProtocol.AbsoluteMaxMessageSize,
            CancellationToken.None);
        Assert.IsNotNull(hello);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1024);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync("{}"u8.ToArray());
        await pipe.FlushAsync();
    }

    private static async Task SendJsonFrameAsync(
        Stream stream,
        string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)bytes.Length));
        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(5_000);
        return pipe;
    }

    private sealed class BrokerHarness : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        private readonly BrokerAuditStore _audit;
        private readonly BrokerNamedPipeServer _server;

        private BrokerHarness(
            TemporaryDirectory directory,
            BrokerAuditStore audit,
            BrokerNamedPipeServer server,
            CountingExecutor executor,
            string pipeName)
        {
            _directory = directory;
            _audit = audit;
            _server = server;
            Executor = executor;
            PipeName = pipeName;
        }

        public CountingExecutor Executor { get; }
        public string PipeName { get; }

        public static async Task<BrokerHarness> CreateAsync()
        {
            var directory = new TemporaryDirectory();
            var audit = new BrokerAuditStore(
                directory.File("broker-v1.db"));
            await audit.InitializeAsync(CancellationToken.None);
            await audit.RecoverPendingAsync(
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            var executor = new CountingExecutor();
            var policy = new BrokerMachinePolicy
            {
                PolicyVersion = "penetration-v1",
                DryRunOnly = false,
                RequireApprovedClientImage = false,
                EnabledActionTypes =
                [
                    ActionTypes.StartApprovedDiagnostic,
                ],
                AllowedCallerSids = [CallerSid],
                AllowedDiagnosticIds =
                [
                    ApprovedDiagnosticIds.BrokerSelfCheck,
                ],
            }.Validate();
            var coordinator = new BrokerActionCoordinator(
                policy,
                audit,
                executor,
                forceDryRunOnly: true);
            var pipeName =
                $"PerfMonitor.Broker.Pen.{Guid.NewGuid():N}";
            var server = new BrokerNamedPipeServer(
                new BrokerPipeEndpoint(pipeName),
                coordinator,
                new FixedIdentityResolver(),
                forceDryRunOnly: true);
            server.Start();
            return new BrokerHarness(
                directory,
                audit,
                server,
                executor,
                pipeName);
        }

        public BrokerActionClient CreateClient() =>
            new(new BrokerClientOptions
            {
                PipeName = PipeName,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

        public async Task AssertListenerHealthyAsync()
        {
            await Task.Delay(25);
            var capabilities =
                await CreateClient().GetCapabilitiesAsync(
                    CancellationToken.None);
            Assert.IsTrue(capabilities.BrokerAvailable);
            Assert.IsTrue(capabilities.DryRunOnly);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _audit.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class FixedIdentityResolver :
        IBrokerClientIdentityResolver
    {
        public BrokerCallerIdentity Resolve(
            NamedPipeServerStream pipe)
        {
            Assert.IsTrue(pipe.IsConnected);
            return new BrokerCallerIdentity(
                CallerSid,
                5000,
                Path.GetFullPath("perf-monitor-agent.exe"),
                ImageHash);
        }
    }

    public sealed class CountingExecutor :
        IPrivilegedActionExecutor
    {
        private int _captureCount;
        private int _executeCount;

        public int CaptureCount => Volatile.Read(
            ref _captureCount);
        public int ExecuteCount => Volatile.Read(
            ref _executeCount);

        public ValueTask<ActionStateContract> CaptureBeforeAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            CancellationToken cancellationToken)
        {
            _ = action;
            _ = caller;
            _ = policy;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _captureCount);
            return ValueTask.FromResult(
                new ActionStateContract
                {
                    DiagnosticId =
                        ApprovedDiagnosticIds.BrokerSelfCheck,
                });
        }

        public ValueTask<ActionStateContract> ExecuteAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            ActionStateContract before,
            CancellationToken cancellationToken)
        {
            _ = action;
            _ = caller;
            _ = policy;
            _ = before;
            _ = cancellationToken;
            Interlocked.Increment(ref _executeCount);
            throw new AssertFailedException(
                "Penetration input reached mutation executor.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"perf-monitor-broker-pen-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) =>
            System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
