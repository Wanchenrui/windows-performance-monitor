namespace PerfMonitor.Contracts;

public static class AdaptiveSchedulingPreferences
{
    public const string Responsiveness = "responsiveness";
    public const string Throughput = "throughput";
    public const string EnergySaving = "energy_saving";

    public static IReadOnlyList<string> All { get; } =
        [Responsiveness, Throughput, EnergySaving];
}

public static class AdaptiveSchedulingDecisionCodes
{
    public const string Disabled = "disabled";
    public const string Observing = "observing";
    public const string Debouncing = "debouncing";
    public const string Reduced = "reduced";
    public const string Recovering = "recovering";
    public const string InsufficientData = "insufficient_data";
    public const string ManualPaused = "manual_paused";
    public const string Expired = "expired";
    public const string Restored = "restored";
    public const string Conflict = "conflict";
    public const string NoHardware = "no_hardware";
}

public static class AdaptiveSchedulingLimits
{
    public const int DefaultDurationSeconds = 1800;
    public static IReadOnlyList<int> DurationSeconds { get; } = [900, 1800, 3600];
}

/// <summary>A bounded, current-Agent-session request affecting only optional collection.</summary>
public sealed record AdaptiveSchedulingRequestContract
{
    public required string InstanceId { get; init; }
    public required string Preference { get; init; }
    public bool Enabled { get; init; }
    public int DurationSeconds { get; init; } = AdaptiveSchedulingLimits.DefaultDurationSeconds;
}

public sealed record AdaptiveSchedulingGroupContract
{
    public required string GroupId { get; init; }
    public int BasePeriodMs { get; init; }
    public int EffectivePeriodMs { get; init; }
    public bool Paused { get; init; }
}

/// <summary>Reports actual collection settings and evidence, without claiming task performance gains.</summary>
public sealed record AdaptiveSchedulingContract
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public bool CanRestore { get; init; }
    public bool SessionOnly { get; init; } = true;
    public required string InstanceId { get; init; }
    public required string Preference { get; init; }
    public required string DecisionCode { get; init; }
    public required string Reason { get; init; }
    public required string ImpactDescription { get; init; }
    public double? RemainingSeconds { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public IReadOnlyList<AdaptiveSchedulingGroupContract> Groups { get; init; } = [];
    public IReadOnlyList<string> Recommendations { get; init; } = [];
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public string? PowerSource { get; init; }
    public bool? BatterySaver { get; init; }

    public static AdaptiveSchedulingContract Unsupported(string instanceId) => new()
    {
        InstanceId = instanceId,
        Preference = AdaptiveSchedulingPreferences.Responsiveness,
        DecisionCode = AdaptiveSchedulingDecisionCodes.Disabled,
        Reason = "当前 Agent 不支持智能采集调度。",
        ImpactDescription = "此功能仅控制本软件的 GPU / 温度请求周期。",
    };
}
