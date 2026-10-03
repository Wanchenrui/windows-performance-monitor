using System.Text.Json;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Desktop;

public sealed record GroupPresentation(string GroupId, string Name, string Status, string Detail, bool IsHealthy);
public sealed record DesktopProcessIdentity(int Pid, long CreationTimeTicks);
public sealed record ProcessPresentation(string Name, double? Cpu,
    DesktopProcessIdentity? Identity = null, long? WorkingSetBytes = null, long? PrivateBytes = null);

/// <summary>Formats contract quality for display without collecting or diagnosing.</summary>
public static class SnapshotPresentation
{
    private static readonly (string Id, string Name)[] KnownGroups =
    [
        (GroupIds.SystemCpu, "系统 CPU"), (GroupIds.Memory, "内存"),
        (GroupIds.Network, "网络"), (GroupIds.DiskIo, "磁盘 I/O"),
        (GroupIds.Gpu, "GPU"), (GroupIds.Sensors, "硬件温度"),
        (GroupIds.Power, "电源"), (GroupIds.Volumes, "卷容量"),
        (GroupIds.Processes, "进程"), (GroupIds.Uptime, "运行时间"),
        (GroupIds.Sampler, "调度器"), (GroupIds.Self, "Agent 资源"),
    ];

    public static IReadOnlyList<GroupPresentation> Groups(DesktopConnectionState state) =>
        KnownGroups.Select(item => Group(state, item.Id, item.Name))
            .Concat((state.LatestSnapshot?.Groups.Keys ?? [])
                .Where(id => !KnownGroups.Any(item => item.Id == id))
                .Order(StringComparer.Ordinal)
                .Select(id => Group(state, id, id)))
            .ToArray();

    public static GroupPresentation Group(DesktopConnectionState state, string groupId, string? name = null)
    {
        name ??= KnownGroups.FirstOrDefault(item => item.Id == groupId).Name ?? groupId;
        if (state.LatestSnapshot is null)
            return new(groupId, name, "等待数据", "尚未收到快照", false);
        var connected = state.Status == DesktopConnectionStatus.Connected &&
            state.InstanceId == state.LatestSnapshot.InstanceId;
        var historicalStatus = "历史快照 · " + (state.InstanceId is not null &&
            state.InstanceId != state.LatestSnapshot.InstanceId ? "实例已更换" : "连接中断");
        if (!state.LatestSnapshot.Groups.TryGetValue(groupId, out var group))
            return new(groupId, name, connected ? "缺测" : historicalStatus + " · 缺测",
                "快照未包含此采集组" + (connected ? "" : " · 等待新快照"), false);
        if (group.CollectionState == "paused")
            return new(groupId, name, connected ? "已暂停 · 轻量模式" : historicalStatus + " · 已暂停",
                "本软件主动暂停此可选组 · 恢复原设置后继续采集", false);

        var statuses = new List<string> { Availability(group.Availability) };
        var details = new List<string>();
        var ready = SampleReady(group);
        var missing = group.Data is null ||
            (group.Data is JsonObject metricData && metricData["metrics"] is JsonObject metrics &&
                metrics.Any(metric => metric.Value is not JsonObject entry || entry["value"] is null));
        var batteryAbsent = groupId == GroupIds.Power && group.Data is JsonObject power &&
            power["batteryPresent"] is JsonValue battery && battery.TryGetValue<bool>(out var present) && !present;
        if (batteryAbsent)
        {
            missing = false;
            details.Add("无系统电池 · 电池指标不适用");
        }
        if (missing && group.Availability is (AvailabilityStates.Available or AvailabilityStates.Partial))
        {
            statuses.Add("指标缺测");
            details.Add("部分指标尚无有效值");
        }
        if (group.Freshness == FreshnessStates.WarmingUp || !ready)
        {
            statuses.Add("预热中");
            details.Add(!ready ? "等待相邻有效采样" : "等待首个有效观测");
        }
        if (group.Freshness == FreshnessStates.Stale)
            statuses.Add("陈旧数据");
        else if (group.Freshness is not (FreshnessStates.Fresh or FreshnessStates.WarmingUp))
            statuses.Add($"新鲜度未知（{group.Freshness}）");

        if (group.Coverage.Status != "complete")
            statuses.Add(group.Coverage.Status == "limited" ? "覆盖有限" : "覆盖未知");
        if (group.Coverage.Readable is not null && group.Coverage.Enumerated is not null)
            details.Add($"可读 {group.Coverage.Readable}/{group.Coverage.Enumerated}");
        var reasons = group.Errors.Select(item => item.ErrorCode)
            .Concat(group.Coverage.SkippedByReason?.Keys ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Reason);
        details.AddRange(reasons);
        if (group.ObservedAtUtc is { } observed)
            details.Add($"观测 {observed.ToLocalTime():HH:mm:ss}");
        else
            details.Add("无有效观测时间");

        if (!connected)
        {
            statuses.Insert(0, historicalStatus);
            if (state.ErrorCode is not null) details.Add(Reason(state.ErrorCode));
            details.Add("等待新快照");
        }
        return new(groupId, name, string.Join(" · ", statuses), string.Join(" · ", details),
            connected && ready && !missing && group.ObservedAtUtc is not null &&
            group.Availability == AvailabilityStates.Available &&
            group.Freshness == FreshnessStates.Fresh && group.Coverage.Status == "complete" &&
            group.Errors.Count == 0);
    }

    public static double? ReadMetric(AgentSnapshot snapshot, string groupId, string metricId)
    {
        if (!snapshot.Groups.TryGetValue(groupId, out var group) ||
            group.Availability is not (AvailabilityStates.Available or AvailabilityStates.Partial) ||
            group.Freshness is not (FreshnessStates.Fresh or FreshnessStates.Stale) ||
            !SampleReady(group) ||
            group.Errors.Any(error => error.MetricId == metricId) ||
            group.Data is not JsonObject data || data["metrics"] is not JsonObject metrics ||
            metrics[metricId] is not JsonObject metric || metric["value"] is not JsonValue value)
            return null;
        double? number = value.TryGetValue<double>(out var result) ? result :
            value.TryGetValue<long>(out var integer) ? integer : null;
        return number is { } finite && double.IsFinite(finite) ? finite : null;
    }

    public static string CardQuality(DesktopConnectionState state, string groupId, params string[] metricIds)
    {
        var group = Group(state, groupId);
        var missing = state.LatestSnapshot is { } snapshot &&
            metricIds.Any(id => ReadMetric(snapshot, groupId, id) is null);
        return $"{group.Status}{(missing && !group.Status.Contains("指标缺测", StringComparison.Ordinal) ? " · 指标缺测" : "")}\n{group.Detail}";
    }

    public static string Availability(string value) => value switch
    {
        AvailabilityStates.Available => "可用", AvailabilityStates.Partial => "部分可用",
        AvailabilityStates.Unavailable => "不可用", AvailabilityStates.NotSupported => "不支持",
        AvailabilityStates.PermissionDenied => "权限不足", AvailabilityStates.Timeout => "采集超时",
        AvailabilityStates.Error => "采集失败", _ => $"状态未知（{value}）",
    };

    public static string DiagnosticsSummary(DiagnosticsContract diagnostics)
    {
        if (diagnostics.Truncated) return "诊断：结果已截断，当前状态不完整";
        var current = diagnostics.Events.Where(item => item.InstanceId == diagnostics.InstanceId).ToArray();
        if (current.Any(item => item.ObservationSequence is not > 0))
            return "诊断：历史事件缺少顺序，当前状态未知";
        if (current.Length == 0 && diagnostics.Events.Count > 0)
            return "诊断：仅有历史事件，当前实例尚无状态证据";
        var active = current.GroupBy(item => (item.RuleId, item.SubjectId))
            .Select(group => group.OrderByDescending(item => item.ObservationSequence).First())
            .Where(item => item.State == DiagnosticStates.Active)
            .OrderByDescending(item => item.ObservationSequence).ToArray();
        return active.Length == 0 ? "诊断：查询区间未返回当前活动告警"
            : $"诊断：{active.Length} 个当前活动告警 · 最近 {active[0].RuleId}";
    }

    public static string DiagnosticsSummary(DiagnosticsContract diagnostics, DesktopConnectionState state)
    {
        if (state.Status != DesktopConnectionStatus.Connected || diagnostics.InstanceId != state.InstanceId)
            return "历史诊断 · 等待连接后刷新";
        var summary = DiagnosticsSummary(diagnostics);
        return summary == "诊断：查询区间未返回当前活动告警"
            ? "本次查询未返回活动告警" : "最近查询结果 · " + summary;
    }

    public static IReadOnlyList<ProcessPresentation> ProcessRows(DesktopConnectionState state)
    {
        if (state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Processes) is not { } group ||
            group.ReadOnlyData is not { ValueKind: JsonValueKind.Array } rows) return [];
        return rows.EnumerateArray().Where(row => row.ValueKind == JsonValueKind.Object).Take(4096).Select(row => ProcessRow(group, row))
            .OrderByDescending(row => row.Cpu).Take(12).ToArray();
    }

    public static ProcessPresentation? FindProcess(DesktopConnectionState state, DesktopProcessIdentity identity)
    {
        if (state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Processes) is not { } group ||
            group.ReadOnlyData is not { ValueKind: JsonValueKind.Array } rows) return null;
        foreach (var row in rows.EnumerateArray().Take(4096))
            if (row.ValueKind == JsonValueKind.Object && ReadIdentity(row) == identity) return ProcessRow(group, row);
        return null;
    }

    public static IReadOnlyDictionary<DesktopProcessIdentity, ProcessPresentation> FindProcesses(
        DesktopConnectionState state, IEnumerable<DesktopProcessIdentity> identities)
    {
        var requested = identities.ToHashSet();
        var found = new Dictionary<DesktopProcessIdentity, ProcessPresentation>();
        if (requested.Count == 0 || state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Processes) is not { } group ||
            group.ReadOnlyData is not { ValueKind: JsonValueKind.Array } rows) return found;
        foreach (var row in rows.EnumerateArray().Take(4096))
            if (row.ValueKind == JsonValueKind.Object && ReadIdentity(row) is { } identity && requested.Contains(identity))
                found.TryAdd(identity, ProcessRow(group, row));
        return found;
    }

    private static ProcessPresentation ProcessRow(SnapshotGroup group, JsonElement row)
    {
        var usable = group.Availability is (AvailabilityStates.Available or AvailabilityStates.Partial) &&
            group.Freshness is (FreshnessStates.Fresh or FreshnessStates.Stale) && group.ObservedAtUtc is not null;
        var nameField = ProcessField(row, "name");
        var name = nameField.ValueKind == JsonValueKind.String ? nameField.GetString() ?? "未知进程" : "未知进程";
        double? cpu = null;
        if (usable && !group.Errors.Any(error => error.MetricId == MetricIds.ProcessCpuNormalized) &&
            ProcessField(row, "cpuReady").ValueKind == JsonValueKind.True)
        {
            var value = ProcessField(ProcessField(ProcessField(row, "metrics"), MetricIds.ProcessCpuNormalized), "value");
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                double.IsFinite(number) && number is >= 0 and <= 100) cpu = number;
        }
        return new(name.Length > 64 ? name[..64] : name, cpu, ReadIdentity(row),
            usable ? ReadProcessBytes(row, group, MetricIds.ProcessWorkingSetBytes) : null,
            usable ? ReadProcessBytes(row, group, MetricIds.ProcessPrivateBytes) : null);
    }

    private static DesktopProcessIdentity? ReadIdentity(JsonElement row)
    {
        var identity = ProcessField(row, "identity");
        var pid = ProcessField(identity, "pid"); var created = ProcessField(identity, "creationTimeTicks");
        if (pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var processId) && processId > 0 &&
            created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var ticks) && ticks > 0)
            return new(processId, ticks);
        return null;
    }

    private static long? ReadProcessBytes(JsonElement row, SnapshotGroup group, string metricId)
    {
        if (group.Errors.Any(error => error.MetricId == metricId)) return null;
        var value = ProcessField(ProcessField(ProcessField(row, "metrics"), metricId), "value");
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null;
    }

    private static JsonElement ProcessField(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string Reason(string code) => code switch
    {
        StableErrorCodes.AccessDenied => "访问被拒绝", StableErrorCodes.ProcessExited => "进程已退出",
        StableErrorCodes.NotSupported => "当前设备或模式不支持", StableErrorCodes.Timeout => "采集超时",
        StableErrorCodes.InvalidData => "无效采样", StableErrorCodes.ResourceExhausted => "资源不足",
        StableErrorCodes.ProviderFailure => "采集器失败", "agent_connect_timeout" => "连接 Agent 超时",
        "agent_disconnected" => "Agent 已断开", "agent_io_failure" => "Agent 通信失败",
        _ => code,
    };

    private static bool SampleReady(SnapshotGroup group) =>
        group.Data is not JsonObject data || data["sampleReady"] is not JsonValue value ||
        !value.TryGetValue<bool>(out var ready) || ready;
}
