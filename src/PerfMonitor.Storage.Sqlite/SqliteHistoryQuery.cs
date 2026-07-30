using Microsoft.Data.Sqlite;
using PerfMonitor.Contracts;

namespace PerfMonitor.Storage.Sqlite;

internal static class SqliteHistoryQuery
{
    private const long MillisecondsPerMinute = 60_000;
    private const long MillisecondsPerHour = 3_600_000;
    private static readonly HashSet<string> SupportedMetricIds =
        new(HistoryPolicy.MetricIds, StringComparer.Ordinal);

    public static async Task<HistoryContract> ExecuteAsync(
        SqliteConnection connection,
        HistoryQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken)
    {
        var validated = Validate(query);
        var source = SelectSource(validated);
        var sourcePointCount = await CountSourcePointsAsync(
            connection,
            validated,
            source,
            cancellationToken).ConfigureAwait(false);
        var points = await ReadPointsAsync(
            connection,
            validated,
            source,
            cancellationToken).ConfigureAwait(false);

        return new HistoryContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            InstanceId = responseInstanceId,
            Query = query,
            SourcePointCount = sourcePointCount,
            PointCount = points.Count,
            Downsampled = sourcePointCount > points.Count,
            Points = points,
        };
    }

    private static ValidatedQuery Validate(HistoryQueryContract query)
    {
        if (query.FromEpochMs is null ||
            query.ToEpochMs is null ||
            query.FromEpochMs > query.ToEpochMs)
        {
            throw new ArgumentException(
                "history_range_invalid",
                nameof(query));
        }

        if (query.MaxPoints <= 0 ||
            query.MaxPoints > HistoryPolicy.MaxPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "history_max_points_invalid");
        }

        if (query.MetricIds.Count is 0 or > HistoryPolicy.MaxMetricIds ||
            query.MetricIds.Distinct(StringComparer.Ordinal).Count() !=
                query.MetricIds.Count ||
            query.MetricIds.Any(
                metricId => !SupportedMetricIds.Contains(metricId)))
        {
            throw new ArgumentException(
                "history_metric_ids_invalid",
                nameof(query));
        }

        var inclusiveRangeMilliseconds = checked(
            query.ToEpochMs.Value - query.FromEpochMs.Value + 1);
        if (inclusiveRangeMilliseconds >
            HistoryPolicy.MaxQueryRange.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "history_range_too_large");
        }

        return new ValidatedQuery(
            query.MetricIds,
            query.FromEpochMs.Value,
            query.ToEpochMs.Value,
            query.MaxPoints,
            inclusiveRangeMilliseconds);
    }

    private static QuerySource SelectSource(ValidatedQuery query)
    {
        if (query.RangeMilliseconds <= TimeSpan
            .FromHours(24).TotalMilliseconds)
        {
            return new QuerySource(0);
        }

        if (query.RangeMilliseconds <= TimeSpan
            .FromDays(30).TotalMilliseconds)
        {
            return new QuerySource(60);
        }

        return new QuerySource(3_600);
    }

    private static async Task<int> CountSourcePointsAsync(
        SqliteConnection connection,
        ValidatedQuery query,
        QuerySource source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var metricParameters = AddParameters(command, query);
        if (source.BucketSeconds == 0)
        {
            command.CommandText = $"""
                SELECT COUNT(*)
                FROM (
                    SELECT instance_id, sequence
                    FROM metrics_raw
                    WHERE sample_time_ms BETWEEN $from_ms AND $to_ms
                      AND metric_id IN ({metricParameters})
                    GROUP BY instance_id, sequence
                );
                """;
        }
        else
        {
            command.CommandText = $"""
                SELECT COALESCE(MAX(metric_count), 0)
                FROM (
                    SELECT SUM(sample_count) AS metric_count
                    FROM metric_rollups
                    WHERE bucket_seconds = $source_bucket_seconds
                      AND bucket_start_ms BETWEEN
                          $source_from_ms AND $to_ms
                      AND metric_id IN ({metricParameters})
                    GROUP BY metric_id
                );
                """;
            command.Parameters.AddWithValue(
                "$source_bucket_seconds",
                source.BucketSeconds);
            command.Parameters.AddWithValue(
                "$source_from_ms",
                FloorToBucket(
                    query.FromEpochMs,
                    checked(source.BucketSeconds * 1_000L)));
        }

        var result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        var count = Convert.ToInt64(
            result,
            System.Globalization.CultureInfo.InvariantCulture);
        return checked((int)Math.Min(int.MaxValue, count));
    }

    private static async Task<IReadOnlyList<HistoryPointContract>>
        ReadPointsAsync(
            SqliteConnection connection,
            ValidatedQuery query,
            QuerySource source,
            CancellationToken cancellationToken)
    {
        var baseBucketMilliseconds = source.BucketSeconds switch
        {
            0 => 1L,
            60 => MillisecondsPerMinute,
            3_600 => MillisecondsPerHour,
            _ => throw new InvalidOperationException(
                "Unsupported history source bucket."),
        };
        var outputOriginMilliseconds = source.BucketSeconds == 0
            ? query.FromEpochMs
            : FloorToBucket(
                query.FromEpochMs,
                baseBucketMilliseconds);
        var outputRangeMilliseconds = checked(
            query.ToEpochMs - outputOriginMilliseconds + 1);
        var requestedBucketMilliseconds = CeilingDivide(
            outputRangeMilliseconds,
            query.MaxPoints);
        var outputBucketMilliseconds = checked(
            CeilingDivide(
                Math.Max(
                    baseBucketMilliseconds,
                    requestedBucketMilliseconds),
                baseBucketMilliseconds) *
            baseBucketMilliseconds);

        await using var command = connection.CreateCommand();
        var metricParameters = AddParameters(command, query);
        command.Parameters.AddWithValue(
            "$output_bucket_ms",
            outputBucketMilliseconds);
        command.Parameters.AddWithValue(
            "$output_origin_ms",
            outputOriginMilliseconds);

        if (source.BucketSeconds == 0)
        {
            command.CommandText = $"""
                WITH bucketed AS (
                    SELECT
                        ((sample_time_ms - $output_origin_ms) /
                            $output_bucket_ms) AS output_bucket,
                        metric_id,
                        unit,
                        value,
                        sample_time_ms,
                        sequence
                    FROM metrics_raw
                    WHERE sample_time_ms BETWEEN $from_ms AND $to_ms
                      AND metric_id IN ({metricParameters})
                ),
                ranked AS (
                    SELECT
                        *,
                        ROW_NUMBER() OVER (
                            PARTITION BY output_bucket, metric_id
                            ORDER BY sample_time_ms DESC, sequence DESC
                        ) AS recency_rank
                    FROM bucketed
                )
                SELECT
                    output_bucket,
                    metric_id,
                    MIN(unit),
                    MIN(value),
                    MAX(value),
                    AVG(value),
                    MAX(CASE
                        WHEN recency_rank = 1 THEN value
                        ELSE NULL
                    END),
                    MAX(CASE
                        WHEN recency_rank = 1 THEN sequence
                        ELSE NULL
                    END)
                FROM ranked
                GROUP BY output_bucket, metric_id
                ORDER BY output_bucket, metric_id;
                """;
        }
        else
        {
            command.CommandText = $"""
                WITH bucketed AS (
                    SELECT
                        ((bucket_start_ms - $output_origin_ms) /
                            $output_bucket_ms) AS output_bucket,
                        metric_id,
                        unit,
                        min_value,
                        max_value,
                        sum_value,
                        sample_count,
                        last_value,
                        last_sample_ms,
                        last_sequence
                    FROM metric_rollups
                    WHERE bucket_seconds = $source_bucket_seconds
                      AND bucket_start_ms BETWEEN
                          $source_from_ms AND $to_ms
                      AND metric_id IN ({metricParameters})
                ),
                ranked AS (
                    SELECT
                        *,
                        ROW_NUMBER() OVER (
                            PARTITION BY output_bucket, metric_id
                            ORDER BY
                                last_sample_ms DESC,
                                last_sequence DESC
                        ) AS recency_rank
                    FROM bucketed
                )
                SELECT
                    output_bucket,
                    metric_id,
                    MIN(unit),
                    MIN(min_value),
                    MAX(max_value),
                    SUM(sum_value) / SUM(sample_count),
                    MAX(CASE
                        WHEN recency_rank = 1 THEN last_value
                        ELSE NULL
                    END),
                    MAX(CASE
                        WHEN recency_rank = 1 THEN last_sequence
                        ELSE NULL
                    END)
                FROM ranked
                GROUP BY output_bucket, metric_id
                ORDER BY output_bucket, metric_id;
                """;
            command.Parameters.AddWithValue(
                "$source_bucket_seconds",
                source.BucketSeconds);
            command.Parameters.AddWithValue(
                "$source_from_ms",
                FloorToBucket(
                    query.FromEpochMs,
                    baseBucketMilliseconds));
        }

        var builders = new SortedDictionary<long, PointBuilder>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var outputBucket = reader.GetInt64(0);
            if (outputBucket < 0 ||
                outputBucket >= query.MaxPoints)
            {
                continue;
            }

            if (!builders.TryGetValue(outputBucket, out var point))
            {
                var start = checked(
                    outputOriginMilliseconds +
                    outputBucket * outputBucketMilliseconds);
                point = new PointBuilder(
                    Math.Max(query.FromEpochMs, start),
                    Math.Min(
                        query.ToEpochMs,
                        checked(
                            start + outputBucketMilliseconds - 1)));
                builders.Add(outputBucket, point);
            }

            point.Sequence = Math.Max(point.Sequence, reader.GetInt64(7));
            point.Metrics.Add(
                reader.GetString(1),
                new HistoryMetricStatsContract
                {
                    Unit = reader.GetString(2),
                    Min = reader.GetDouble(3),
                    Max = reader.GetDouble(4),
                    Avg = reader.GetDouble(5),
                    Last = reader.GetDouble(6),
                });
        }

        return builders.Values
            .Select(builder => new HistoryPointContract
            {
                StartEpochMs = builder.StartEpochMs,
                EndEpochMs = builder.EndEpochMs,
                Sequence = builder.Sequence,
                Metrics = builder.Metrics,
            })
            .ToArray();
    }

    private static string AddParameters(
        SqliteCommand command,
        ValidatedQuery query)
    {
        command.Parameters.AddWithValue("$from_ms", query.FromEpochMs);
        command.Parameters.AddWithValue("$to_ms", query.ToEpochMs);
        var names = new string[query.MetricIds.Count];
        for (var index = 0; index < query.MetricIds.Count; index++)
        {
            names[index] = $"$metric_{index}";
            command.Parameters.AddWithValue(
                names[index],
                query.MetricIds[index]);
        }

        return string.Join(", ", names);
    }

    private static long FloorToBucket(long value, long bucket) =>
        value - Modulo(value, bucket);

    private static long Modulo(long value, long divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static long CeilingDivide(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);

    private sealed record ValidatedQuery(
        IReadOnlyList<string> MetricIds,
        long FromEpochMs,
        long ToEpochMs,
        int MaxPoints,
        long RangeMilliseconds);

    private sealed record QuerySource(int BucketSeconds);

    private sealed class PointBuilder(
        long startEpochMs,
        long endEpochMs)
    {
        public long StartEpochMs { get; } = startEpochMs;
        public long EndEpochMs { get; } = endEpochMs;
        public long Sequence { get; set; }
        public Dictionary<string, HistoryMetricStatsContract> Metrics
        {
            get;
        } = new(StringComparer.Ordinal);
    }
}
