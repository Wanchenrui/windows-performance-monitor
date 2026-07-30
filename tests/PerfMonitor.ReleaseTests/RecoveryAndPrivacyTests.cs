using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using PerfMonitor.Storage.Sqlite;
using PerfMonitor.Support;

namespace PerfMonitor.ReleaseTests;

[TestClass]
public sealed class RecoveryAndPrivacyTests
{
    [TestMethod]
    public async Task MigrationBackupCanBeVerifiedAndRestored()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);

        var backup =
            await SqliteRecoveryManager.CreateMigrationBackupAsync(
                databasePath,
                targetSchemaVersion: 2);
        var report = await SqliteRecoveryManager.VerifyAsync(
            backup.BackupPath);
        Assert.IsTrue(report.IntegrityPassed);
        Assert.AreEqual(1, report.SchemaVersion);
        Assert.AreEqual(
            report.Sha256,
            backup.Manifest.Sha256,
            ignoreCase: true);

        await SetSchemaVersionAsync(databasePath, 2);
        await SqliteRecoveryManager.RestoreAsync(
            databasePath,
            backup.ManifestPath,
            confirmDiscardNewerData: true);

        var restored = await SqliteRecoveryManager.VerifyAsync(
            databasePath);
        Assert.IsTrue(restored.IntegrityPassed);
        Assert.AreEqual(1, restored.SchemaVersion);
        await using var connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM recovery_fixture WHERE id=1;";
        Assert.AreEqual(
            "preserve-me",
            await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task RestoreRejectsTamperedBackup()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);
        var backup =
            await SqliteRecoveryManager.CreateMigrationBackupAsync(
                databasePath,
                targetSchemaVersion: 2);
        await File.AppendAllTextAsync(
            backup.BackupPath,
            "tamper",
            Encoding.UTF8);

        var exception = await Assert.ThrowsAsync<
            InvalidDataException>(
                () => SqliteRecoveryManager.RestoreAsync(
                    databasePath,
                    backup.ManifestPath,
                    confirmDiscardNewerData: true));
        Assert.AreEqual(
            "sqlite_backup_manifest_mismatch",
            exception.Message);
    }

    [TestMethod]
    public async Task MigrationFailureLeavesVersionOneAndBackup()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);
        await using (var connection = OpenReadWrite(databasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE diagnostic_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_ms INTEGER NOT NULL,
                    diagnostic_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE TABLE diagnostic_events_v1 (
                    id INTEGER PRIMARY KEY
                );
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteHistoryStore(
            new SqliteHistoryOptions
            {
                DatabasePath = databasePath,
            });
        await store.StartAsync(CancellationToken.None);

        Assert.AreEqual(
            SqliteHistoryState.DegradedReadOnly,
            store.Health.State);
        var report = await SqliteRecoveryManager.VerifyAsync(
            databasePath);
        Assert.IsTrue(report.IntegrityPassed);
        Assert.AreEqual(1, report.SchemaVersion);
        var backupDirectory = Path.Combine(
            temp.Path,
            "migration-backups");
        Assert.HasCount(
            1,
            Directory.GetFiles(
                backupDirectory,
                "*.manifest.json"));
    }

    [TestMethod]
    public async Task RestoreRejectsSpoofedSourceSchema()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);
        await SetSchemaVersionAsync(databasePath, 2);
        var backup =
            await SqliteRecoveryManager.CreateMigrationBackupAsync(
                databasePath,
                targetSchemaVersion: 3);
        var manifest = await File.ReadAllTextAsync(
            backup.ManifestPath);
        manifest = manifest.Replace(
            "\"sourceSchemaVersion\": 2",
            "\"sourceSchemaVersion\": 1",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            backup.ManifestPath,
            manifest,
            Encoding.UTF8);

        var exception = await Assert.ThrowsAsync<
            InvalidDataException>(
                () => SqliteRecoveryManager.RestoreAsync(
                    databasePath,
                    backup.ManifestPath,
                    confirmDiscardNewerData: true));
        Assert.AreEqual(
            "sqlite_backup_manifest_mismatch",
            exception.Message);
    }

    [TestMethod]
    public async Task RestoreRejectsBackupForDifferentDatabaseName()
    {
        using var temp = new TemporaryDirectory();
        var sourcePath = Path.Combine(temp.Path, "history-v1.db");
        var otherPath = Path.Combine(temp.Path, "other.db");
        await CreateVersionOneDatabaseAsync(sourcePath);
        await CreateVersionOneDatabaseAsync(otherPath);
        var backup =
            await SqliteRecoveryManager.CreateMigrationBackupAsync(
                sourcePath,
                targetSchemaVersion: 2);

        var exception = await Assert.ThrowsAsync<
            InvalidDataException>(
                () => SqliteRecoveryManager.RestoreAsync(
                    otherPath,
                    backup.ManifestPath,
                    confirmDiscardNewerData: true));
        Assert.AreEqual(
            "sqlite_backup_target_mismatch",
            exception.Message);
    }

    [TestMethod]
    public async Task RestoreRejectsActiveAgentLease()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);
        var backup =
            await SqliteRecoveryManager.CreateMigrationBackupAsync(
                databasePath,
                targetSchemaVersion: 2);
        await using var agentLease = new FileStream(
            Path.Combine(temp.Path, ".agent.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => SqliteRecoveryManager.RestoreAsync(
                    databasePath,
                    backup.ManifestPath,
                    confirmDiscardNewerData: true));
        Assert.AreEqual(
            "sqlite_restore_requires_agent_stopped",
            exception.Message);
    }

    [TestMethod]
    public async Task ExistingVersionZeroDatabaseGetsMigrationBackup()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await using (var connection = OpenReadWrite(databasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE pre_v1_fixture (
                    id INTEGER PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO pre_v1_fixture(id, value)
                VALUES (1, 'preserve-me');
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteHistoryStore(
            new SqliteHistoryOptions
            {
                DatabasePath = databasePath,
            });
        await store.StartAsync(CancellationToken.None);

        Assert.AreEqual(
            SqliteHistoryState.Healthy,
            store.Health.State);
        var manifests = Directory.GetFiles(
            Path.Combine(temp.Path, "migration-backups"),
            "*.manifest.json");
        Assert.HasCount(1, manifests);
        var manifest = await File.ReadAllTextAsync(manifests[0]);
        StringAssert.Contains(
            manifest,
            "\"sourceSchemaVersion\": 0");
    }

    [TestMethod]
    public async Task NewerSchemaIsNeverWrittenByOlderStore()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateVersionOneDatabaseAsync(databasePath);
        await SetSchemaVersionAsync(databasePath, 99);
        var before = await SqliteRecoveryManager.VerifyAsync(
            databasePath);

        await using var store = new SqliteHistoryStore(
            new SqliteHistoryOptions
            {
                DatabasePath = databasePath,
            });
        await store.StartAsync(CancellationToken.None);

        Assert.AreEqual(
            SqliteHistoryState.DegradedReadOnly,
            store.Health.State);
        var after = await SqliteRecoveryManager.VerifyAsync(
            databasePath);
        Assert.AreEqual(99, after.SchemaVersion);
        Assert.AreEqual(
            before.Sha256,
            after.Sha256,
            ignoreCase: true);
    }

    [TestMethod]
    public async Task DiagnosticExportExcludesRawSensitivePayload()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "history-v1.db");
        await CreateDiagnosticDatabaseAsync(databasePath);
        var outputPath = Path.Combine(temp.Path, "diagnostics.zip");

        await DiagnosticBundleExporter.ExportAsync(
            new DiagnosticExportOptions(
                temp.Path,
                outputPath,
                null,
                null));

        using var archive = ZipFile.OpenRead(outputPath);
        Assert.AreEqual(2, archive.Entries.Count);
        var diagnostics = await ReadEntryAsync(
            archive,
            "diagnostics.json");
        var privacy = await ReadEntryAsync(
            archive,
            "privacy-manifest.json");
        StringAssert.Contains(
            diagnostics,
            "windows.memory.global-status.v1");
        StringAssert.Contains(
            diagnostics,
            "provider_failure");
        StringAssert.Contains(
            diagnostics,
            "system.high_cpu");
        Assert.IsFalse(
            diagnostics.Contains(
                "Alice",
                StringComparison.Ordinal));
        Assert.IsFalse(
            diagnostics.Contains(
                "C:\\Users\\Alice",
                StringComparison.Ordinal));
        Assert.IsFalse(
            diagnostics.Contains(
                "S-1-5-21-111-222-333-1001",
                StringComparison.Ordinal));
        Assert.IsFalse(
            diagnostics.Contains(
                "alice@example.com",
                StringComparison.Ordinal));
        StringAssert.Contains(privacy, "allowlist");
    }

    [TestMethod]
    public async Task DiagnosticExportRejectsSensitiveAllowlistedField()
    {
        using var temp = new TemporaryDirectory();
        var manifestPath = Path.Combine(
            temp.Path,
            "build-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            """
            {
              "productVersion": "C:\\Users\\Alice",
              "files": []
            }
            """,
            Encoding.UTF8);

        var exception = await Assert.ThrowsAsync<
            InvalidDataException>(
                () => DiagnosticBundleExporter.ExportAsync(
                    new DiagnosticExportOptions(
                        temp.Path,
                        Path.Combine(temp.Path, "diagnostics.zip"),
                        manifestPath,
                        null)));
        StringAssert.StartsWith(
            exception.Message,
            "diagnostic_sensitive_pattern_detected_");
    }

    private static async Task CreateVersionOneDatabaseAsync(
        string path)
    {
        await using var connection = OpenReadWrite(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE recovery_fixture (
                id INTEGER PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO recovery_fixture(id, value)
            VALUES (1, 'preserve-me');
            PRAGMA user_version=1;
            """;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDiagnosticDatabaseAsync(
        string path)
    {
        await using var connection = OpenReadWrite(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE snapshots_raw (
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                PRIMARY KEY(instance_id, sequence)
            ) WITHOUT ROWID;
            CREATE TABLE diagnostic_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_ms INTEGER NOT NULL,
                diagnostic_id TEXT NOT NULL UNIQUE,
                severity TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                rule_id TEXT NOT NULL,
                rule_version TEXT NOT NULL,
                state TEXT NOT NULL,
                first_seen_ms INTEGER NOT NULL,
                last_seen_ms INTEGER NOT NULL
            );
            INSERT INTO snapshots_raw(
                instance_id,
                sequence,
                sample_time_ms,
                snapshot_json
            ) VALUES (
                'secret-instance',
                1,
                1767225600000,
                '{
                  "groups": {
                    "memory": {
                      "providerId": "windows.memory.global-status.v1",
                      "availability": "error",
                      "freshness": "fresh",
                      "errors": [
                        {"errorCode": "provider_failure"}
                      ],
                      "data": {
                        "path": "C:\\Users\\Alice",
                        "sid": "S-1-5-21-111-222-333-1001"
                      }
                    }
                  }
                }'
            );
            INSERT INTO diagnostic_events(
                occurred_at_ms,
                diagnostic_id,
                severity,
                payload_json,
                rule_id,
                rule_version,
                state,
                first_seen_ms,
                last_seen_ms
            ) VALUES (
                1767225600000,
                'diagnostic-1',
                'warning',
                '{"email":"alice@example.com","name":"Alice"}',
                'system.high_cpu',
                '1.0.0',
                'active',
                1767225600000,
                1767225660000
            );
            PRAGMA user_version=2;
            """;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task SetSchemaVersionAsync(
        string path,
        int version)
    {
        await using var connection = OpenReadWrite(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version={version};";
        _ = await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection OpenReadWrite(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

    private static SqliteConnection OpenReadOnly(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static async Task<string> ReadEntryAsync(
        ZipArchive archive,
        string name)
    {
        var entry = archive.GetEntry(name) ??
            throw new InvalidDataException(
                "diagnostic_entry_missing");
        await using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "perf-monitor-release-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
