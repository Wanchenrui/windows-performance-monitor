using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PerfMonitor.Actions;
using PerfMonitor.Broker;
using PerfMonitor.Broker.Client;
using PerfMonitor.Contracts;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class BrokerPipeTests
{
    private const string TestSid = "S-1-5-21-1000";
    private const string TestImageHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void BrokerPipeAclDeniesNetworkAndLimitsUserRights()
    {
        var security = BrokerPipeEndpoint.Default.CreateSecurity();
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        var network = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);
        var authenticated = new SecurityIdentifier(
            WellKnownSidType.AuthenticatedUserSid,
            domainSid: null);
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);

        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.IsTrue(rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Deny &&
            rule.IdentityReference.Equals(network) &&
            rule.PipeAccessRights.HasFlag(
                PipeAccessRights.FullControl)));
        var userRule = rules.Single(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Equals(authenticated));
        Assert.IsTrue(
            userRule.PipeAccessRights.HasFlag(
                PipeAccessRights.ReadWrite));
        Assert.IsFalse(
            userRule.PipeAccessRights.HasFlag(
                PipeAccessRights.ChangePermissions));
        Assert.IsTrue(rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference.Equals(localSystem) &&
            rule.PipeAccessRights.HasFlag(
                PipeAccessRights.FullControl)));
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task TransportResolverUsesActualClientSidPidAndImage()
    {
        var endpoint = TestEndpoint();
        await using var server = endpoint.CreateServerStream(
            firstInstance: true);
        await using var client = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        var wait = server.WaitForConnectionAsync();
        await client.ConnectAsync(5_000);
        await wait;

        var identity =
            new BrokerClientIdentityResolver().Resolve(server);
        using var windowsIdentity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        using var process = Process.GetCurrentProcess();
        var imagePath = process.MainModule?.FileName ??
            throw new AssertFailedException(
                "Test process image path is unavailable.");
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(
                await File.ReadAllBytesAsync(imagePath)));

        Assert.AreEqual(
            windowsIdentity.User?.Value,
            identity.Sid);
        Assert.AreEqual(Environment.ProcessId, identity.ClientPid);
        Assert.AreEqual(
            Path.GetFullPath(imagePath),
            identity.ClientImagePath,
            ignoreCase: true);
        Assert.AreEqual(expectedHash, identity.ClientImageSha256);
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task ClientServerDryRunRoundTripNeverMutates()
    {
        using var directory = new TemporaryDirectory();
        await using var audit = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await audit.InitializeAsync(CancellationToken.None);
        await audit.RecoverPendingAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var executor = new FakeExecutor();
        var policy = new BrokerMachinePolicy
        {
            PolicyVersion = "pipe-test-v1",
            DryRunOnly = false,
            RequireApprovedClientImage = false,
            RequireTrustedClientSignature = false,
            RequireProtectedClientPath = false,
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
            ],
            AllowedCallerSids = [TestSid],
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
        var endpoint = TestEndpoint();
        await using var server = new BrokerNamedPipeServer(
            endpoint,
            coordinator,
            new FakeIdentityResolver(),
            forceDryRunOnly: true);
        server.Start();
        var client = new BrokerActionClient(
            new BrokerClientOptions
            {
                PipeName = endpoint.PipeName,
            });

        var capabilities = await client.GetCapabilitiesAsync(
            CancellationToken.None);
        Assert.IsTrue(capabilities.BrokerAvailable);
        Assert.IsTrue(capabilities.DryRunOnly);
        Assert.HasCount(1, capabilities.Actions);

        var request = new ActionExecutionRequestContract
        {
            IdempotencyKey = "pipe-dry-run",
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            DryRun = false,
            AgentPolicyVersion = "agent-test-v1",
            Action = new ActionRequestContract
            {
                ActionType =
                    ActionTypes.StartApprovedDiagnostic,
                DiagnosticId =
                    ApprovedDiagnosticIds.BrokerSelfCheck,
            },
        };
        var result = await client.ExecuteAsync(
            request,
            CancellationToken.None);
        var replay = await client.ExecuteAsync(
            request with
            {
                DeadlineUtc =
                    DateTimeOffset.UtcNow.AddSeconds(10),
            },
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.DryRun, result.Status);
        Assert.AreEqual(result, replay);
        Assert.AreEqual(1, executor.CaptureCount);
        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.IsTrue(client.LastKnownAvailable);
        await server.StopAsync();
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task PendingAcceptCancellationIsCleanShutdown()
    {
        using var directory = new TemporaryDirectory();
        await using var audit = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await audit.InitializeAsync(CancellationToken.None);
        await audit.RecoverPendingAsync(
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var coordinator = new BrokerActionCoordinator(
            BrokerMachinePolicy.Default,
            audit,
            new FakeExecutor(),
            forceDryRunOnly: true);

        for (var index = 0; index < 32; index++)
        {
            await using var server = new BrokerNamedPipeServer(
                TestEndpoint(),
                coordinator,
                new FakeIdentityResolver(),
                forceDryRunOnly: true);
            server.Start();
            await server.StopAsync();
            Assert.IsTrue(
                server.Completion.IsCompletedSuccessfully);
        }
    }

    private static BrokerPipeEndpoint TestEndpoint() =>
        new($"PerfMonitor.Broker.Test.{Guid.NewGuid():N}");

    private sealed class FakeIdentityResolver :
        IBrokerClientIdentityResolver
    {
        public BrokerCallerIdentity Resolve(
            NamedPipeServerStream pipe)
        {
            Assert.IsTrue(pipe.IsConnected);
            return new BrokerCallerIdentity(
                TestSid,
                5000,
                Path.GetFullPath("perf-monitor-agent.exe"),
                TestImageHash);
        }
    }

    private sealed class FakeExecutor :
        IPrivilegedActionExecutor
    {
        public int CaptureCount { get; private set; }
        public int ExecuteCount { get; private set; }

        public ValueTask<ActionStateContract> CaptureBeforeAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return ValueTask.FromResult(new ActionStateContract
            {
                DiagnosticId = action.DiagnosticId,
            });
        }

        public ValueTask<ActionStateContract> ExecuteAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            ActionStateContract before,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            throw new AssertFailedException(
                "Dry-run reached mutation executor.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"perf-monitor-broker-pipe-{Guid.NewGuid():N}");
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
