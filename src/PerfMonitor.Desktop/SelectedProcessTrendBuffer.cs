using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Desktop;

/// <summary>One selected process instance, observed only after selection from existing Desktop snapshots.</summary>
public sealed class SelectedProcessTrendBuffer
{
    public const int MaxPoints = 600;
    public const int MaxPayloadBytes = 64 * 1024;
    public const double RetainedSeconds = 300;
    private readonly List<TrendPoint> _points = [];
    private long? _observation, _delivery;
    private double? _observed;
    private bool _breakPending = true;
    public DesktopProcessIdentity? Identity { get; private set; }
    public string? AgentInstanceId { get; private set; }
    public string Name { get; private set; } = "";
    public string Status { get; private set; } = "选中后开始记录";
    public double EndElapsedSeconds { get; private set; }
    public int PayloadBytes { get; private set; }
    public IReadOnlyList<TrendPoint> Points => _points.ToArray();

    public void Select(DesktopConnectionState state, ProcessPresentation process)
    {
        var snapshot = state.LatestSnapshot;
        if (process.Identity is null || state.InstanceId is null ||
            snapshot is null || state.Status != DesktopConnectionStatus.Connected || snapshot.InstanceId != state.InstanceId) return;
        if (Identity == process.Identity && AgentInstanceId == state.InstanceId) return;
        Clear(); Identity = process.Identity; AgentInstanceId = state.InstanceId; Name = process.Name;
        Status = "等待选中后的新观测";
        // The displayed snapshot is a baseline, not history collected after selection.
        if (snapshot.Groups.TryGetValue(GroupIds.Processes, out var group))
        {
            _observation = group.ObservationSequence;
            _observed = group.ObservedElapsedSeconds;
        }
        _delivery = snapshot.DeliverySequence;
        EndElapsedSeconds = snapshot.ElapsedSeconds ?? 0;
    }

    public void Clear()
    {
        Identity = null; AgentInstanceId = null; Name = ""; _points.Clear(); PayloadBytes = 0;
        _observation = null; _observed = null; _delivery = null; _breakPending = true;
        EndElapsedSeconds = 0; Status = "选中后开始记录";
    }

    public void BreakContinuity() => _breakPending = true;

    public void Observe(DesktopConnectionState state)
    {
        if (Identity is not { } identity) return;
        var snapshot = state.LatestSnapshot;
        if (state.Status != DesktopConnectionStatus.Connected || snapshot is null ||
            state.InstanceId != AgentInstanceId || snapshot.InstanceId != AgentInstanceId)
        {
            Status = state.InstanceId is not null && state.InstanceId != AgentInstanceId
                ? "Agent 实例已更换 · 请重新选择" : "连接中断 · 等待新观测";
            BreakContinuity(); return;
        }
        if (snapshot.ElapsedSeconds is not { } elapsed || !double.IsFinite(elapsed) || elapsed < 0)
        { Status = "时间缺测"; BreakContinuity(); return; }
        EndElapsedSeconds = Math.Max(EndElapsedSeconds, elapsed);
        if (_delivery is { } lastDelivery && snapshot.DeliverySequence is { } delivery && delivery > lastDelivery + 1)
            BreakContinuity();
        _delivery = snapshot.DeliverySequence;
        if (!snapshot.Groups.TryGetValue(GroupIds.Processes, out var group) || group.ObservedAtUtc is not { } utc ||
            group.ObservationSequence is not > 0 || group.ObservedElapsedSeconds is not { } observed ||
            !double.IsFinite(observed) || observed < 0 || observed > elapsed)
        { Status = "进程观测缺测"; BreakContinuity(); Trim(); return; }
        var process = SnapshotPresentation.FindProcess(state, identity);
        var cpu = group.Freshness == FreshnessStates.Fresh ? process?.Cpu : null;
        Status = process is null ? "当前未观测到该实例 · 可能退出或不可读" :
            cpu is null ? "CPU 缺测 · 曲线留空" : "实时 · 选中后记录";
        if (group.ObservationSequence == _observation)
        { if (cpu is null) BreakContinuity(); Trim(); return; }
        if (_observed is { } previous && (observed <= previous || group.ObservationSequence <= _observation))
        { Status = "观测顺序异常 · 曲线留空"; BreakContinuity(); Trim(); return; }
        var point = new TrendPoint(observed, utc, cpu, Status,
            _breakPending || _observed is { } last && observed - last > 5,
            group.ObservationSequence.Value);
        _points.Add(point); PayloadBytes += point.PayloadBytes;
        _observation = group.ObservationSequence; _observed = observed; _breakPending = cpu is null;
        Trim();
    }

    private void Trim()
    {
        while (_points.Count > MaxPoints || PayloadBytes > MaxPayloadBytes ||
            _points.Count > 0 && _points[0].ElapsedSeconds < EndElapsedSeconds - RetainedSeconds)
        {
            PayloadBytes -= _points[0].PayloadBytes; _points.RemoveAt(0);
            if (_points.Count > 0) _points[0] = _points[0] with { BreakBefore = true };
        }
    }
}
