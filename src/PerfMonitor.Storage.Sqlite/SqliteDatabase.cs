using Microsoft.Data.Sqlite;

namespace PerfMonitor.Storage.Sqlite;

internal sealed class SqliteDatabase
{
    public const int CurrentSchemaVersion = 3;
    private static readonly Lazy<bool> ProviderInitialization = new(
        static () =>
        {
            SQLitePCL.Batteries_V2.Init();
            return true;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly SqliteHistoryOptions _options;
    private readonly string _readWriteConnectionString;
    private readonly string _readOnlyConnectionString;

    public SqliteDatabase(SqliteHistoryOptions options)
    {
        _ = ProviderInitialization.Value;
        _options = options;
        _readWriteConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(
                options.BusyTimeout.TotalSeconds)),
        }.ToString();
        _readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = checked((int)Math.Ceiling(
                options.BusyTimeout.TotalSeconds)),
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // v1.0: compatibility and recovery preparation must happen
        // before the first writable connection. PRAGMA journal_mode=WAL
        // itself mutates the database, so opening writable first would
        // violate the old-binary/new-schema no-write guarantee.
        int? preflightSchemaVersion = null;
        if (File.Exists(_options.DatabasePath))
        {
            var preflight = await SqliteRecoveryManager.VerifyAsync(
                _options.DatabasePath,
                cancellationToken).ConfigureAwait(false);
            if (!preflight.IntegrityPassed)
            {
                throw new InvalidDataException(
                    "sqlite_integrity_check_failed");
            }

            preflightSchemaVersion = preflight.SchemaVersion;
            if (preflight.SchemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "sqlite_schema_version_too_new");
            }

            if (preflight.SchemaVersion < CurrentSchemaVersion)
            {
                _ = await SqliteRecoveryManager
                    .CreateMigrationBackupAsync(
                        _options.DatabasePath,
                        CurrentSchemaVersion,
                        cancellationToken).ConfigureAwait(false);
            }
        }

        await using var connection = CreateReadWriteConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // v1.0: do not enable WAL or issue any other persistent PRAGMA
        // until the schema seen through the writable handle matches the
        // read-only preflight. This closes the preflight/open race without
        // weakening the single-writer timing model.
        await VerifyIntegrityAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var sourceSchemaVersion = await ReadSchemaVersionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (
            preflightSchemaVersion is not null &&
            sourceSchemaVersion != preflightSchemaVersion.Value
        )
        {
            throw new InvalidDataException(
                "sqlite_schema_changed_during_initialization");
        }

        await ApplyMigrationsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var migratedSchemaVersion = await ReadSchemaVersionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (migratedSchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "sqlite_migration_version_invalid");
        }
        await VerifyIntegrityAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        // Failed upgrades retain their original schema and journal mode.
        // Enable the persistent WAL setting only after a successful migration.
        await ConfigureConnectionAsync(connection, writable: true, cancellationToken)
            .ConfigureAwait(false);
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public SqliteConnection CreateReadWriteConnection() =>
        new(_readWriteConnectionString);

    public SqliteConnection CreateReadOnlyConnection() =>
        new(_readOnlyConnectionString);

    public async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        bool writable,
        CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = checked(
            (int)Math.Ceiling(_options.BusyTimeout.TotalMilliseconds));
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA busy_timeout={timeoutMilliseconds};
            PRAGMA foreign_keys=ON;
            PRAGMA query_only={(writable ? 0 : 1)};
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!writable)
        {
            return;
        }

        await using var writePragmas = connection.CreateCommand();
        writePragmas.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            """;
        _ = await writePragmas.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(1);";
        var result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(result as string, "ok"))
        {
            throw new InvalidDataException("sqlite_integrity_check_failed");
        }
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ReadSchemaVersionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "sqlite_schema_version_too_new");
        }

        if (version == CurrentSchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (version < 1)
        {
            await ApplyVersion1Async(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            version = 1;
        }

        if (version < 2)
        {
            await ApplyVersion2Async(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        if (version < 3)
            await ApplyVersion3Async(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "PRAGMA integrity_check(1);";
        if (!StringComparer.OrdinalIgnoreCase.Equals(
            await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok"))
            throw new InvalidDataException("sqlite_integrity_check_failed");
        transaction.Commit();
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var versionObject = await versionCommand
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt32(
            versionObject,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyVersion1Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS snapshots_raw (
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                PRIMARY KEY (instance_id, sequence)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_snapshots_raw_time
                ON snapshots_raw(sample_time_ms);

            CREATE TABLE IF NOT EXISTS metrics_raw (
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL,
                metric_id TEXT NOT NULL,
                unit TEXT NOT NULL,
                value REAL NOT NULL,
                PRIMARY KEY (instance_id, sequence, metric_id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_metrics_raw_metric_time
                ON metrics_raw(metric_id, sample_time_ms);

            CREATE TABLE IF NOT EXISTS metric_rollups (
                bucket_seconds INTEGER NOT NULL,
                bucket_start_ms INTEGER NOT NULL,
                metric_id TEXT NOT NULL,
                unit TEXT NOT NULL,
                min_value REAL NOT NULL,
                max_value REAL NOT NULL,
                sum_value REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                last_value REAL NOT NULL,
                last_sample_ms INTEGER NOT NULL,
                last_sequence INTEGER NOT NULL,
                PRIMARY KEY (
                    bucket_seconds,
                    bucket_start_ms,
                    metric_id
                )
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_metric_rollups_metric_time
                ON metric_rollups(
                    bucket_seconds,
                    metric_id,
                    bucket_start_ms
                );

            CREATE TABLE IF NOT EXISTS diagnostic_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_ms INTEGER NOT NULL,
                diagnostic_id TEXT NOT NULL,
                severity TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS action_audit (
                audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_ms INTEGER NOT NULL,
                action_id TEXT NOT NULL,
                outcome TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_migrations(
                version,
                applied_at_utc
            ) VALUES (
                1,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            );
            PRAGMA user_version=1;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ApplyVersion2Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE diagnostic_events
                RENAME TO diagnostic_events_v1;

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
            CREATE INDEX ix_diagnostic_events_time
                ON diagnostic_events(last_seen_ms);
            CREATE INDEX ix_diagnostic_events_rule_time
                ON diagnostic_events(rule_id, last_seen_ms);
            CREATE INDEX ix_diagnostic_events_state_time
                ON diagnostic_events(state, last_seen_ms);

            INSERT OR IGNORE INTO schema_migrations(
                version,
                applied_at_utc
            ) VALUES (
                2,
                strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            );
            PRAGMA user_version=2;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ApplyVersion3Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        // Both table replacements and version publication commit together.
        // Existing raw data is preserved; legacy replay is explicitly rejected.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE snapshots_raw RENAME TO snapshots_raw_v2;
            CREATE TABLE snapshots_raw (
                persistence_order INTEGER PRIMARY KEY AUTOINCREMENT,
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                delivery_sequence INTEGER,
                projection_version INTEGER NOT NULL DEFAULT 0,
                UNIQUE(instance_id, delivery_sequence)
            );
            INSERT INTO snapshots_raw(instance_id, sequence, sample_time_ms, snapshot_json)
                SELECT instance_id, sequence, sample_time_ms, snapshot_json
                FROM snapshots_raw_v2 ORDER BY sample_time_ms, instance_id, sequence;
            DROP TABLE snapshots_raw_v2;
            CREATE INDEX ix_snapshots_raw_time ON snapshots_raw(sample_time_ms);

            ALTER TABLE metrics_raw RENAME TO metrics_raw_v2;
            CREATE TABLE metrics_raw (
                instance_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                sample_time_ms INTEGER NOT NULL,
                group_id TEXT NOT NULL,
                metric_id TEXT NOT NULL,
                unit TEXT NOT NULL,
                source_id TEXT NOT NULL,
                value REAL,
                availability TEXT NOT NULL,
                freshness TEXT NOT NULL,
                coverage_json TEXT NOT NULL,
                observed_at_utc TEXT,
                observed_elapsed_seconds REAL,
                observation_sequence INTEGER,
                observation_key TEXT NOT NULL,
                PRIMARY KEY(instance_id, group_id, metric_id, observation_key)
            ) WITHOUT ROWID;
            INSERT INTO metrics_raw(instance_id, sequence, sample_time_ms, group_id,
                metric_id, unit, source_id, value, availability, freshness, coverage_json,
                observed_at_utc, observation_key)
                SELECT instance_id, sequence, sample_time_ms, 'legacy', metric_id, unit,
                    'legacy', value, 'unknown', 'unknown', '{"status":"unknown"}',
                    NULL, 'legacy:' || sequence FROM metrics_raw_v2;
            DROP TABLE metrics_raw_v2;
            CREATE INDEX ix_metrics_raw_metric_time ON metrics_raw(metric_id, sample_time_ms);
            INSERT INTO schema_migrations(version, applied_at_utc)
                VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            PRAGMA user_version=3;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
