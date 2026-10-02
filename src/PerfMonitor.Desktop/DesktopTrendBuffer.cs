using System.Text;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Desktop;

public sealed record TrendPoint(double ElapsedSeconds, DateTimeOffset ObservedAtUtc,
    double? Value, string Quality, bool BreakBefore, long ObservationSequence)
{
    public int PayloadBytes => 48 + Encoding.UTF8.GetByteCount(Quality);
}

/// <summary>Bounded, session-local observations received by the visible UI. No collection or stored history.</summary>
public sealed class DesktopTrendBuffer
{
    public const int MaxPointsPerSeries = 600;
    public const int MaxPayloadBytes = 384 * 1024;
    public const double RetainedSeconds = 300;
    public static readonly (string Group, string Metric)[] Metrics =
    [
        (GroupIds.SystemCpu, MetricIds.SystemCpuUtilization),
        (GroupIds.Memory, MetricIds.MemoryUtilization),
        (GroupIds.Network, MetricIds.NetworkReceiveBytesPerSecond),
        (GroupIds.Network, MetricIds.NetworkSendBytesPerSecond),
        (GroupIds.DiskIo, MetricIds.DiskReadBytesPerSecond),
        (GroupIds.DiskIo, MetricIds.DiskWriteBytesPerSecond),
    ];
    private sealed class Series
    {
        public readonly List<TrendPoint> Points = [];
        public long? Observation;
        public double? Elapsed;
        public bool BreakPending = true;
    }
    private readonly Dictionary<string, Series> _series = Metrics.ToDictionary(item => item.Metric, _ => new Series());
    private long? _lastDelivery;
    public string? InstanceId { get; private set; }
    public double EndElapsedSeconds { get; private set; }
    public int PayloadBytes { get; private set; }
    public IReadOnlyList<TrendPoint> Points(string metric) => _series[metric].Points.ToArray();

    public void BreakContinuity()
    {
        foreach (var series in _series.Values) series.BreakPending = true;
    }

    public void Observe(DesktopConnectionState state)
    {
        var snapshot = state.LatestSnapshot;
        if (state.Status != DesktopConnectionStatus.Connected || snapshot is null ||
            snapshot.InstanceId != state.InstanceId || snapshot.ElapsedSeconds is not { } elapsed ||
            !double.IsFinite(elapsed) || elapsed < 0)
        {
            BreakContinuity();
            return;
        }
        if (InstanceId != snapshot.InstanceId)
        {
            foreach (var series in _series.Values)
            {
                series.Points.Clear(); series.Observation = null; series.Elapsed = null; series.BreakPending = true;
            }
            PayloadBytes = 0; EndElapsedSeconds = elapsed; InstanceId = snapshot.InstanceId;
            _lastDelivery = null;
        }
        if (_lastDelivery is { } previousDelivery && snapshot.DeliverySequence is { } delivery && delivery > previousDelivery + 1)
            BreakContinuity();
        _lastDelivery = snapshot.DeliverySequence;
        EndElapsedSeconds = Math.Max(EndElapsedSeconds, elapsed);
        foreach (var (groupId, metric) in Metrics)
        {
            var series = _series[metric];
            if (!snapshot.Groups.TryGetValue(groupId, out var group) ||
                group.ObservedAtUtc is not { } utc || group.ObservationSequence is not > 0 ||
                group.ObservedElapsedSeconds is not { } observed || !double.IsFinite(observed) ||
                observed < 0 || observed > elapsed)
            {
                series.BreakPending = true;
                continue;
            }
            var value = group.Freshness == FreshnessStates.Fresh
                ? SnapshotPresentation.ReadMetric(snapshot, groupId, metric) : null;
            if (value < 0 || ((groupId == GroupIds.SystemCpu || groupId == GroupIds.Memory) && value > 100))
                value = null;
            // A held value changing quality can break continuity, but is never a new point.
            if (series.Observation == group.ObservationSequence)
            {
                if (value is null) series.BreakPending = true;
                continue;
            }
            if (series.Elapsed is { } previous && (observed <= previous || group.ObservationSequence <= series.Observation))
            {
                series.BreakPending = true;
                continue;
            }
            var quality = SnapshotPresentation.Group(state, groupId).Status;
            if (value is null && !quality.Contains("缺测", StringComparison.Ordinal)) quality += " · 指标缺测";
            if (quality.Length > 128) quality = quality[..128];
            var point = new TrendPoint(observed, utc, value, quality,
                series.BreakPending || series.Elapsed is { } last && observed - last > 3.5,
                group.ObservationSequence.Value);
            series.Points.Add(point); PayloadBytes += point.PayloadBytes;
            series.Elapsed = observed; series.Observation = group.ObservationSequence;
            series.BreakPending = value is null;
        }
        foreach (var series in _series.Values)
            while (series.Points.Count > MaxPointsPerSeries ||
                series.Points.Count > 0 && series.Points[0].ElapsedSeconds < EndElapsedSeconds - RetainedSeconds)
                RemoveFirst(series);
        while (PayloadBytes > MaxPayloadBytes)
        {
            var oldest = _series.Values.Where(series => series.Points.Count > 0)
                .MinBy(series => series.Points[0].ElapsedSeconds);
            if (oldest is null) break;
            RemoveFirst(oldest);
        }
    }

    private void RemoveFirst(Series series)
    {
        PayloadBytes -= series.Points[0].PayloadBytes;
        series.Points.RemoveAt(0);
        if (series.Points.Count > 0) series.Points[0] = series.Points[0] with { BreakBefore = true };
    }
}
