using System.Globalization;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public sealed record DiagnosticExplanation(string Phenomenon, string Evidence,
    string PossibleCauses, string Suggestions, string Limitation);

/// <summary>Explains existing evidence without evaluating rules or taking actions.</summary>
public static class DiagnosticExplainer
{
    public static DiagnosticExplanation Explain(DiagnosticEventContract diagnosticEvent,
        AgentSnapshot? currentSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var (phenomenon, causes, suggestions) = DescribeRule(diagnosticEvent.RuleId);
        var resolved = diagnosticEvent.State == DiagnosticStates.Resolved;
        phenomenon = resolved ? $"该证据窗口内已达到恢复条件；此前记录：{phenomenon}" : phenomenon;
        var evidence = DescribeEvidence(diagnosticEvent, resolved);
        var limitation = CurrentLimit(diagnosticEvent, currentSnapshot);
        if (diagnosticEvent.RuleId is DiagnosticRuleIds.HighCpu or DiagnosticRuleIds.ProcessCpuSpike)
            limitation += " 高占用可能是正常工作负载，应结合响应速度与正在进行的任务判断。";
        limitation += " 可能原因是排查方向；这些建议不会自动执行系统操作。";
        return new(phenomenon, evidence, causes, suggestions, limitation);
    }

    private static string DescribeEvidence(DiagnosticEventContract item, bool resolved)
    {
        var categorical = item.RuleId == DiagnosticRuleIds.ProviderUnavailable;
        var usable = item.Evidence.Where(evidence => categorical ||
            evidence.Value is { } value && double.IsFinite(value)).ToArray();
        if (usable.Length == 0)
            return "事件缺少可用的观测依据，无法确认数值或原因；请等待新的有效采集。";
        var latest = usable[^1];
        var signal = SignalName(latest.Signal);
        var value = latest.Value is { } number && double.IsFinite(number)
            ? $"{number.ToString("0.##", CultureInfo.InvariantCulture)} {latest.Unit}".TrimEnd()
            : resolved ? "恢复可用状态" : "不可用状态";
        var condition = resolved ? item.Hysteresis.RecoverWhen : item.Hysteresis.ActivateWhen;
        var seconds = resolved ? item.Debounce.RecoverSeconds : item.Debounce.ActivateSeconds;
        return $"证据窗口包含 {usable.Length} 个有效样本；最近 {signal}：{value}。" +
            $"规则条件：{condition}；持续门槛 {seconds.ToString("0.##", CultureInfo.InvariantCulture)} 秒。";
    }

    private static string CurrentLimit(DiagnosticEventContract item, AgentSnapshot? snapshot)
    {
        if (snapshot is null)
            return "说明仅依据事件当时的证据，当前观测状态尚未核对。";
        if (snapshot.InstanceId != item.InstanceId)
            return "这是其他 Agent 会话的历史事件，不能代表本次会话的当前状态。";
        var groupId = item.RuleId switch
        {
            DiagnosticRuleIds.HighCpu => GroupIds.SystemCpu,
            DiagnosticRuleIds.MemoryPressure => GroupIds.Memory,
            DiagnosticRuleIds.SystemDiskLow => GroupIds.Volumes,
            DiagnosticRuleIds.ProcessCpuSpike => GroupIds.Processes,
            DiagnosticRuleIds.SamplingGap => GroupIds.Sampler,
            DiagnosticRuleIds.AgentResourceAnomaly => GroupIds.Self,
            DiagnosticRuleIds.ProviderUnavailable when item.SubjectId.StartsWith("provider:", StringComparison.Ordinal)
                => item.SubjectId["provider:".Length..],
            _ => null,
        };
        if (groupId is null || !snapshot.Groups.TryGetValue(groupId, out var group))
            return "当前没有对应采集组，状态未知；历史事件不能证明持续异常或已经恢复。";
        if (group.CollectionState == "paused")
            return "当前采集已主动暂停，属于缺测；此事件仅提供历史依据。";
        if (group.ObservedAtUtc is null || group.Freshness != FreshnessStates.Fresh ||
            group.Availability is not (AvailabilityStates.Available or AvailabilityStates.Partial))
            return "当前观测缺测、过期或不可用，状态未知；不能当作低占用或已恢复。";
        return "事件描述当时的规则判断；当前有新鲜采集，仍应结合近期趋势确认变化。";
    }

    private static (string, string, string) DescribeRule(string ruleId) => ruleId switch
    {
        DiagnosticRuleIds.HighCpu => ("整机 CPU 占用持续达到策略阈值。",
            "正在运行的计算任务、多个应用同时工作，或某个进程持续忙碌。",
            "查看进程 CPU 排行并选中进程观察趋势；确认正在执行的任务，再关闭不需要的应用。"),
        DiagnosticRuleIds.MemoryPressure => ("内存占用持续达到策略阈值。",
            "多个应用、缓存或某个长期运行任务占用较多内存；单个读数不能确认泄漏。",
            "在系统任务管理器查看内存占用排行，结合本软件的进程详情观察变化；关闭不需要的应用前保留未保存内容。"),
        DiagnosticRuleIds.SystemDiskLow => ("系统卷已用空间比例持续达到策略阈值。",
            "下载、日志、安装文件或用户文件增加；需要查看实际目录才能确认来源。",
            "检查系统卷空间和大文件位置，整理已确认不需要的文件，或使用系统存储设置。"),
        DiagnosticRuleIds.ProcessCpuSpike => ("关注的同名进程实例出现持续高 CPU。",
            "该应用正在计算、编译或处理请求，也可能存在持续忙碌的任务。",
            "在进程排行确认 PID 与创建时间，选中实例查看 CPU 趋势和任务状态。"),
        DiagnosticRuleIds.SamplingGap => ("采集出现时间间隔过大的信号。",
            "系统繁忙、采集调用延迟或后台调度等待；缺测不能证明资源占用下降。",
            "查看采集状态和下一次有效观测；需要减少监控请求时可尝试本软件的会话轻量模式。"),
        DiagnosticRuleIds.ProviderUnavailable => ("某个采集来源持续不可用。",
            "权限限制、数据源暂不可用、硬件不支持或采集请求超时；事件无法单独确认原因。",
            "查看对应组的采集状态；若是 GPU/温度，可暂停该可选请求并恢复后观察。"),
        DiagnosticRuleIds.AgentResourceAnomaly => ("本软件的资源使用持续达到策略阈值。",
            "监控请求、序列化或后台任务较多，也可能是短期负载；需要结合趋势判断。",
            "尝试暂停本软件的 GPU/温度请求，观察后再恢复原采集设置；不承诺量化改善。"),
        _ => ("已有诊断事件，当前版本没有该规则的专门说明。",
            "原因未知；仅凭规则名称无法确认。", "查看事件证据和对应观测，等待明确依据后再处理。"),
    };

    private static string SignalName(string signal) => signal switch
    {
        MetricIds.SystemCpuUtilization => "整机 CPU",
        MetricIds.MemoryUtilization => "内存占用",
        MetricIds.VolumeUtilization => "系统卷已用比例",
        MetricIds.ProcessCpuNormalized => "进程 CPU",
        "provider.availability" => "采集来源",
        "agent.cpu_or_private_memory.ratio" => "Agent 相对策略阈值的比例",
        _ => signal,
    };
}
