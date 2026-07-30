using PerfMonitor.Actions;
using PerfMonitor.Broker;
using PerfMonitor.Contracts;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class BrokerAuditStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 7, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CompletedResultIsReplayedWithoutNewReservation()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await store.InitializeAsync(CancellationToken.None);
        var request = AuditRequest("intent-1", "AA");

        var reservation = await store.ReserveAsync(
            request,
            CancellationToken.None);
        Assert.AreEqual(
            ActionAuditReservationKind.New,
            reservation.Kind);
        var result = Result(
            reservation.ActionId,
            request.Request.IdempotencyKey,
            ActionStatuses.Succeeded);
        await store.CompleteAsync(
            request.CallerSid,
            request.Request.IdempotencyKey,
            result,
            CancellationToken.None);

        var replay = await store.ReserveAsync(
            request,
            CancellationToken.None);
        Assert.AreEqual(
            ActionAuditReservationKind.Replay,
            replay.Kind);
        Assert.AreEqual(result, replay.StoredResult);

        var record = await store.ReadAsync(
            request.CallerSid,
            request.Request.IdempotencyKey);
        Assert.IsNotNull(record);
        Assert.AreEqual(
            ActionStatuses.Succeeded,
            record.Status);
        Assert.AreEqual(result.Before, record.Before);
        Assert.AreEqual(result.After, record.After);
        Assert.AreEqual("test-broker-v1", record.BrokerPolicyVersion);
        Assert.AreEqual("test-agent-v1", record.AgentPolicyVersion);
    }

    [TestMethod]
    public async Task DifferentHashForSameCallerAndKeyConflicts()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await store.InitializeAsync(CancellationToken.None);
        var first = AuditRequest("same-key", "AA");
        var second = AuditRequest("same-key", "BB");

        var created = await store.ReserveAsync(
            first,
            CancellationToken.None);
        var conflict = await store.ReserveAsync(
            second,
            CancellationToken.None);

        Assert.AreEqual(
            ActionAuditReservationKind.New,
            created.Kind);
        Assert.AreEqual(
            ActionAuditReservationKind.Conflict,
            conflict.Kind);
        Assert.AreEqual(created.ActionId, conflict.ActionId);
    }

    [TestMethod]
    public async Task PendingRecoversAsIndeterminateAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("broker-v1.db");
        var request = AuditRequest("crash-key", "AA");
        string actionId;
        await using (var first = new BrokerAuditStore(path))
        {
            await first.InitializeAsync(CancellationToken.None);
            var reservation = await first.ReserveAsync(
                request,
                CancellationToken.None);
            actionId = reservation.ActionId;
        }

        await using var restarted = new BrokerAuditStore(path);
        await restarted.InitializeAsync(CancellationToken.None);
        await restarted.RecoverPendingAsync(
            Now.AddMinutes(1),
            CancellationToken.None);
        var recovered = await restarted.ReserveAsync(
            request,
            CancellationToken.None);

        Assert.AreEqual(
            ActionAuditReservationKind.Indeterminate,
            recovered.Kind);
        Assert.AreEqual(actionId, recovered.ActionId);
        Assert.AreEqual(
            ActionStatuses.Indeterminate,
            recovered.StoredResult?.Status);
        Assert.AreEqual(
            ActionErrorCodes.IdempotencyIndeterminate,
            recovered.StoredResult?.ErrorCode);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateGetsOneNewReservation()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await store.InitializeAsync(CancellationToken.None);
        var request = AuditRequest("parallel-key", "AA");

        var reservations = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
                await store.ReserveAsync(
                    request,
                    CancellationToken.None)));

        Assert.AreEqual(
            1,
            reservations.Count(item =>
                item.Kind == ActionAuditReservationKind.New));
        Assert.AreEqual(
            7,
            reservations.Count(item =>
                item.Kind ==
                    ActionAuditReservationKind.Indeterminate));
        Assert.AreEqual(
            1,
            reservations.Select(item => item.ActionId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [TestMethod]
    public async Task CorruptAuditDatabaseFailsInitialization()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("broker-v1.db");
        await File.WriteAllTextAsync(
            path,
            "not a sqlite database");
        await using var store = new BrokerAuditStore(path);

        await Assert.ThrowsExactlyAsync<
            Microsoft.Data.Sqlite.SqliteException>(
                () => store.InitializeAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task PolicyDenialPersistsActualTransportIdentity()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new BrokerAuditStore(
            directory.File("broker-v1.db"));
        await store.InitializeAsync(CancellationToken.None);
        var executor = new NeverCalledExecutor();
        var policy = new BrokerMachinePolicy
        {
            PolicyVersion = "denial-machine-v1",
            DryRunOnly = false,
            RequireApprovedClientImage = false,
            RequireTrustedClientSignature = false,
            RequireProtectedClientPath = false,
            EnabledActionTypes =
            [
                ActionTypes.TerminateProcess,
            ],
            AllowedCallerSids =
            [
                "S-1-5-21-9999",
            ],
        }.Validate();
        var coordinator = new BrokerActionCoordinator(
            policy,
            store,
            executor,
            forceDryRunOnly: false,
            new FixedTimeProvider(Now));
        var request = AuditRequest(
            "denied-intent",
            "AA").Request;
        var caller = new BrokerCallerIdentity(
            "S-1-5-21-1000",
            4321,
            Path.GetFullPath("perf-monitor-agent.exe"),
            new string('C', 64));

        var result = await coordinator.ExecuteAsync(
            request,
            caller,
            Now,
            CancellationToken.None);
        var record = await store.ReadAsync(
            caller.Sid,
            request.IdempotencyKey);

        Assert.AreEqual(ActionStatuses.Denied, result.Status);
        Assert.AreEqual(
            ActionErrorCodes.CallerIdentityDenied,
            result.ErrorCode);
        Assert.IsNotNull(record);
        Assert.AreEqual(ActionStatuses.Denied, record.Status);
        Assert.AreEqual(caller.ClientPid, record.ClientPid);
        Assert.AreEqual(
            caller.ClientImageSha256,
            record.ClientImageSha256);
        Assert.AreEqual(
            policy.PolicyVersion,
            record.BrokerPolicyVersion);
        Assert.IsNull(record.Before);
        Assert.IsNull(record.After);
        Assert.AreEqual(0, executor.CallCount);
    }

    private static ActionAuditRequest AuditRequest(
        string key,
        string hashPrefix)
    {
        var request = new ActionExecutionRequestContract
        {
            IdempotencyKey = key,
            DeadlineUtc = Now.AddSeconds(10),
            DryRun = false,
            AgentPolicyVersion = "test-agent-v1",
            Action = new ActionRequestContract
            {
                ActionType = ActionTypes.TerminateProcess,
                Pid = 1234,
                CreationTimeTicks = 123456789,
            },
        };
        return new ActionAuditRequest(
            "S-1-5-21-1000",
            4321,
            new string('C', 64),
            hashPrefix + new string('0', 62),
            "test-broker-v1",
            Now,
            request);
    }

    private static ActionResultContract Result(
        string actionId,
        string key,
        string status) =>
        new()
        {
            ActionId = actionId,
            IdempotencyKey = key,
            Status = status,
            ReceivedAtUtc = Now,
            StartedAtUtc = Now.AddMilliseconds(1),
            CompletedAtUtc = Now.AddMilliseconds(2),
            Before = new ActionStateContract
            {
                Pid = 1234,
                CreationTimeTicks = 123456789,
                IsRunning = true,
                Priority = ActionPriorities.Normal,
            },
            After = new ActionStateContract
            {
                Pid = 1234,
                CreationTimeTicks = 123456789,
                IsRunning = false,
                Priority = ActionPriorities.Normal,
            },
        };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"perf-monitor-broker-test-{Guid.NewGuid():N}");
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

    private sealed class NeverCalledExecutor :
        IPrivilegedActionExecutor
    {
        public int CallCount { get; private set; }

        public ValueTask<ActionStateContract> CaptureBeforeAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException(
                "Denied action reached the executor.");
        }

        public ValueTask<ActionStateContract> ExecuteAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            ActionStateContract before,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException(
                "Denied action reached the mutation executor.");
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
