using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Desktop;

public sealed record WatchedProcess(string AgentInstanceId, DesktopProcessIdentity Identity, string Name);
public sealed record WatchedProcessPresentation(WatchedProcess Entry, ProcessPresentation? Process, string Status)
{
    public bool CanSelect => Process is not null;
}

/// <summary>Desktop-lifetime pins, never name-based preferences or additional sampling.</summary>
public sealed class WatchedProcessList
{
    public const int MaxEntries = 12;
    private readonly List<WatchedProcess> _entries = [];
    public IReadOnlyList<WatchedProcess> Entries => _entries.ToArray();
    public bool Contains(string? instanceId, DesktopProcessIdentity identity) =>
        _entries.Any(entry => entry.AgentInstanceId == instanceId && entry.Identity == identity);

    public bool TryPin(DesktopConnectionState state, ProcessPresentation process)
    {
        if (process.Identity is not { } identity || state.InstanceId is not { } instance || !IsCurrentObservation(state) ||
            SnapshotPresentation.FindProcess(state, identity) is not { } current) return false;
        if (Contains(instance, identity)) return true;
        if (_entries.Count >= MaxEntries) return false;
        _entries.Add(new(instance, identity, current.Name));
        return true;
    }

    public bool Remove(string instanceId, DesktopProcessIdentity identity) =>
        _entries.RemoveAll(entry => entry.AgentInstanceId == instanceId && entry.Identity == identity) > 0;

    public IReadOnlyList<WatchedProcessPresentation> Present(DesktopConnectionState state)
    {
        var current = IsCurrentObservation(state);
        var found = current ? SnapshotPresentation.FindProcesses(state,
            _entries.Where(entry => entry.AgentInstanceId == state.InstanceId).Select(entry => entry.Identity)) : null;
        return _entries.Select(entry =>
        {
            if (state.InstanceId is not null && entry.AgentInstanceId != state.InstanceId)
                return new WatchedProcessPresentation(entry, null, "旧 Agent 会话 · 未继续采集");
            if (state.Status != DesktopConnectionStatus.Connected || state.LatestSnapshot?.InstanceId != entry.AgentInstanceId)
                return new WatchedProcessPresentation(entry, null, "连接中断 · 等待新观测");
            if (!current)
                return new WatchedProcessPresentation(entry, null, "观测缺测或陈旧 · 等待更新");
            var process = found!.GetValueOrDefault(entry.Identity);
            return new WatchedProcessPresentation(entry, process, process is null
                ? "当前未观测到 · 可能退出或不可读"
                : process.Cpu is null ? "CPU 缺测 · 可查看详情" : "当前观测 · 点击查看");
        }).ToArray();
    }

    public static bool IsCurrentObservation(DesktopConnectionState state) =>
        state.Status == DesktopConnectionStatus.Connected && state.InstanceId is not null &&
        state.LatestSnapshot is { } snapshot && snapshot.InstanceId == state.InstanceId &&
        snapshot.Groups.GetValueOrDefault(GroupIds.Processes) is { } group &&
        group.Availability is (AvailabilityStates.Available or AvailabilityStates.Partial) &&
        group.Freshness == FreshnessStates.Fresh && group.ObservedAtUtc is not null &&
        group.ObservationSequence is > 0;
}
