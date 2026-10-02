using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class StorageMigrationTests
{
    [TestMethod]
    public async Task VersionTwoMigrationPreservesHistoryEventsAndVerifiedBackupButRefusesLegacyReplay()
    {
        using var temp = new TestDirectory();
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedAsync(temp.Database, time);
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        Assert.AreEqual(SqliteHistoryState.Healthy, store.Health.State);
        var history = await store.QueryAsync(new()
        {
            MetricIds = [MetricIds.SystemCpuUtilization], FromEpochMs = time.AddSeconds(-1).ToUnixTimeMilliseconds(),
            ToEpochMs = time.AddSeconds(1).ToUnixTimeMilliseconds(), MaxPoints = 10,
        }, "response", CancellationToken.None);
        Assert.AreEqual(95d, history.Points.Single().Metrics[MetricIds.SystemCpuUtilization].Last);
        var events = await store.QueryDiagnosticsAsync(new()
        {
            FromEpochMs = time.AddSeconds(-1).ToUnixTimeMilliseconds(), ToEpochMs = time.AddSeconds(1).ToUnixTimeMilliseconds(),
            MaxEvents = 10,
        }, "response", CancellationToken.None);
        Assert.AreEqual(1, events.EventCount);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in store.ReadSnapshotsForReplayAsync(time.AddSeconds(-1).ToUnixTimeMilliseconds(),
                time.AddSeconds(1).ToUnixTimeMilliseconds(), 10, CancellationToken.None)) { }
        });
        Assert.AreEqual("diagnostic_replay_time_unavailable", exception.Message);
        var backup = Directory.GetFiles(System.IO.Path.Combine(temp.Path, "migration-backups"), "*.db").Single();
        var report = await SqliteRecoveryManager.VerifyAsync(backup);
        Assert.IsTrue(report.IntegrityPassed);
        Assert.AreEqual(2, report.SchemaVersion);
        await using var connection = new SqliteConnection($"Data Source={temp.Database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "PRAGMA integrity_check(1);";
        Assert.AreEqual("ok", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task FailedMigrationRollsBackTableReplacementAndLeavesVerifiedOriginalBackup()
    {
        using var temp = new TestDirectory();
        await SeedAsync(temp.Database, DateTimeOffset.UtcNow, brokenMetricTable: true);
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        Assert.AreEqual(SqliteHistoryState.DegradedReadOnly, store.Health.State);
        var report = await SqliteRecoveryManager.VerifyAsync(temp.Database);
        Assert.IsTrue(report.IntegrityPassed);
        Assert.AreEqual(2, report.SchemaVersion);
        await using var connection = new SqliteConnection($"Data Source={temp.Database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshots_raw WHERE instance_id='legacy-instance';";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='snapshots_raw_v2';";
        Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        var backup = Directory.GetFiles(System.IO.Path.Combine(temp.Path, "migration-backups"), "*.db").Single();
        Assert.IsTrue((await SqliteRecoveryManager.VerifyAsync(backup)).IntegrityPassed);
        Assert.AreEqual(2, (await SqliteRecoveryManager.VerifyAsync(backup)).SchemaVersion);
    }

    [TestMethod]
    public async Task FailedMultiVersionMigrationPreservesOriginalVersionJournalAndBytes()
    {
        using var temp = new TestDirectory();
        await SeedAsync(temp.Database, DateTimeOffset.UtcNow);
        await using (var connection = new SqliteConnection($"Data Source={temp.Database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE diagnostic_events;
                CREATE TABLE diagnostic_events(event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_ms INTEGER NOT NULL, diagnostic_id TEXT NOT NULL,
                    severity TEXT NOT NULL, payload_json TEXT NOT NULL);
                CREATE TABLE action_audit(audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_ms INTEGER NOT NULL, action_id TEXT NOT NULL,
                    outcome TEXT NOT NULL, payload_json TEXT NOT NULL);
                DELETE FROM schema_migrations;
                INSERT INTO schema_migrations VALUES(1, 'legacy');
                CREATE TABLE metrics_raw_v2(sentinel TEXT);
                INSERT INTO metrics_raw_v2 VALUES('preserve-original');
                PRAGMA user_version=1;
                PRAGMA journal_mode=DELETE;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var before = await SqliteRecoveryManager.VerifyAsync(temp.Database);
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        var after = await SqliteRecoveryManager.VerifyAsync(temp.Database);
        Assert.AreEqual(SqliteHistoryState.DegradedReadOnly, store.Health.State);
        Assert.AreEqual(1, after.SchemaVersion);
        Assert.IsTrue(after.IntegrityPassed);
        Assert.AreEqual(before.Sha256, after.Sha256);
        await using var check = new SqliteConnection($"Data Source={temp.Database};Mode=ReadOnly;Pooling=False");
        await check.OpenAsync();
        await using var query = check.CreateCommand();
        query.CommandText = "PRAGMA journal_mode;";
        Assert.AreEqual("delete", Convert.ToString(await query.ExecuteScalarAsync()));
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='diagnostic_events_v1';";
        Assert.AreEqual(0L, Convert.ToInt64(await query.ExecuteScalarAsync()));
        query.CommandText = "SELECT sentinel FROM metrics_raw_v2;";
        Assert.AreEqual("preserve-original", Convert.ToString(await query.ExecuteScalarAsync()));
        query.CommandText = "SELECT COUNT(*) FROM snapshots_raw WHERE instance_id='legacy-instance';";
        Assert.AreEqual(1L, Convert.ToInt64(await query.ExecuteScalarAsync()));
        var backup = Directory.GetFiles(System.IO.Path.Combine(temp.Path, "migration-backups"), "*.db").Single();
        var backupReport = await SqliteRecoveryManager.VerifyAsync(backup);
        Assert.AreEqual(1, backupReport.SchemaVersion);
        Assert.IsTrue(backupReport.IntegrityPassed);
    }

    [TestMethod]
    public async Task FutureSchemaIsRejectedWithoutMutatingDatabase()
    {
        using var temp = new TestDirectory();
        await SeedAsync(temp.Database, DateTimeOffset.UtcNow);
        await using (var connection = new SqliteConnection($"Data Source={temp.Database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99;";
            await command.ExecuteNonQueryAsync();
        }
        var before = await SqliteRecoveryManager.VerifyAsync(temp.Database);
        await using var store = new SqliteHistoryStore(new() { DatabasePath = temp.Database });
        await store.StartAsync(CancellationToken.None);
        var after = await SqliteRecoveryManager.VerifyAsync(temp.Database);
        Assert.AreEqual(SqliteHistoryState.DegradedReadOnly, store.Health.State);
        Assert.AreEqual(99, after.SchemaVersion);
        Assert.AreEqual(before.Sha256, after.Sha256);
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(temp.Path, "migration-backups")));
    }

    private static async Task SeedAsync(string path, DateTimeOffset time, bool brokenMetricTable = false)
    {
        // Initialize the native SQLite provider through the public store boundary.
        await using var initialize = new SqliteHistoryStore(new() { DatabasePath = path });
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);
            INSERT INTO schema_migrations VALUES(2, 'legacy');
            CREATE TABLE snapshots_raw(instance_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL, snapshot_json TEXT NOT NULL,
                PRIMARY KEY(instance_id, sequence)) WITHOUT ROWID;
            CREATE TABLE metrics_raw(instance_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL, metric_id TEXT NOT NULL, unit TEXT NOT NULL,
                value REAL NOT NULL, PRIMARY KEY(instance_id, sequence, metric_id)) WITHOUT ROWID;
            CREATE TABLE metric_rollups(bucket_seconds INTEGER NOT NULL, bucket_start_ms INTEGER NOT NULL,
                metric_id TEXT NOT NULL, unit TEXT NOT NULL, min_value REAL NOT NULL, max_value REAL NOT NULL,
                sum_value REAL NOT NULL, sample_count INTEGER NOT NULL, last_value REAL NOT NULL,
                last_sample_ms INTEGER NOT NULL, last_sequence INTEGER NOT NULL,
                PRIMARY KEY(bucket_seconds, bucket_start_ms, metric_id)) WITHOUT ROWID;
            CREATE TABLE diagnostic_events(event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_ms INTEGER NOT NULL, diagnostic_id TEXT NOT NULL UNIQUE,
                severity TEXT NOT NULL, payload_json TEXT NOT NULL, rule_id TEXT NOT NULL,
                rule_version TEXT NOT NULL, state TEXT NOT NULL, first_seen_ms INTEGER NOT NULL,
                last_seen_ms INTEGER NOT NULL);
            PRAGMA user_version=2;
            """;
        await command.ExecuteNonQueryAsync();
        var snapshot = new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent, "legacy-instance", 1,
            time, time, time, 0, new("available", "fresh"), new(3600, 3600),
            new Dictionary<string, SnapshotGroup>
            {
                [GroupIds.SystemCpu] = new("test", time, "available", "fresh", ProviderCoverage.Complete, [],
                    new System.Text.Json.Nodes.JsonObject { ["metrics"] = new System.Text.Json.Nodes.JsonObject
                    { [MetricIds.SystemCpuUtilization] = MetricJson.Value(95d, Units.Percent, SourceIds.SystemCpu) } }),
            });
        var policy = DiagnosticPolicy.Default with { HighCpu = DiagnosticPolicy.Default.HighCpu with { ActivateDebounceSeconds = 0 } };
        var current = snapshot with { ElapsedSeconds = 0, Groups = snapshot.Groups.ToDictionary(item => item.Key,
            item => item.Value with { ObservedElapsedSeconds = 0, ObservationSequence = 1 }) };
        var diagnostic = DiagnosticReplay.Replay([current], policy).Single();
        command.CommandText = """
            INSERT INTO snapshots_raw VALUES('legacy-instance', 1, $time, $snapshot);
            INSERT INTO metrics_raw VALUES('legacy-instance', 1, $time, $metric, 'percent', 95);
            INSERT INTO diagnostic_events(occurred_at_ms, diagnostic_id, severity, payload_json,
                rule_id, rule_version, state, first_seen_ms, last_seen_ms)
                VALUES($time, $id, $severity, $event, $rule, $version, $state, $time, $time);
            """;
        command.Parameters.AddWithValue("$time", time.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, ContractJson.Options));
        command.Parameters.AddWithValue("$metric", MetricIds.SystemCpuUtilization);
        command.Parameters.AddWithValue("$id", diagnostic.EventId);
        command.Parameters.AddWithValue("$severity", diagnostic.Severity);
        command.Parameters.AddWithValue("$event", JsonSerializer.Serialize(diagnostic, ContractJson.Options));
        command.Parameters.AddWithValue("$rule", diagnostic.RuleId);
        command.Parameters.AddWithValue("$version", diagnostic.RuleVersion);
        command.Parameters.AddWithValue("$state", diagnostic.State);
        await command.ExecuteNonQueryAsync();
        if (brokenMetricTable)
        {
            command.Parameters.Clear();
            command.CommandText = "ALTER TABLE metrics_raw RENAME COLUMN value TO incompatible_value;";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perf-monitor-migration-{Guid.NewGuid():N}");
        public string Database => System.IO.Path.Combine(Path, "history.db");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
