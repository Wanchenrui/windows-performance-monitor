using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal sealed record HardwareWorkerOptions
{
    public const string WorkerExecutableName =
        "perf-monitor-provider-worker.exe";

    public string WorkerPath { get; init; } = Path.Combine(
        AppContext.BaseDirectory,
        "provider-worker",
        WorkerExecutableName);

    public long MaxPrivateMemoryBytes { get; init; } =
        256L * 1024 * 1024;

    public int MaxStartsPerWindow { get; init; } = 3;

    public TimeSpan RestartWindow { get; init; } =
        TimeSpan.FromMinutes(1);

    public TimeSpan GracefulStopTimeout { get; init; } =
        TimeSpan.FromMilliseconds(500);

    public int MaxStderrBytes { get; init; } = 4_096;

    public static HardwareWorkerOptions Default { get; } = new();

    public void Validate()
    {
        var fullPath = Path.GetFullPath(WorkerPath);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                WorkerExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Worker path must name the fixed Worker executable.",
                nameof(WorkerPath));
        }
        if (MaxPrivateMemoryBytes <= 0 ||
            MaxStartsPerWindow <= 0 ||
            RestartWindow <= TimeSpan.Zero ||
            GracefulStopTimeout <= TimeSpan.Zero ||
            MaxStderrBytes <= 0 ||
            MaxStderrBytes >
                ProviderWorkerProtocol.MaxMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HardwareWorkerOptions));
        }
    }
}
