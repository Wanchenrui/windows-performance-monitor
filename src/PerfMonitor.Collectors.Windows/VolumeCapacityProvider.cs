using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

public sealed class VolumeCapacityProvider : IMetricProvider
{
    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Volumes,
        ProviderIds.Volumes,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drives = DriveInfo.GetDrives();
        var data = new JsonArray();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var systemRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.System));
        var enumerated = 0;

        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            enumerated++;
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    Increment(reasons, StableErrorCodes.InvalidData);
                    continue;
                }

                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = Math.Max(0, total - free);
                var utilization = Math.Clamp(
                    100.0 * used / total,
                    0,
                    100);
                data.Add(
                    new JsonObject
                    {
                        ["volumeId"] = drive.Name,
                        ["mountpoint"] = drive.RootDirectory.FullName,
                        ["fileSystem"] = drive.DriveFormat,
                        ["isSystem"] = string.Equals(
                            drive.RootDirectory.FullName,
                            systemRoot,
                            StringComparison.OrdinalIgnoreCase),
                        ["metrics"] = new JsonObject
                        {
                            [MetricIds.VolumeUtilization] = MetricJson.Value(
                                utilization,
                                Units.Percent,
                                SourceIds.Volumes),
                            [MetricIds.VolumeUsedBytes] = MetricJson.Value(
                                used,
                                Units.Byte,
                                SourceIds.Volumes),
                            [MetricIds.VolumeFreeBytes] = MetricJson.Value(
                                free,
                                Units.Byte,
                                SourceIds.Volumes),
                            [MetricIds.VolumeTotalBytes] = MetricJson.Value(
                                total,
                                Units.Byte,
                                SourceIds.Volumes),
                        },
                    });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Increment(reasons, ExceptionClassifier.StableCode(exception));
            }
        }

        var readable = data.Count;
        var skipped = enumerated - readable;
        var availability = readable == 0
            ? AvailabilityStates.Error
            : skipped == 0
                ? AvailabilityStates.Available
                : AvailabilityStates.Partial;
        var coverage = new ProviderCoverage(
            skipped == 0 ? "complete" : "limited",
            enumerated,
            readable,
            skipped,
            MetricJson.ReadOnlyReasons(reasons));
        var errors = reasons.Keys
            .Order(StringComparer.Ordinal)
            .Select(code => new ProviderError(code, null))
            .ToArray();
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                availability,
                coverage,
                errors,
                readable == 0 ? null : data));
    }

    private static void Increment(
        IDictionary<string, int> reasons,
        string reason)
    {
        reasons.TryGetValue(reason, out var count);
        reasons[reason] = count + 1;
    }
}
