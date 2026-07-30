using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfMonitor.Actions;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker;

public sealed record BrokerAuditRecord(
    string CallerSid,
    string IdempotencyKey,
    string ActionId,
    string RequestSha256,
    int ClientPid,
    string ClientImageSha256,
    string BrokerPolicyVersion,
    string AgentPolicyVersion,
    string ActionType,
    bool DryRun,
    string Status,
    string? ErrorCode,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ActionStateContract? Before,
    ActionStateContract? After);

public sealed class BrokerAuditStore : IActionAuditStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxStateJsonBytes = 16 * 1024;
    private const int MaxRequestJsonBytes = 16 * 1024;
    private static readonly Lazy<bool> ProviderInitialization = new(
        static () =>
        {
            SQLitePCL.Batteries_V2.Init();
            return true;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _initialized;
    private int _disposed;

    public BrokerAuditStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _ = ProviderInitialization.Value;
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(
            ref _initialized,
            1,
            0) != 0)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await ApplySchemaAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _initialized, 0);
            throw;
        }
    }

    public async ValueTask RecoverPendingAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE actions
                SET status = $status,
                    error_code = $error,
                    completed_at_utc = $completed
                WHERE status = 'pending';
                """;
            command.Parameters.AddWithValue(
                "$status",
                ActionStatuses.Indeterminate);
            command.Parameters.AddWithValue(
                "$error",
                ActionErrorCodes.IdempotencyIndeterminate);
            command.Parameters.AddWithValue(
                "$completed",
                FormatUtc(recoveredAtUtc));
            _ = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ActionAuditReservation> ReserveAsync(
        ActionAuditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        var requestJson = JsonSerializer.Serialize(
            request.Request,
            BrokerJson.Options);
        EnsureJsonBound(requestJson, MaxRequestJsonBytes);

        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var existing = await ReadCoreAsync(
                connection,
                transaction,
                request.CallerSid,
                request.Request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                transaction.Commit();
                if (!StringComparer.Ordinal.Equals(
                    existing.RequestSha256,
                    request.RequestSha256))
                {
                    return new ActionAuditReservation(
                        ActionAuditReservationKind.Conflict,
                        existing.ActionId,
                        null);
                }

                var storedResult = ToResult(existing);
                var kind = existing.Status is
                    "pending" or ActionStatuses.Indeterminate
                    ? ActionAuditReservationKind.Indeterminate
                    : ActionAuditReservationKind.Replay;
                return new ActionAuditReservation(
                    kind,
                    existing.ActionId,
                    storedResult);
            }

            var actionId = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO actions (
                    caller_sid,
                    idempotency_key,
                    action_id,
                    request_sha256,
                    client_pid,
                    client_image_sha256,
                    broker_policy_version,
                    agent_policy_version,
                    action_type,
                    dry_run,
                    status,
                    error_code,
                    received_at_utc,
                    started_at_utc,
                    completed_at_utc,
                    request_json,
                    before_json,
                    after_json
                ) VALUES (
                    $caller,
                    $key,
                    $action,
                    $hash,
                    $pid,
                    $image,
                    $broker_policy,
                    $agent_policy,
                    $action_type,
                    $dry_run,
                    'pending',
                    NULL,
                    $received,
                    NULL,
                    NULL,
                    $request_json,
                    NULL,
                    NULL
                );
                """;
            insert.Parameters.AddWithValue(
                "$caller",
                request.CallerSid);
            insert.Parameters.AddWithValue(
                "$key",
                request.Request.IdempotencyKey);
            insert.Parameters.AddWithValue("$action", actionId);
            insert.Parameters.AddWithValue(
                "$hash",
                request.RequestSha256);
            insert.Parameters.AddWithValue(
                "$pid",
                request.ClientPid);
            insert.Parameters.AddWithValue(
                "$image",
                request.ClientImageSha256);
            insert.Parameters.AddWithValue(
                "$broker_policy",
                request.BrokerPolicyVersion);
            insert.Parameters.AddWithValue(
                "$agent_policy",
                request.Request.AgentPolicyVersion);
            insert.Parameters.AddWithValue(
                "$action_type",
                request.Request.Action.ActionType);
            insert.Parameters.AddWithValue(
                "$dry_run",
                request.Request.DryRun ? 1 : 0);
            insert.Parameters.AddWithValue(
                "$received",
                FormatUtc(request.ReceivedAtUtc));
            insert.Parameters.AddWithValue(
                "$request_json",
                requestJson);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return new ActionAuditReservation(
                ActionAuditReservationKind.New,
                actionId,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CompleteAsync(
        string callerSid,
        string idempotencyKey,
        ActionResultContract result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(result);
        EnsureInitialized();
        if (!ActionStatuses.Terminal.Contains(
            result.Status,
            StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "broker_audit_status_invalid");
        }

        var beforeJson = SerializeState(result.Before);
        var afterJson = SerializeState(result.After);
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE actions
                SET status = $status,
                    error_code = $error,
                    started_at_utc = $started,
                    completed_at_utc = $completed,
                    before_json = $before,
                    after_json = $after
                WHERE caller_sid = $caller
                  AND idempotency_key = $key
                  AND action_id = $action
                  AND status = 'pending';
                """;
            command.Parameters.AddWithValue(
                "$status",
                result.Status);
            command.Parameters.AddWithValue(
                "$error",
                (object?)result.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$started",
                result.StartedAtUtc is null
                    ? DBNull.Value
                    : FormatUtc(result.StartedAtUtc.Value));
            command.Parameters.AddWithValue(
                "$completed",
                FormatUtc(result.CompletedAtUtc));
            command.Parameters.AddWithValue(
                "$before",
                (object?)beforeJson ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$after",
                (object?)afterJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$caller", callerSid);
            command.Parameters.AddWithValue(
                "$key",
                idempotencyKey);
            command.Parameters.AddWithValue(
                "$action",
                result.ActionId);
            var updated = await command.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                throw new InvalidDataException(
                    "broker_audit_completion_conflict");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask MarkIndeterminateAsync(
        string callerSid,
        string idempotencyKey,
        DateTimeOffset completedAtUtc,
        string errorCode,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE actions
                SET status = $status,
                    error_code = $error,
                    completed_at_utc = $completed
                WHERE caller_sid = $caller
                  AND idempotency_key = $key
                  AND status = 'pending';
                """;
            command.Parameters.AddWithValue(
                "$status",
                ActionStatuses.Indeterminate);
            command.Parameters.AddWithValue("$error", errorCode);
            command.Parameters.AddWithValue(
                "$completed",
                FormatUtc(completedAtUtc));
            command.Parameters.AddWithValue("$caller", callerSid);
            command.Parameters.AddWithValue(
                "$key",
                idempotencyKey);
            _ = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrokerAuditRecord?> ReadAsync(
        string callerSid,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ConfigureAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            return await ReadCoreAsync(
                connection,
                transaction: null,
                callerSid,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection() =>
        new(_connectionString);

    private static async Task ConfigureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout=1000;
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(1);";
        var result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
            result as string,
            "ok"))
        {
            throw new InvalidDataException(
                "broker_audit_integrity_failure");
        }
    }

    private static async Task ApplySchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var versionValue = await versionCommand.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        var version = Convert.ToInt32(
            versionValue,
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "broker_audit_schema_too_new");
        }

        if (version == CurrentSchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE actions (
                caller_sid TEXT NOT NULL
                    CHECK (length(caller_sid) BETWEEN 4 AND 184),
                idempotency_key TEXT NOT NULL
                    CHECK (length(idempotency_key) BETWEEN 1 AND 128),
                action_id TEXT NOT NULL UNIQUE
                    CHECK (length(action_id) = 32),
                request_sha256 TEXT NOT NULL
                    CHECK (length(request_sha256) = 64),
                client_pid INTEGER NOT NULL
                    CHECK (client_pid BETWEEN 1 AND 2147483647),
                client_image_sha256 TEXT NOT NULL
                    CHECK (length(client_image_sha256) = 64),
                broker_policy_version TEXT NOT NULL
                    CHECK (length(broker_policy_version)
                        BETWEEN 1 AND 64),
                agent_policy_version TEXT NOT NULL
                    CHECK (length(agent_policy_version)
                        BETWEEN 1 AND 64),
                action_type TEXT NOT NULL CHECK (action_type IN (
                    'set_process_priority',
                    'terminate_process',
                    'start_approved_diagnostic',
                    'apply_approved_power_profile'
                )),
                dry_run INTEGER NOT NULL CHECK (dry_run IN (0, 1)),
                status TEXT NOT NULL CHECK (status IN (
                    'pending',
                    'succeeded',
                    'dry_run',
                    'denied',
                    'rejected',
                    'failed',
                    'indeterminate',
                    'idempotency_conflict'
                )),
                error_code TEXT NULL
                    CHECK (error_code IS NULL OR
                        length(error_code) BETWEEN 1 AND 64),
                received_at_utc TEXT NOT NULL,
                started_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                request_json TEXT NOT NULL
                    CHECK (length(CAST(request_json AS BLOB))
                        BETWEEN 2 AND 16384),
                before_json TEXT NULL
                    CHECK (before_json IS NULL OR
                        length(CAST(before_json AS BLOB)) <= 16384),
                after_json TEXT NULL
                    CHECK (after_json IS NULL OR
                        length(CAST(after_json AS BLOB)) <= 16384),
                PRIMARY KEY (caller_sid, idempotency_key)
            ) WITHOUT ROWID;
            CREATE INDEX ix_actions_completed
                ON actions(completed_at_utc);
            CREATE INDEX ix_actions_type_completed
                ON actions(action_type, completed_at_utc);
            PRAGMA user_version=1;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private static async ValueTask<BrokerAuditRecord?> ReadCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string callerSid,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                caller_sid,
                idempotency_key,
                action_id,
                request_sha256,
                client_pid,
                client_image_sha256,
                broker_policy_version,
                agent_policy_version,
                action_type,
                dry_run,
                status,
                error_code,
                received_at_utc,
                started_at_utc,
                completed_at_utc,
                before_json,
                after_json
            FROM actions
            WHERE caller_sid = $caller
              AND idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$caller", callerSid);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            return null;
        }

        return new BrokerAuditRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9) != 0,
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            ParseUtc(reader.GetString(12)),
            reader.IsDBNull(13)
                ? null
                : ParseUtc(reader.GetString(13)),
            reader.IsDBNull(14)
                ? null
                : ParseUtc(reader.GetString(14)),
            reader.IsDBNull(15)
                ? null
                : DeserializeState(reader.GetString(15)),
            reader.IsDBNull(16)
                ? null
                : DeserializeState(reader.GetString(16)));
    }

    private static ActionResultContract? ToResult(
        BrokerAuditRecord record)
    {
        if (record.Status == "pending")
        {
            return null;
        }

        return new ActionResultContract
        {
            ActionId = record.ActionId,
            IdempotencyKey = record.IdempotencyKey,
            Status = record.Status,
            ErrorCode = record.ErrorCode,
            ReceivedAtUtc = record.ReceivedAtUtc,
            StartedAtUtc = record.StartedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc ??
                record.ReceivedAtUtc,
            Before = record.Before,
            After = record.After,
        };
    }

    private static string? SerializeState(
        ActionStateContract? state)
    {
        if (state is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(
            state,
            BrokerJson.Options);
        EnsureJsonBound(json, MaxStateJsonBytes);
        return json;
    }

    private static ActionStateContract DeserializeState(
        string json) =>
        JsonSerializer.Deserialize<ActionStateContract>(
            json,
            BrokerJson.Options) ?? throw new InvalidDataException(
            "broker_audit_state_invalid");

    private static void EnsureJsonBound(
        string json,
        int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) >
            maxBytes)
        {
            throw new InvalidDataException(
                "broker_audit_payload_too_large");
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal);
        if (parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "broker_audit_time_invalid");
        }

        return parsed;
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException(
                "Broker audit store is not initialized.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
