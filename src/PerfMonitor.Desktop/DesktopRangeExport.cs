using System.IO;
using System.Text.Json;
using PerfMonitor.Contracts;

namespace PerfMonitor.Desktop;

/// <summary>A local file format; this does not change the Agent IPC contract.</summary>
public sealed record DesktopRangeExportEnvelope(
    string SchemaVersion,
    string Kind,
    DateTimeOffset ExportedAtUtc,
    DesktopTrendExport? Trend,
    DesktopDiagnosticExport? Diagnostics);

public sealed record DesktopExportConnection(string Status, string? CurrentAgentInstanceId, string? ErrorCode);
public sealed record DesktopExportRange(double WindowSeconds, double FromElapsedSeconds, double ToElapsedSeconds);
public sealed record DesktopExportProcess(string Name, int Pid, long CreationTimeTicks, string Status);
public sealed record DesktopExportPoint(double ElapsedSeconds, DateTimeOffset ObservedAtUtc,
    double? Value, string Quality, bool BreakBefore, long ObservationSequence);
public sealed record DesktopExportSeries(string MetricId, string Unit, int PointCount,
    IReadOnlyList<DesktopExportPoint> Points);
public sealed record DesktopTrendExport(string Source, string? AgentInstanceId, string Resource,
    string RangeSemantics, DesktopExportRange Range, DesktopExportConnection Connection,
    DesktopExportProcess? Process, string CoverageNote, IReadOnlyList<DesktopExportSeries> Series);
public sealed record DesktopDiagnosticExport(string Source, string RangeSemantics, bool? StorageAvailable,
    string CoverageNote, DiagnosticsContract Response);

public static class DesktopRangeExport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Null is data: retain missing values and unknown storage availability.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static DesktopRangeExportEnvelope CreateTrend(DesktopTrendBuffer buffer, ResourceView resource,
        double windowSeconds, DesktopConnectionState state, DateTimeOffset exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(state);
        var range = Range(windowSeconds, buffer.EndElapsedSeconds);
        var (name, metrics) = resource switch
        {
            ResourceView.Cpu => ("cpu", new[] { MetricIds.SystemCpuUtilization }),
            ResourceView.Memory => ("memory", new[] { MetricIds.MemoryUtilization }),
            ResourceView.Network => ("network", new[] { MetricIds.NetworkReceiveBytesPerSecond, MetricIds.NetworkSendBytesPerSecond }),
            ResourceView.Disk => ("disk", new[] { MetricIds.DiskReadBytesPerSecond, MetricIds.DiskWriteBytesPerSecond }),
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };
        var unit = resource is ResourceView.Cpu or ResourceView.Memory ? Units.Percent : Units.BytePerSecond;
        var series = metrics.Select(metric => Series(metric, unit, buffer.Points(metric), range)).ToArray();
        return Trend(buffer.InstanceId, name, range, state, null, series, exportedAtUtc);
    }

    public static DesktopRangeExportEnvelope CreateProcessTrend(SelectedProcessTrendBuffer buffer,
        double windowSeconds, DesktopConnectionState state, DateTimeOffset exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(state);
        var identity = buffer.Identity ?? throw new InvalidOperationException("export_process_not_selected");
        var range = Range(windowSeconds, buffer.EndElapsedSeconds);
        var process = new DesktopExportProcess(buffer.Name, identity.Pid, identity.CreationTimeTicks, buffer.Status);
        return Trend(buffer.AgentInstanceId, "processCpu", range, state, process,
            [Series(MetricIds.ProcessCpuNormalized, Units.Percent, buffer.Points, range)], exportedAtUtc);
    }

    public static DesktopRangeExportEnvelope CreateDiagnostics(DiagnosticsContract response,
        DateTimeOffset exportedAtUtc, bool? storageAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var query = response.Query;
        var validated = DiagnosticQueryValidation.Validate(query);
        if (response.EventCount != response.Events.Count || response.Events.Count > validated.MaxEvents)
            throw new InvalidDataException("export_diagnostic_response_count_invalid");
        if (response.Events.Any(item => !validated.Matches(item)))
            throw new InvalidDataException("export_diagnostic_response_outside_query");
        if (response.Events.Any(item => !double.IsFinite(item.Confidence) ||
            !double.IsFinite(item.CooldownSeconds) || !double.IsFinite(item.Debounce.ActivateSeconds) ||
            !double.IsFinite(item.Debounce.RecoverSeconds) ||
            item.Evidence.Any(evidence => evidence.Value is { } value && !double.IsFinite(value))))
            throw new InvalidDataException("export_diagnostic_non_finite_value");
        var frozen = response with
        {
            Query = query with { RuleIds = query.RuleIds.ToArray(), States = query.States.ToArray() },
            Events = response.Events.Select(item => item with { Evidence = item.Evidence.ToArray() }).ToArray(),
        };
        return new("1", "diagnostics", exportedAtUtc.ToUniversalTime(), null,
            new("agentRangeQuery", "lastSeenUtcEpochMillisecondsInclusive", storageAvailable,
                "范围按事件 LastSeenUtc 的 Unix 毫秒闭区间筛选，不表示事件持续时间与范围相交。" +
                "当前协议未报告持久存储可用性，结果可能只含内存记录；空结果不代表无诊断或完整覆盖。" +
                "truncated 为 true 时结果不完整。", frozen));
    }

    /// <summary>Publish a complete same-directory file. The caller confirms replacement before calling.</summary>
    public static async Task WriteAsync(string outputPath, DesktopRangeExportEnvelope export,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("export_output_must_be_json", nameof(outputPath));
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Same-volume MoveFileEx replacement publishes the complete temporary file atomically.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DesktopRangeExportEnvelope Trend(string? instanceId, string resource, DesktopExportRange range,
        DesktopConnectionState state, DesktopExportProcess? process, IReadOnlyList<DesktopExportSeries> series,
        DateTimeOffset exportedAtUtc) => new("1", "trend", exportedAtUtc.ToUniversalTime(),
        new("desktopReceivedObservations", instanceId, resource, "elapsedSecondsInclusive", range,
            new(state.Status.ToString(), state.InstanceId, state.ErrorCode), process,
            "仅包含此 Desktop 可见时收到并保留的实际观测；隐藏、断连、丢帧及无观测时间的缺口不回填。" +
            "value=null 表示指标缺测；breakBefore=true 表示不能连接到前一点，不代表精确缺口长度。" +
            "quality 保留当时界面质量文本，不包含完整原始质量契约。UTC 仅为各点原始时间，排序与范围使用单调时间。" +
            "范围末端是缓冲最后收到的时间，不是导出时刻；断连后保存的是历史观测。" +
            (process is null ? "" : "进程仅记录选中后的新观测，身份绑定 Agent 会话、PID 与创建时间。"), series), null);

    private static DesktopExportRange Range(double windowSeconds, double end)
    {
        if (windowSeconds is not (60 or 300) || !double.IsFinite(end) || end < 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "export_trend_range_invalid");
        return new(windowSeconds, Math.Max(0, end - windowSeconds), end);
    }

    private static DesktopExportSeries Series(string metric, string unit, IReadOnlyList<TrendPoint> source,
        DesktopExportRange range)
    {
        var points = source.Where(point => point.ElapsedSeconds >= range.FromElapsedSeconds &&
            point.ElapsedSeconds <= range.ToElapsedSeconds).Select((point, index) =>
        {
            if (!double.IsFinite(point.ElapsedSeconds) ||
                point.Value is { } value && !double.IsFinite(value))
                throw new InvalidDataException("export_trend_non_finite_value");
            return new DesktopExportPoint(point.ElapsedSeconds, point.ObservedAtUtc.ToUniversalTime(),
                point.Value, point.Quality, index == 0 || point.BreakBefore, point.ObservationSequence);
        }).ToArray();
        return new(metric, unit, points.Length, points);
    }
}
