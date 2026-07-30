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
    private readonly Channel<AgentSnapshot> _channel;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _writerTask;
    private int _started;
    private int _state = (int)SqliteHistoryState.Created;
    private long _acceptedSamples;
    private long _persistedSamples;
    private long _droppedSamples;
    private long _writeFailures;
    private string? _lastErrorCode;
    private DateTimeOffset _nextRetentionUtc = DateTimeOffset.MinValue;

    public SqliteHistoryStore(SqliteHistoryOptions options)
    {
        _options = options.Validate();
        _database = new SqliteDatabase(_options);
        _channel = Channel.CreateBounded<AgentSnapshot>(
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
        Interlocked.Read(ref _writeFailures),
        Volatile.Read(ref _lastErrorCode));

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
        if ((SqliteHistoryState)Volatile.Read(ref _state) !=
                SqliteHistoryState.Healthy ||
            !_channel.Writer.TryWrite(snapshot))
        {
            Interlocked.Increment(ref _droppedSamples);
            return false;
        }

        Interlocked.Increment(ref _acceptedSamples);
        return true;
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

            var batch = new List<AgentSnapshot>(_options.BatchSize);
            while (await _channel.Reader
                .WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _options.BatchSize &&
                    _channel.Reader.TryRead(out var snapshot))
                {
                    batch.Add(snapshot);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                await WriteBatchAsync(
                    connection,
                    batch,
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _persistedSamples, batch.Count);

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
            while (_channel.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _droppedSamples);
            }
        }
    }

    private static async Task WriteBatchAsync(
        SqliteConnection connection,
        IReadOnlyList<AgentSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Sequence <= 0 ||
                snapshot.CompletedAtUtc is null)
            {
                continue;
            }

            var sampleTimeMs =
                snapshot.CompletedAtUtc.Value.ToUnixTimeMilliseconds();
            await InsertSnapshotAsync(
                connection,
                transaction,
                snapshot,
                sampleTimeMs,
                cancellationToken).ConfigureAwait(false);

            foreach (var metric in SnapshotMetricExtractor.Extract(snapshot))
            {
                var inserted = await InsertRawMetricAsync(
                    connection,
                    transaction,
                    snapshot,
                    sampleTimeMs,
                    metric,
                    cancellationToken).ConfigureAwait(false);
                if (!inserted)
                {
                    continue;
                }

                await UpsertRollupAsync(
                    connection,
                    transaction,
                    snapshot.Sequence,
                    sampleTimeMs,
                    metric,
                    bucketSeconds: 60,
                    cancellationToken).ConfigureAwait(false);
                await UpsertRollupAsync(
                    connection,
                    transaction,
                    snapshot.Sequence,
                    sampleTimeMs,
                    metric,
                    bucketSeconds: 3_600,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Commit();
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSnapshot snapshot,
        long sampleTimeMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO snapshots_raw(
                instance_id,
                sequence,
                sample_time_ms,
                snapshot_json
            ) VALUES (
                $instance_id,
                $sequence,
                $sample_time_ms,
                $snapshot_json
            );
            """;
        command.Parameters.AddWithValue(
            "$instance_id",
            snapshot.InstanceId);
        command.Parameters.AddWithValue("$sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("$sample_time_ms", sampleTimeMs);
        command.Parameters.AddWithValue(
            "$snapshot_json",
            JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));
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
                value
            ) VALUES (
                $instance_id,
                $sequence,
                $sample_time_ms,
                $metric_id,
                $unit,
                $value
            );
            """;
        command.Parameters.AddWithValue(
            "$instance_id",
            snapshot.InstanceId);
        command.Parameters.AddWithValue("$sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("$sample_time_ms", sampleTimeMs);
        command.Parameters.AddWithValue("$metric_id", metric.MetricId);
        command.Parameters.AddWithValue("$unit", metric.Unit);
        command.Parameters.AddWithValue("$value", metric.Value);
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
}
