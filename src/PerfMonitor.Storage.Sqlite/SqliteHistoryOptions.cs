namespace PerfMonitor.Storage.Sqlite;

public sealed record SqliteHistoryOptions
{
    public required string DatabasePath { get; init; }
    public int QueueCapacity { get; init; } = 256;
    public int BatchSize { get; init; } = 64;
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RawRetention { get; init; } = TimeSpan.FromHours(48);
    public TimeSpan MinuteRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan HourRetention { get; init; } = TimeSpan.FromDays(366);

    internal SqliteHistoryOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new ArgumentException(
                "DatabasePath is required.",
                nameof(DatabasePath));
        }

        if (QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }

        if (BatchSize <= 0 || BatchSize > QueueCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        }

        if (BusyTimeout <= TimeSpan.Zero ||
            BusyTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(BusyTimeout));
        }

        if (RawRetention <= TimeSpan.Zero ||
            MinuteRetention < RawRetention ||
            HourRetention < MinuteRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RawRetention),
                "Retention windows must be positive and ordered.");
        }

        return this with
        {
            DatabasePath = Path.GetFullPath(DatabasePath),
        };
    }
}

public enum SqliteHistoryState
{
    Created,
    Healthy,
    DegradedReadOnly,
    Stopped,
}

public sealed record SqliteHistoryHealth(
    SqliteHistoryState State,
    long AcceptedSamples,
    long PersistedSamples,
    long DroppedPersistenceSamples,
    long WriteFailures,
    string? LastErrorCode);
