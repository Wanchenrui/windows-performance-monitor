using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class StorageProjectionTests
{
    private const string Sensitive = "SENSITIVE_SENTINEL_93751";

    [TestMethod]
    public async Task ActualDatabaseContainsOnlyProjectionAndReplaysAllSevenRulesExactly()
    {
        using var temp = new TestDirectory();
        var policy = FastPolicy();
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database, DiagnosticPolicy = policy });
        await store.StartAsync(CancellationToken.None);
        Assert.AreEqual(SqliteHistoryState.Healthy, store.Health.State);
        var origin = DateTimeOffset.UtcNow.AddMinutes(-1);
        AgentSnapshot[] snapshots =
        [
            FullSnapshot(1, origin, 0, true), FullSnapshot(2, origin.AddSeconds(6), 6, true),
            FullSnapshot(3, origin.AddSeconds(7), 7, false), FullSnapshot(4, origin.AddSeconds(8), 8, false),
        ];
        foreach (var snapshot in snapshots) Assert.IsTrue(store.TryPublish(snapshot));
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        var expected = DiagnosticReplay.Replay(snapshots, policy);
        var actual = await DiagnosticReplay.ReplayAsync(store.ReadSnapshotsForReplayAsync(
            origin.AddSeconds(-1).ToUnixTimeMilliseconds(), origin.AddSeconds(10).ToUnixTimeMilliseconds(),
            100, CancellationToken.None), policy);
        CollectionAssert.AreEquivalent(DiagnosticRuleIds.All.ToArray(),
            expected.Where(item => item.State == DiagnosticStates.Active).Select(item => item.RuleId).Distinct().ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(expected, ContractJson.Options),
            JsonSerializer.Serialize(actual, ContractJson.Options));

        await using var connection = await OpenAsync(temp.Database);
        var json = await TextAsync(connection, "SELECT group_concat(snapshot_json) FROM snapshots_raw;");
        Assert.IsFalse(json.Contains(Sensitive, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("creationTimeTicks", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"pid\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("devices", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("mountpoint", StringComparison.Ordinal));
        StringAssert.Contains(json, "diagnosticIdentity");
        StringAssert.Contains(json, "GameApp");
        StringAssert.Contains(json, "policyFingerprint");
        Assert.AreEqual(3L, await NumberAsync(connection, "PRAGMA user_version;"));
        Assert.AreEqual(0L, store.Health.QueueBytes);
        var physical = await ReadSharedAsync(temp.Database);
        Assert.IsFalse(Encoding.UTF8.GetString(physical).Contains(Sensitive, StringComparison.Ordinal));
        if (File.Exists(temp.Database + "-wal"))
            Assert.IsFalse(Encoding.UTF8.GetString(await ReadSharedAsync(temp.Database + "-wal"))
                .Contains(Sensitive, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EmptyWatchlistPersistsNoProcessRowsAndPolicyChangesRefuseReplay()
    {
        using var temp = new TestDirectory();
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(store.TryPublish(FullSnapshot(1, now, 0, true)));
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await using var connection = await OpenAsync(temp.Database);
        var json = await TextAsync(connection, "SELECT snapshot_json FROM snapshots_raw;");
        Assert.IsFalse(json.Contains("GameApp", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("diagnosticIdentity", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(Sensitive, StringComparison.Ordinal));
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await DiagnosticReplay.ReplayAsync(store.ReadSnapshotsForReplayAsync(
                now.AddSeconds(-1).ToUnixTimeMilliseconds(), now.AddSeconds(1).ToUnixTimeMilliseconds(),
                10, CancellationToken.None), FastPolicy()));
        Assert.AreEqual("diagnostic_replay_projection_incompatible", exception.Message);
    }

    [TestMethod]
    public async Task HeldObservationsDeduplicateAndQualityIsPreservedWithoutFalseZero()
    {
        using var temp = new TestDirectory();
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var original = CpuSnapshot(1, now, 12.5);
        Assert.IsTrue(store.TryPublish(original));
        Assert.IsTrue(store.TryPublish(original with { Sequence = 2, CompletedAtUtc = now.AddSeconds(5), ElapsedSeconds = 5 }));
        var failed = CpuSnapshot(3, now.AddSeconds(6), 0);
        failed = failed with { Groups = failed.Groups.ToDictionary(item => item.Key,
            item => item.Value with { Availability = AvailabilityStates.PermissionDenied,
                Errors = [new(StableErrorCodes.AccessDenied, MetricIds.SystemCpuUtilization)] }) };
        Assert.IsTrue(store.TryPublish(failed));
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        await using var connection = await OpenAsync(temp.Database);
        Assert.AreEqual(2L, await NumberAsync(connection, "SELECT COUNT(*) FROM metrics_raw;"));
        Assert.AreEqual(1L, await NumberAsync(connection, "SELECT COUNT(*) FROM metrics_raw WHERE value IS NULL AND availability='permission_denied';"));
        Assert.AreEqual(1L, await NumberAsync(connection, "SELECT sample_count FROM metric_rollups WHERE bucket_seconds=3600;"));
        Assert.AreEqual(now.ToUnixTimeMilliseconds(), await NumberAsync(connection,
            "SELECT sample_time_ms FROM metrics_raw WHERE value IS NOT NULL;"));
        StringAssert.Contains(await TextAsync(connection, "SELECT source_id FROM metrics_raw LIMIT 1;"), SourceIds.SystemCpu);
        var history = await store.QueryAsync(new() { MetricIds = [MetricIds.SystemCpuUtilization],
            FromEpochMs = now.AddSeconds(-1).ToUnixTimeMilliseconds(), ToEpochMs = now.AddSeconds(10).ToUnixTimeMilliseconds(),
            MaxPoints = 10 }, "response", CancellationToken.None);
        Assert.AreEqual(1, history.SourcePointCount);
        Assert.AreEqual(12.5, history.Points.Single().Metrics[MetricIds.SystemCpuUtilization].Last);
    }

    [TestMethod]
    public async Task ReplayUsesPersistenceOrderAcrossUtcRollback()
    {
        using var temp = new TestDirectory();
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = CpuSnapshot(1, now, 95);
        var second = CpuSnapshot(2, now.AddSeconds(-20), 70) with { ElapsedSeconds = 1 };
        second = second with { Groups = second.Groups.ToDictionary(item => item.Key,
            item => item.Value with { ObservedElapsedSeconds = 1 }) };
        Assert.IsTrue(store.TryPublish(first));
        Assert.IsTrue(store.TryPublish(second));
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        var order = new List<long>();
        await foreach (var snapshot in store.ReadSnapshotsForReplayAsync(now.AddSeconds(-30).ToUnixTimeMilliseconds(),
            now.AddSeconds(1).ToUnixTimeMilliseconds(), 10, CancellationToken.None)) order.Add(snapshot.Sequence);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, order);
    }

    [TestMethod]
    public async Task ProjectionPreservesHeldDeliveriesAndExplicitDeliveryGaps()
    {
        using var temp = new TestDirectory();
        var policy = FastPolicy();
        policy = policy with { HighCpu = policy.HighCpu with { ActivateDebounceSeconds = 2 } };
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database, DiagnosticPolicy = policy });
        await store.StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        AgentSnapshot Frame(long sequence, long delivery, double elapsed)
        {
            var frame = CpuSnapshot(sequence, now.AddSeconds(elapsed), 95);
            return frame with { DeliverySequence = delivery, ElapsedSeconds = elapsed,
                Groups = frame.Groups.ToDictionary(item => item.Key, item => item.Value with { ObservedElapsedSeconds = elapsed }) };
        }
        var first = Frame(1, 1, 0);
        var held = first with { DeliverySequence = 2, ElapsedSeconds = 0.5 };
        AgentSnapshot[] input = [first, held, Frame(50, 4, 1), Frame(99, 5, 2), Frame(130, 6, 3)];
        foreach (var frame in input) Assert.IsTrue(store.TryPublish(frame));
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        var expected = DiagnosticReplay.Replay(input, policy);
        var actual = await DiagnosticReplay.ReplayAsync(store.ReadSnapshotsForReplayAsync(
            now.AddSeconds(-1).ToUnixTimeMilliseconds(), now.AddSeconds(5).ToUnixTimeMilliseconds(),
            20, CancellationToken.None), policy);
        Assert.AreEqual(JsonSerializer.Serialize(expected, ContractJson.Options), JsonSerializer.Serialize(actual, ContractJson.Options));
        var activation = actual.Single(item => item.RuleId == DiagnosticRuleIds.HighCpu);
        Assert.AreEqual(now.AddSeconds(1), activation.FirstSeenUtc);
        var replayFrames = new List<AgentSnapshot>();
        await foreach (var frame in store.ReadSnapshotsForReplayAsync(now.AddSeconds(-1).ToUnixTimeMilliseconds(),
            now.AddSeconds(5).ToUnixTimeMilliseconds(), 5, CancellationToken.None)) replayFrames.Add(frame);
        Assert.AreEqual(5, replayFrames.Count);
        var limit = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in store.ReadSnapshotsForReplayAsync(now.AddSeconds(-1).ToUnixTimeMilliseconds(),
                now.AddSeconds(5).ToUnixTimeMilliseconds(), 4, CancellationToken.None)) { }
        });
        Assert.AreEqual("diagnostic_replay_snapshot_limit", limit.Message);
        await using var connection = await OpenAsync(temp.Database);
        Assert.AreEqual(5L, await NumberAsync(connection, "SELECT COUNT(*) FROM snapshots_raw;"));
        Assert.AreEqual(2L, await NumberAsync(connection, "SELECT COUNT(*) FROM snapshots_raw WHERE sequence=1;"));
        Assert.AreEqual(4L, await NumberAsync(connection, "SELECT COUNT(*) FROM metrics_raw;"));
    }

    [TestMethod]
    public async Task PayloadAndPendingByteLimitsDropWithExplicitCounters()
    {
        using var temp = new TestDirectory();
        await using var store = new SqliteHistoryStore(new()
        {
            DatabasePath = temp.Database, MaxSnapshotBytes = 4096, MaxDiagnosticEventBytes = 4096,
            MaxQueueBytes = 8192, MaxBatchBytes = 4096,
        });
        await store.StartAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var oversized = CpuSnapshot(1, now, 50);
        oversized = oversized with { Groups = oversized.Groups.ToDictionary(item => item.Key,
            item => item.Value with { Errors = Enumerable.Range(0, 100)
                .Select(_ => new ProviderError(StableErrorCodes.InvalidData, MetricIds.SystemCpuUtilization)).ToArray() }) };
        Assert.IsFalse(store.TryPublish(oversized));
        Assert.AreEqual(1L, store.Health.DroppedPayloadTooLarge);
        await using var blocker = await OpenAsync(temp.Database);
        using (var transaction = blocker.BeginTransaction())
        {
            for (var index = 0; index < 40; index++) store.TryPublish(CpuSnapshot(index + 2, now.AddSeconds(index), 50));
            Assert.IsTrue(store.Health.DroppedQueueByteLimit > 0);
            Assert.IsTrue(store.Health.PeakQueueBytes <= 8192);
            transaction.Commit();
        }
        await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0L, store.Health.QueueBytes);
        Assert.AreEqual(SqliteHistoryState.Healthy, store.Health.State);
    }

    private static DiagnosticPolicy FastPolicy()
    {
        var defaults = DiagnosticPolicy.Default;
        ThresholdRulePolicy Fast(ThresholdRulePolicy policy) => policy with
        { ActivateDebounceSeconds = 0, RecoverDebounceSeconds = 0, CooldownSeconds = 0 };
        return defaults with
        {
            HighCpu = Fast(defaults.HighCpu), MemoryPressure = Fast(defaults.MemoryPressure),
            SystemDiskLow = Fast(defaults.SystemDiskLow), ProcessCpuSpike = Fast(defaults.ProcessCpuSpike),
            SamplingGap = Fast(defaults.SamplingGap),
            ProviderUnavailable = defaults.ProviderUnavailable with
            { ActivateDebounceSeconds = 0, RecoverDebounceSeconds = 0, CooldownSeconds = 0 },
            AgentResourceAnomaly = defaults.AgentResourceAnomaly with
            { ActivateDebounceSeconds = 0, RecoverDebounceSeconds = 0, CooldownSeconds = 0 },
            WatchedProcessNames = ["GameApp"],
        };
    }

    private static AgentSnapshot CpuSnapshot(long sequence, DateTimeOffset time, double cpu) =>
        Snapshot(sequence, time, sequence - 1,
            new Dictionary<string, SnapshotGroup> { [GroupIds.SystemCpu] = Scalar(time, MetricIds.SystemCpuUtilization, cpu, Units.Percent) });

    private static AgentSnapshot FullSnapshot(long sequence, DateTimeOffset time, double elapsed, bool breach)
    {
        var amount = breach ? 95 : 20;
        var groups = new Dictionary<string, SnapshotGroup>
        {
            [GroupIds.SystemCpu] = Scalar(time, MetricIds.SystemCpuUtilization, amount, Units.Percent),
            [GroupIds.Memory] = Scalar(time, MetricIds.MemoryUtilization, amount, Units.Percent),
            [GroupIds.Volumes] = Group(time, new JsonArray(new JsonObject
            {
                ["isSystem"] = true, ["mountpoint"] = Sensitive,
                ["metrics"] = new JsonObject { [MetricIds.VolumeUtilization] = MetricJson.Value(amount, Units.Percent, SourceIds.Volumes) },
            })),
            [GroupIds.Processes] = Group(time, new JsonArray(Process("GameApp", amount, 123), Process(Sensitive, 99, 456))),
            [GroupIds.Gpu] = Scalar(time, MetricIds.GpuLoadMaxPercent, 40, Units.Percent),
            [GroupIds.Sensors] = Scalar(time, MetricIds.HardwareTemperatureMaxCelsius, 45, Units.Celsius),
            [GroupIds.Uptime] = Group(time, null) with { Availability = breach ? AvailabilityStates.Timeout : AvailabilityStates.Available },
            [GroupIds.Self] = Group(time, new JsonObject { ["metrics"] = new JsonObject
            {
                [MetricIds.AgentCpuCoreEquivalent] = MetricJson.Value(breach ? 30d : 5d, Units.Percent, SourceIds.SelfProcess),
                [MetricIds.AgentPrivateBytes] = MetricJson.Value(breach ? 300_000_000L : 100_000_000L, Units.Byte, SourceIds.SelfProcess),
            } }),
        };
        foreach (var id in new[] { GroupIds.Gpu, GroupIds.Sensors })
        {
            var data = (JsonObject)groups[id].Data!;
            data["devices"] = new JsonArray(new JsonObject { ["name"] = Sensitive, ["deviceId"] = Sensitive });
            groups[id] = groups[id] with { Data = data };
        }
        return Snapshot(sequence, time, elapsed, groups);
    }

    private static JsonObject Process(string name, double cpu, int pid) => new()
    {
        ["name"] = name, ["commandLine"] = Sensitive, ["path"] = Sensitive,
        ["identity"] = new JsonObject { ["pid"] = pid, ["creationTimeTicks"] = 123_456_789L },
        ["cpuReady"] = true, ["metrics"] = new JsonObject
        { [MetricIds.ProcessCpuNormalized] = MetricJson.Value(cpu, Units.Percent, SourceIds.ProcessCpu) },
    };

    private static SnapshotGroup Scalar(DateTimeOffset time, string metric, double value, string unit) =>
        Group(time, new JsonObject { ["metrics"] = new JsonObject { [metric] = MetricJson.Value(value, unit,
            metric == MetricIds.SystemCpuUtilization ? SourceIds.SystemCpu : "test") } });

    private static SnapshotGroup Group(DateTimeOffset time, JsonNode? data) =>
        new("test", time, AvailabilityStates.Available, FreshnessStates.Fresh, ProviderCoverage.Complete, [], data);

    private static AgentSnapshot Snapshot(long sequence, DateTimeOffset time, double elapsed, IReadOnlyDictionary<string, SnapshotGroup> groups) =>
        new(ContractVersions.V1, ProductVersions.Agent, "projection-instance", sequence, time, time, time, 0,
            new("available", "fresh"), new(3600, 3600), groups.ToDictionary(item => item.Key,
                item => item.Value with { ObservationSequence = sequence, ObservedElapsedSeconds = elapsed }))
        { ElapsedSeconds = elapsed };

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> NumberAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perf-monitor-projection-{Guid.NewGuid():N}");
        public string Database => System.IO.Path.Combine(Path, "history.db");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
