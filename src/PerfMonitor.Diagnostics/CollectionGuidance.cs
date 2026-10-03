using System.Globalization;
using System.Text.Json;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public sealed record CollectionHelp(string Status, string Attempts, IReadOnlyList<string> Suggestions, string Limitation);

/// <summary>Explains recorded collection evidence; never retries or changes configuration.</summary>
public static class CollectionGuidance
{
    public static CollectionHelp Explain(string groupId, SnapshotGroup? group)
    {
        if (group is null)
            return new("当前快照未提供此采集组，尚无法判断原因", "没有该组的采集路径记录",
                ["先等待首个完整快照；再检查 Agent 的可选采集状态。"], "缺组不能证明未配置、权限不足或硬件不存在。");
        if (group.CollectionState == "paused")
            return new("本软件已暂停此可选采集组", "轻量模式暂停期间不发起新的采集请求",
                ["在轻量模式中选择‘恢复原设置’，再等待新观测。"], "暂停属于主动缺测，不是硬件故障；已开始的调用可能仍在结束。");

        var data = group.ReadOnlyData ?? default;
        var readout = Field(data, "readout");
        var primary = String(readout, "primary"); var primaryStatus = String(readout, "primaryStatus");
        var secondary = String(readout, "secondary"); var secondaryStatus = String(readout, "secondaryStatus");
        var attempts = new List<string>();
        if (primary is not null) attempts.Add($"主路径 {SourceName(primary)}：{RouteStatus(primaryStatus)}");
        else if (WorkerStatus(group.CollectionState) is { } worker) attempts.Add(worker);
        if (secondary is not null) attempts.Add($"备用 {SourceName(secondary)}：{RouteStatus(secondaryStatus)}");
        var retry = Field(readout, "retryAfterSeconds");
        if (retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var remaining) && double.IsFinite(remaining) && remaining > 0)
            attempts.Add("失败后冷却剩余约 " + remaining.ToString("0.#", CultureInfo.InvariantCulture) + " 秒；到期可再次尝试，不保证恢复");
        var recorded = attempts.Count > 0 ? string.Join("；", attempts) : "快照未记录采集路径，不能确认尝试了哪些方法";
        var reasons = group.Errors.Select(error => error.ErrorCode).Distinct(StringComparer.Ordinal).ToArray();
        var noBattery = groupId == GroupIds.Power && Field(data, "batteryPresent").ValueKind == JsonValueKind.False;
        var powerKnown = String(data, "powerSource") is "ac" or "battery";
        var status = noBattery ? powerKnown ? "无系统电池 · 电量与剩余时间不适用" : "无系统电池；供电状态仍未知"
            : WorkerStatus(group.CollectionState) ?? Availability(group.Availability);
        if (groupId == GroupIds.Gpu && HasGpuEngineLoad(data)) status = "GPU 引擎负载部分可用；原硬件路径：" + (WorkerStatus(group.CollectionState) ?? RouteStatus(primaryStatus));
        if (reasons.Length > 0) status += "；记录原因：" + string.Join(" / ", reasons.Select(Reason));
        if (group.Freshness != FreshnessStates.Fresh || group.ObservedAtUtc is null) status = "当前观测尚不可确认 · " + status;

        var suggestions = Suggestions(groupId, group, reasons, noBattery && powerKnown && reasons.Length == 0);
        var limitation = groupId switch
        {
            GroupIds.Power => "电源组描述供电与电池状态，不提供整机功耗瓦数；没有电池不需要修复。",
            GroupIds.Gpu when UsesGpuEngine(data) => "Windows GPU Engine 只提供已读取引擎中最忙引擎的聚合负载，不代表某块物理显卡，也不提供温度或完整设备信息。",
            GroupIds.Gpu => "负载、设备信息与温度可分别缺测；一个数据源不可读不能证明没有显卡。",
            GroupIds.Sensors => "温度当前只使用 LibreHardwareMonitor，未实现通用备用温度来源；部分设备不暴露传感器，更新驱动也不保证能读到。",
            _ => "说明依据这份快照的记录；未返回的指标保持缺测，不能视为 0。",
        };
        if (group.CollectionState?.StartsWith("worker_", StringComparison.Ordinal) == true)
            limitation += " Worker 的恢复仍受启动预算约束，不会无限重启。";
        if (group.Freshness != FreshnessStates.Fresh || group.ObservedAtUtc is null)
            limitation += " 陈旧或缺测记录不能证明当前已经恢复。";
        return new(status, recorded, suggestions, limitation);
    }

    private static IReadOnlyList<string> Suggestions(string groupId, SnapshotGroup group, string[] reasons, bool noBattery)
    {
        if (noBattery) return [];
        switch (group.CollectionState)
        {
            case "worker_missing":
                return ["确认 Agent 配置的 Worker 路径存在，且程序目录中的 Worker 与依赖完整；缺失时重新解压或修复同版本程序。"];
            case "worker_access_denied":
                return ["检查运行账户或安全策略是否阻止启动 Worker；在 Windows 安全中心的保护历史记录查看本软件是否被拦截。"];
            case "worker_backoff":
                return ["等待启动预算释放后的自动重试；若反复发生，检查 Agent/Worker 日志中的首个失败原因。"];
            case "worker_timeout":
            case "worker_invalid_data":
            case "worker_resource_limit":
            case "worker_failed":
                return ["观察下一轮受预算限制的自动恢复；若反复失败，检查 Agent/Worker 日志并确认二者来自同一版本。"];
        }
        var needsHelp = group.Availability != AvailabilityStates.Available || reasons.Length > 0 ||
            group.CollectionState is "worker_open_failed" or "hardware_unreported";
        if (!needsHelp) return [];
        var suggestions = new List<string>();
        if (reasons.Contains(StableErrorCodes.AccessDenied, StringComparer.Ordinal))
            suggestions.Add("检查采集账户与本机策略是否允许该数据源访问；先查看日志中的被拒操作，不能仅凭此错误判定硬件不支持。");
        switch (groupId)
        {
            case GroupIds.Power:
                suggestions.Add("打开 Windows 设置 → 系统 → 电源和电池（台式机可能显示‘电源’），核对供电与电池状态；若系统也异常，再检查设备管理器的电池条目。");
                break;
            case GroupIds.Gpu:
                suggestions.Add("打开任务管理器 → 性能 → GPU，核对系统能否显示负载；在设备管理器 → 显示适配器查看驱动状态，按设备厂商说明修复异常。");
                break;
            case GroupIds.Sensors:
                suggestions.Add("用设备厂商工具或 UEFI/BIOS 硬件监视页确认是否提供温度；若那里可读而本软件仍不可读，保留设备型号与 Worker 日志再排查。");
                break;
            default:
                suggestions.Add("等待下一次有效观测，并查看 Agent 日志里对应采集组的具体错误。");
                break;
        }
        return suggestions;
    }

    private static string? WorkerStatus(string? state) => state switch
    {
        "worker_missing" => "Worker 组件缺失；尚未执行硬件读取",
        "worker_access_denied" => "Worker 启动被拒；不能据此确认硬件权限",
        "worker_backoff" => "Worker 启动预算等待；本轮未重新启动",
        "worker_timeout" => "Worker 请求超时",
        "worker_invalid_data" => "Worker 返回数据无效",
        "worker_resource_limit" => "Worker 资源上限已触发",
        "worker_failed" => "Worker 通信或运行失败",
        "worker_open_failed" => "已尝试打开硬件数据源，但全局打开失败",
        "hardware_unreported" => "该数据源未返回目标传感器；不能证明没有硬件",
        _ => null,
    };

    private static string SourceName(string source) => source switch
    {
        SourceIds.Power => "Windows GetSystemPowerStatus",
        SourceIds.PowerBatteryState => "Windows SystemBatteryState",
        SourceIds.LibreHardwareMonitor => "LibreHardwareMonitor",
        SourceIds.WindowsGpuEngine => "Windows GPU Engine",
        "hardware_worker" => "硬件 Worker",
        _ => "快照记录的其他数据源",
    };
    private static string RouteStatus(string? status) => WorkerStatus(status) ?? (status switch
    {
        "unknown" => "读数未知", "no_load" => "未返回有效 GPU 负载", "warming_up" => "正在预热，尚无有效负载",
        null => "未记录结果", _ => Availability(status),
    });
    private static string Availability(string status) => status switch
    {
        AvailabilityStates.Available => "可用", AvailabilityStates.Partial => "部分可用",
        AvailabilityStates.Unavailable => "不可用", AvailabilityStates.PermissionDenied => "访问被拒",
        AvailabilityStates.NotSupported => "数据源报告不支持", AvailabilityStates.Timeout => "采集超时",
        AvailabilityStates.Error => "采集失败", _ => Reason(status),
    };
    private static string Reason(string code) => code switch
    {
        StableErrorCodes.AccessDenied => "访问被拒", StableErrorCodes.NotSupported => "数据源不支持",
        StableErrorCodes.Timeout => "采集超时", StableErrorCodes.InvalidData => "无效数据",
        StableErrorCodes.ResourceExhausted => "资源不足", StableErrorCodes.ProviderFailure => "采集器失败",
        _ => "未识别原因",
    };
    private static bool UsesGpuEngine(JsonElement data) => String(Field(Field(data, "metrics"), MetricIds.GpuLoadMaxPercent), "sourceId") == SourceIds.WindowsGpuEngine;
    private static bool HasGpuEngineLoad(JsonElement data)
    {
        var value = Field(Field(Field(data, "metrics"), MetricIds.GpuLoadMaxPercent), "value");
        return UsesGpuEngine(data) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 100;
    }
    private static JsonElement Field(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    private static string? String(JsonElement parent, string name) => Field(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
