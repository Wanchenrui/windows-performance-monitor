using PerfMonitor.Diagnostics;

namespace PerfMonitor.Storage.Sqlite;

public sealed record SqliteHistoryOptions
{
    public required string DatabasePath { get; init; }
    public int QueueCapacity { get; init; } = 256;
    public int BatchSize { get; init; } = 64;
    public int MaxSnapshotBytes { get; init; } = 65_536;
    public int MaxDiagnosticEventBytes { get; init; } = 131_072;
    public long MaxQueueBytes { get; init; } = 8 * 1024 * 1024;
    public long MaxBatchBytes { get; init; } = 1024 * 1024;
    public DiagnosticPolicy DiagnosticPolicy { get; init; } = DiagnosticPolicy.Default;
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

        if (MaxSnapshotBytes is < 1024 or > 4 * 1024 * 1024 ||
            MaxDiagnosticEventBytes is < 1024 or > 4 * 1024 * 1024 ||
            MaxQueueBytes < Math.Max(MaxSnapshotBytes, MaxDiagnosticEventBytes) ||
            MaxQueueBytes > 256 * 1024 * 1024 ||
            MaxBatchBytes < Math.Max(MaxSnapshotBytes, MaxDiagnosticEventBytes) ||
            MaxBatchBytes > MaxQueueBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxQueueBytes));

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
            DiagnosticPolicy = DiagnosticPolicy.Validate(),
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
    long AcceptedDiagnosticEvents,
    long PersistedDiagnosticEvents,
    long DroppedDiagnosticEvents,
    long WriteFailures,
    string? LastErrorCode)
{
    public long QueueBytes { get; init; }
    public long PeakQueueBytes { get; init; }
    public long DroppedPayloadTooLarge { get; init; }
    public long DroppedQueueByteLimit { get; init; }
}
