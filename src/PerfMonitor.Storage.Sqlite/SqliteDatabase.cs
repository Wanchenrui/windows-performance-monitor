using Microsoft.Data.Sqlite;

namespace PerfMonitor.Storage.Sqlite;

internal sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 1;
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
            Pooling = true,
            DefaultTimeout = checked((int)Math.Ceiling(
                options.BusyTimeout.TotalSeconds)),
        }.ToString();
        _readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
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

        await using var connection = CreateReadWriteConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(
            connection,
            writable: true,
            cancellationToken).ConfigureAwait(false);
        await VerifyIntegrityAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await ApplyMigrationsAsync(connection, cancellationToken)
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
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var versionObject = await versionCommand
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = Convert.ToInt32(
            versionObject,
            System.Globalization.CultureInfo.InvariantCulture);
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
        transaction.Commit();
    }
}
