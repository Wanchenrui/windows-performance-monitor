using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Storage.Sqlite;

public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class SqliteHistoryStore :
    ISnapshotConsumer,
    IHistoryReader,
    IDiagnosticEventSink,
    IDiagnosticEventReader,
    ISnapshotReplayReader,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private readonly SqliteHistoryOptions _options;
    private readonly SqliteDatabase _database;
    private readonly Channel<StorageWorkItem> _channel;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _writerTask;
    private int _started;
    private int _state = (int)SqliteHistoryState.Created;
    private long _acceptedSamples;
    private long _persistedSamples;
    private long _droppedSamples;
    private long _acceptedDiagnosticEvents;
    private long _persistedDiagnosticEvents;
    private long _droppedDiagnosticEvents;
    private long _writeFailures;
    private long _deliveryAttempts;
    private long _queueBytes;
    private long _peakQueueBytes;
    private long _droppedPayloadTooLarge;
    private long _droppedQueueByteLimit;
    private string? _lastErrorCode;
    private DateTimeOffset _nextRetentionUtc = DateTimeOffset.MinValue;

    public SqliteHistoryStore(SqliteHistoryOptions options)
    {
        _options = options.Validate();
        _database = new SqliteDatabase(_options);
        _channel = Channel.CreateBounded<StorageWorkItem>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public SqliteHistoryHealth Health => new(
        (SqliteHistoryState)Volatile.Read(ref _state),
        Interlocked.Read(ref _acceptedSamples),
        Interlocked.Read(ref _persistedSamples),
        Interlocked.Read(ref _droppedSamples),
        Interlocked.Read(ref _acceptedDiagnosticEvents),
        Interlocked.Read(ref _persistedDiagnosticEvents),
        Interlocked.Read(ref _droppedDiagnosticEvents),
        Interlocked.Read(ref _writeFailures),
        Volatile.Read(ref _lastErrorCode))
    {
        QueueBytes = Interlocked.Read(ref _queueBytes),
        PeakQueueBytes = Interlocked.Read(ref _peakQueueBytes),
        DroppedPayloadTooLarge = Interlocked.Read(ref _droppedPayloadTooLarge),
        DroppedQueueByteLimit = Interlocked.Read(ref _droppedQueueByteLimit),
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        try
        {
            await _database.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _state, (int)SqliteHistoryState.Healthy);
            _writerTask = Task.Run(
                () => RunWriterAsync(_stopping.Token),
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is SqliteException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            Degrade("sqlite_initialization_failure");
        }
    }

    public bool TryPublish(AgentSnapshot snapshot)
    {
        if (snapshot.DeliverySequence is null)
            snapshot = snapshot with { DeliverySequence = Interlocked.Increment(ref _deliveryAttempts) };
        if ((SqliteHistoryState)Volatile.Read(ref _state) != SqliteHistoryState.Healthy)
        {
            Interlocked.Increment(ref _droppedSamples);
            return false;
        }
        StorageWorkItem item;
        try
        {
            var projection = ReplaySnapshotProjection.Create(snapshot, _options.DiagnosticPolicy);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(projection, SnapshotJsonOptions);
            item = new(projection, null, System.Text.Encoding.UTF8.GetString(bytes), bytes.Length);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            Volatile.Write(ref _lastErrorCode, "sqlite_projection_invalid");
            Interlocked.Increment(ref _droppedSamples);
            return false;
        }
        if (!TryEnqueue(item, _options.MaxSnapshotBytes))
        {
            Interlocked.Increment(ref _droppedSamples);
            return false;
        }
        Interlocked.Increment(ref _acceptedSamples);
        return true;
    }

    public bool TryPublishDiagnostic(
        DiagnosticEventContract diagnosticEvent)
    {
        if ((SqliteHistoryState)Volatile.Read(ref _state) != SqliteHistoryState.Healthy)
        {
            Interlocked.Increment(ref _droppedDiagnosticEvents);
            return false;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(diagnosticEvent, SnapshotJsonOptions);
        if (!TryEnqueue(new(null, diagnosticEvent, System.Text.Encoding.UTF8.GetString(bytes), bytes.Length),
            _options.MaxDiagnosticEventBytes))
        {
            Interlocked.Increment(ref _droppedDiagnosticEvents);
            return false;
        }
        Interlocked.Increment(ref _acceptedDiagnosticEvents);
        return true;
    }

    private bool TryEnqueue(StorageWorkItem item, int payloadLimit)
    {
        if (item.Bytes > payloadLimit)
        {
            Interlocked.Increment(ref _droppedPayloadTooLarge);
            Volatile.Write(ref _lastErrorCode, "sqlite_payload_byte_limit");
            return false;
        }
        long reserved;
        while (true)
        {
            var queued = Interlocked.Read(ref _queueBytes);
            if (queued + item.Bytes > _options.MaxQueueBytes)
            {
                Interlocked.Increment(ref _droppedQueueByteLimit);
                Volatile.Write(ref _lastErrorCode, "sqlite_queue_byte_limit");
                return false;
            }
            reserved = queued + item.Bytes;
            if (Interlocked.CompareExchange(ref _queueBytes, reserved, queued) == queued) break;
        }
        long peak;
        do { peak = Interlocked.Read(ref _peakQueueBytes); }
        while (reserved > peak && Interlocked.CompareExchange(ref _peakQueueBytes, reserved, peak) != peak);
        if (_channel.Writer.TryWrite(item)) return true;
        Interlocked.Add(ref _queueBytes, -item.Bytes);
        return false;
    }

    public async Task WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var acceptedTarget = Interlocked.Read(ref _acceptedSamples);
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (Interlocked.Read(ref _persistedSamples) <
            acceptedTarget)
        {
            if ((SqliteHistoryState)Volatile.Read(ref _state) !=
                SqliteHistoryState.Healthy)
            {
                throw new StorageUnavailableException(
                    Volatile.Read(ref _lastErrorCode) ??
                    "sqlite_history_unavailable");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask<HistoryContract> QueryAsync(
        HistoryQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken)
    {
        var state = (SqliteHistoryState)Volatile.Read(ref _state);
        if (state is SqliteHistoryState.Created or
            SqliteHistoryState.Stopped ||
            !File.Exists(_options.DatabasePath))
        {
            throw new StorageUnavailableException(
                "sqlite_history_unavailable");
        }

        try
        {
            await using var connection =
                _database.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await _database.ConfigureConnectionAsync(
                connection,
                writable: false,
                cancellationToken).ConfigureAwait(false);
            return await SqliteHistoryQuery.ExecuteAsync(
                connection,
                query,
                responseInstanceId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StorageUnavailableException(
                $"sqlite_query_failure_{exception.SqliteErrorCode}");
        }
    }

    public async ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken)
    {
        var state = (SqliteHistoryState)Volatile.Read(ref _state);
        if (state is SqliteHistoryState.Created or
            SqliteHistoryState.Stopped ||
            !File.Exists(_options.DatabasePath))
        {
            throw new StorageUnavailableException(
                "sqlite_diagnostics_unavailable");
        }

        try
        {
            await using var connection =
                _database.CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await _database.ConfigureConnectionAsync(
                connection,
                writable: false,
                cancellationToken).ConfigureAwait(false);
            return await SqliteDiagnosticQuery.ExecuteAsync(
                connection,
                query,
                responseInstanceId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SqliteException or
            JsonException or
            InvalidDataException)
        {
            throw new StorageUnavailableException(
                "sqlite_diagnostics_query_failure");
        }
    }

    public async Task WaitForDiagnosticsIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var acceptedTarget =
            Interlocked.Read(ref _acceptedDiagnosticEvents);
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (Interlocked.Read(ref _persistedDiagnosticEvents) <
            acceptedTarget)
        {
            if ((SqliteHistoryState)Volatile.Read(ref _state) !=
                SqliteHistoryState.Healthy)
            {
                throw new StorageUnavailableException(
                    Volatile.Read(ref _lastErrorCode) ??
                    "sqlite_diagnostics_unavailable");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<AgentSnapshot>
        ReadSnapshotsForReplayAsync(
            long fromEpochMs,
            long toEpochMs,
            int maxSnapshots,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        if (fromEpochMs > toEpochMs ||
            checked(toEpochMs - fromEpochMs + 1) >
                DiagnosticReplayLimits.MaxRange.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromEpochMs),
                "diagnostic_replay_range_invalid");
        }

        if (maxSnapshots <= 0 ||
            maxSnapshots > DiagnosticReplayLimits.MaxSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSnapshots));
        }

        if (!File.Exists(_options.DatabasePath))
        {
            throw new StorageUnavailableException(
                "sqlite_replay_unavailable");
        }

        await using var connection =
            _database.CreateReadOnlyConnection();
        await connection.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await _database.ConfigureConnectionAsync(
            connection,
            writable: false,
            cancellationToken).ConfigureAwait(false);
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) < 3)
                throw new InvalidDataException("diagnostic_replay_time_unavailable");
        }
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM snapshots_raw
                WHERE sample_time_ms BETWEEN $from_ms AND $to_ms;
                """;
            countCommand.Parameters.AddWithValue(
                "$from_ms",
                fromEpochMs);
            countCommand.Parameters.AddWithValue("$to_ms", toEpochMs);
            var count = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(
                    cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (count > maxSnapshots)
            {
                throw new InvalidDataException(
                    "diagnostic_replay_snapshot_limit");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_json
            FROM snapshots_raw
            WHERE sample_time_ms BETWEEN $from_ms AND $to_ms
            ORDER BY persistence_order;
            """;
        command.Parameters.AddWithValue("$from_ms", fromEpochMs);
        command.Parameters.AddWithValue("$to_ms", toEpochMs);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        var returnedSnapshots = 0;
        while (await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            // COUNT is only an early rejection; the writer may commit before SELECT.
            if (++returnedSnapshots > maxSnapshots)
                throw new InvalidDataException("diagnostic_replay_snapshot_limit");
            var snapshot = JsonSerializer.Deserialize<AgentSnapshot>(
                    reader.GetString(0),
                    SnapshotJsonOptions) ??
                throw new InvalidDataException(
                    "diagnostic_replay_snapshot_invalid");
            if (snapshot.ElapsedSeconds is null)
                throw new InvalidDataException("diagnostic_replay_time_unavailable");
            yield return snapshot;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            try
            {
                await _writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Cancel();
        _stopping.Dispose();
        Volatile.Write(ref _state, (int)SqliteHistoryState.Stopped);
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                _database.CreateReadWriteConnection();
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await _database.ConfigureConnectionAsync(
                connection,
                writable: true,
                cancellationToken).ConfigureAwait(false);

            var batch = new List<StorageWorkItem>(_options.BatchSize);
            while (await _channel.Reader
                .WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                batch.Clear();
                long batchBytes = 0;
                while (batch.Count < _options.BatchSize &&
                    _channel.Reader.TryPeek(out var next) &&
                    batchBytes + next.Bytes <= _options.MaxBatchBytes &&
                    _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                    batchBytes += item.Bytes;
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                PersistedBatch persisted;
                try
                {
                    persisted = await WriteBatchAsync(connection, batch, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    foreach (var failed in batch)
                    {
                        if (failed.DiagnosticEvent is null) Interlocked.Increment(ref _droppedSamples);
                        else Interlocked.Increment(ref _droppedDiagnosticEvents);
                    }
                    throw;
                }
                finally
                {
                    Interlocked.Add(ref _queueBytes, -batchBytes);
                }
                Interlocked.Add(
                    ref _persistedSamples,
                    persisted.Snapshots);
                Interlocked.Add(
                    ref _persistedDiagnosticEvents,
                    persisted.DiagnosticEvents);

                var now = DateTimeOffset.UtcNow;
                if (now >= _nextRetentionUtc)
                {
                    await ApplyRetentionAsync(
                        connection,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    _nextRetentionUtc = now.AddHours(1);
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is SqliteException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            Interlocked.Increment(ref _writeFailures);
            Degrade("sqlite_write_failure");
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var dropped))
            {
                Interlocked.Add(ref _queueBytes, -dropped.Bytes);
                if (dropped.DiagnosticEvent is null)
                {
                    Interlocked.Increment(ref _droppedSamples);
                }
                else
                {
                    Interlocked.Increment(
                        ref _droppedDiagnosticEvents);
                }
            }
        }
    }

    private static async Task<PersistedBatch> WriteBatchAsync(
        SqliteConnection connection,
        IReadOnlyList<StorageWorkItem> items,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var persistedSnapshots = 0;
        var persistedDiagnostics = 0;
        foreach (var item in items)
        {
            if (item.DiagnosticEvent is { } diagnosticEvent)
            {
                await InsertDiagnosticEventAsync(
                    connection,
                    transaction,
                    diagnosticEvent,
                    item.Json,
                    cancellationToken).ConfigureAwait(false);
                persistedDiagnostics++;
                continue;
            }

            var snapshot = item.Snapshot ??
                throw new InvalidDataException(
                    "sqlite_work_item_invalid");
            if (snapshot.Sequence <= 0 ||
                snapshot.CompletedAtUtc is null)
            {
                persistedSnapshots++;
                continue;
            }

            var sampleTimeMs =
                snapshot.CompletedAtUtc.Value.ToUnixTimeMilliseconds();
            await InsertSnapshotAsync(
                connection,
                transaction,
                snapshot,
                sampleTimeMs,
                item.Json,
                cancellationToken).ConfigureAwait(false);

            foreach (var metric in SnapshotMetricExtractor.Extract(snapshot))
            {
                var observedTimeMs = metric.Group.ObservedAtUtc!.Value.ToUnixTimeMilliseconds();
                var inserted = await InsertRawMetricAsync(
                    connection,
                    transaction,
                    snapshot,
                    observedTimeMs,
                    metric,
                    cancellationToken).ConfigureAwait(false);
                if (!inserted || metric.Value is null)
                {
                    continue;
                }

                await UpsertRollupAsync(
                    connection,
                    transaction,
                    snapshot.Sequence,
                    observedTimeMs,
                    metric,
                    bucketSeconds: 60,
                    cancellationToken).ConfigureAwait(false);
                await UpsertRollupAsync(
                    connection,
                    transaction,
                    snapshot.Sequence,
                    observedTimeMs,
                    metric,
                    bucketSeconds: 3_600,
                    cancellationToken).ConfigureAwait(false);
            }

            persistedSnapshots++;
        }

        transaction.Commit();
        return new PersistedBatch(
            persistedSnapshots,
            persistedDiagnostics);
    }

    private static async Task InsertDiagnosticEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiagnosticEventContract diagnosticEvent,
        string json,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO diagnostic_events(
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
                $occurred_at_ms,
                $diagnostic_id,
                $severity,
                $payload_json,
                $rule_id,
                $rule_version,
                $state,
                $first_seen_ms,
                $last_seen_ms
            );
            """;
        command.Parameters.AddWithValue(
            "$occurred_at_ms",
            diagnosticEvent.LastSeenUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$diagnostic_id",
            diagnosticEvent.EventId);
        command.Parameters.AddWithValue(
            "$severity",
            diagnosticEvent.Severity);
        command.Parameters.AddWithValue(
            "$payload_json",
            json);
        command.Parameters.AddWithValue(
            "$rule_id",
            diagnosticEvent.RuleId);
        command.Parameters.AddWithValue(
            "$rule_version",
            diagnosticEvent.RuleVersion);
        command.Parameters.AddWithValue(
            "$state",
            diagnosticEvent.State);
        command.Parameters.AddWithValue(
            "$first_seen_ms",
            diagnosticEvent.FirstSeenUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$last_seen_ms",
            diagnosticEvent.LastSeenUtc.ToUnixTimeMilliseconds());
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSnapshot snapshot,
        long sampleTimeMs,
        string json,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO snapshots_raw(
                instance_id,
                sequence,
                sample_time_ms,
                snapshot_json,
                delivery_sequence,
                projection_version
            ) VALUES (
                $instance_id,
                $sequence,
                $sample_time_ms,
                $snapshot_json,
                $delivery_sequence,
                1
            );
            """;
        command.Parameters.AddWithValue(
            "$instance_id",
            snapshot.InstanceId);
        command.Parameters.AddWithValue("$sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("$sample_time_ms", sampleTimeMs);
        command.Parameters.AddWithValue("$delivery_sequence", snapshot.DeliverySequence!.Value);
        command.Parameters.AddWithValue(
            "$snapshot_json",
            json);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> InsertRawMetricAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSnapshot snapshot,
        long sampleTimeMs,
        PersistedMetric metric,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO metrics_raw(
                instance_id,
                sequence,
                sample_time_ms,
                metric_id,
                unit,
                value,
                group_id,
                source_id,
                availability,
                freshness,
                coverage_json,
                observed_at_utc,
                observed_elapsed_seconds,
                observation_sequence,
                observation_key
            ) VALUES (
                $instance_id,
                $sequence,
                $sample_time_ms,
                $metric_id,
                $unit,
                $value,
                $group_id,
                $source_id,
                $availability,
                $freshness,
                $coverage_json,
                $observed_at_utc,
                $observed_elapsed_seconds,
                $observation_sequence,
                $observation_key
            );
            """;
        command.Parameters.AddWithValue(
            "$instance_id",
            snapshot.InstanceId);
        command.Parameters.AddWithValue("$sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("$sample_time_ms", sampleTimeMs);
        command.Parameters.AddWithValue("$metric_id", metric.MetricId);
        command.Parameters.AddWithValue("$unit", metric.Unit);
        command.Parameters.AddWithValue("$value", (object?)metric.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$group_id", metric.GroupId);
        command.Parameters.AddWithValue("$source_id", metric.SourceId);
        command.Parameters.AddWithValue("$availability", metric.Group.Availability);
        command.Parameters.AddWithValue("$freshness", metric.Group.Freshness);
        command.Parameters.AddWithValue("$coverage_json", JsonSerializer.Serialize(metric.Group.Coverage, SnapshotJsonOptions));
        command.Parameters.AddWithValue("$observed_at_utc", metric.Group.ObservedAtUtc!.Value.ToString("O"));
        command.Parameters.AddWithValue("$observed_elapsed_seconds", (object?)metric.Group.ObservedElapsedSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$observation_sequence", (object?)metric.Group.ObservationSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$observation_key", metric.ObservationKey);
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static async Task UpsertRollupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sequence,
        long sampleTimeMs,
        PersistedMetric metric,
        int bucketSeconds,
        CancellationToken cancellationToken)
    {
        var bucketMilliseconds = checked(bucketSeconds * 1_000L);
        var bucketStartMs =
            sampleTimeMs - sampleTimeMs % bucketMilliseconds;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO metric_rollups(
                bucket_seconds,
                bucket_start_ms,
                metric_id,
                unit,
                min_value,
                max_value,
                sum_value,
                sample_count,
                last_value,
                last_sample_ms,
                last_sequence
            ) VALUES (
                $bucket_seconds,
                $bucket_start_ms,
                $metric_id,
                $unit,
                $value,
                $value,
                $value,
                1,
                $value,
                $sample_time_ms,
                $sequence
            )
            ON CONFLICT(
                bucket_seconds,
                bucket_start_ms,
                metric_id
            ) DO UPDATE SET
                min_value = MIN(min_value, excluded.min_value),
                max_value = MAX(max_value, excluded.max_value),
                sum_value = sum_value + excluded.sum_value,
                sample_count = sample_count + excluded.sample_count,
                last_value = CASE
                    WHEN excluded.last_sample_ms > last_sample_ms OR
                         (
                            excluded.last_sample_ms = last_sample_ms AND
                            excluded.last_sequence >= last_sequence
                         )
                    THEN excluded.last_value
                    ELSE last_value
                END,
                last_sample_ms = MAX(
                    last_sample_ms,
                    excluded.last_sample_ms
                ),
                last_sequence = CASE
                    WHEN excluded.last_sample_ms > last_sample_ms
                    THEN excluded.last_sequence
                    WHEN excluded.last_sample_ms = last_sample_ms
                    THEN MAX(last_sequence, excluded.last_sequence)
                    ELSE last_sequence
                END;
            """;
        command.Parameters.AddWithValue(
            "$bucket_seconds",
            bucketSeconds);
        command.Parameters.AddWithValue(
            "$bucket_start_ms",
            bucketStartMs);
        command.Parameters.AddWithValue("$metric_id", metric.MetricId);
        command.Parameters.AddWithValue("$unit", metric.Unit);
        command.Parameters.AddWithValue("$value", metric.Value);
        command.Parameters.AddWithValue("$sample_time_ms", sampleTimeMs);
        command.Parameters.AddWithValue("$sequence", sequence);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyRetentionAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rawCutoff = now.Subtract(_options.RawRetention)
            .ToUnixTimeMilliseconds();
        var minuteCutoff = now.Subtract(_options.MinuteRetention)
            .ToUnixTimeMilliseconds();
        var hourCutoff = now.Subtract(_options.HourRetention)
            .ToUnixTimeMilliseconds();
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM metrics_raw
            WHERE sample_time_ms < $raw_cutoff;
            DELETE FROM snapshots_raw
            WHERE sample_time_ms < $raw_cutoff;
            DELETE FROM metric_rollups
            WHERE bucket_seconds = 60
              AND bucket_start_ms < $minute_cutoff;
            DELETE FROM metric_rollups
            WHERE bucket_seconds = 3600
              AND bucket_start_ms < $hour_cutoff;
            DELETE FROM diagnostic_events
            WHERE last_seen_ms < $hour_cutoff;
            """;
        command.Parameters.AddWithValue("$raw_cutoff", rawCutoff);
        command.Parameters.AddWithValue("$minute_cutoff", minuteCutoff);
        command.Parameters.AddWithValue("$hour_cutoff", hourCutoff);
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private void Degrade(string errorCode)
    {
        Volatile.Write(ref _lastErrorCode, errorCode);
        Volatile.Write(
            ref _state,
            (int)SqliteHistoryState.DegradedReadOnly);
    }

    private sealed record StorageWorkItem(
        AgentSnapshot? Snapshot,
        DiagnosticEventContract? DiagnosticEvent,
        string Json,
        int Bytes);

    private sealed record PersistedBatch(
        int Snapshots,
        int DiagnosticEvents);
}
