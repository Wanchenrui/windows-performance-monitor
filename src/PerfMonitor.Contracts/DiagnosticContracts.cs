namespace PerfMonitor.Contracts;

public static class DiagnosticRuleIds
{
    public const string HighCpu = "system.high_cpu";
    public const string MemoryPressure = "system.memory_pressure";
    public const string SystemDiskLow = "system.disk_low";
    public const string ProcessCpuSpike = "process.cpu_spike";
    public const string SamplingGap = "agent.sampling_gap";
    public const string ProviderUnavailable = "agent.provider_unavailable";
    public const string AgentResourceAnomaly =
        "agent.resource_anomaly";

    public static IReadOnlyList<string> All { get; } =
    [
        AgentResourceAnomaly,
        ProviderUnavailable,
        SamplingGap,
        ProcessCpuSpike,
        HighCpu,
        SystemDiskLow,
        MemoryPressure,
    ];
}

public static class DiagnosticStates
{
    public const string Active = "active";
    public const string Resolved = "resolved";

    public static IReadOnlyList<string> All { get; } =
        [Active, Resolved];
}

public static class DiagnosticSeverities
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

public static class DiagnosticPolicyLimits
{
    public const int MaxEvents = 2_000;
    public const int DefaultMaxEvents = 200;
    public const int MaxRuleIds = 16;
    public static readonly TimeSpan MaxQueryRange =
        TimeSpan.FromDays(366);
}

public sealed record DiagnosticHysteresisContract
{
    public required string ActivateWhen { get; init; }
    public required string RecoverWhen { get; init; }
}

public sealed record DiagnosticDebounceContract
{
    public double ActivateSeconds { get; init; }
    public double RecoverSeconds { get; init; }
}

public sealed record DiagnosticEvidenceWindowContract
{
    public required DateTimeOffset FromUtc { get; init; }
    public required DateTimeOffset ToUtc { get; init; }
    public int SampleCount { get; init; }
}

public sealed record DiagnosticEvidenceContract
{
    public required DateTimeOffset ObservedAtUtc { get; init; }
    public required string SubjectId { get; init; }
    public required string Signal { get; init; }
    public double? Value { get; init; }
    public string? Unit { get; init; }
    public required string Condition { get; init; }
}

public sealed record DiagnosticEventContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string EventId { get; init; }
    public required string InstanceId { get; init; }
    public required string RuleId { get; init; }
    public required string RuleVersion { get; init; }
    public required string Severity { get; init; }
    public required string State { get; init; }
    public required string SubjectId { get; init; }
    public required DiagnosticHysteresisContract Hysteresis { get; init; }
    public required DiagnosticDebounceContract Debounce { get; init; }
    public double CooldownSeconds { get; init; }
    public required DiagnosticEvidenceWindowContract EvidenceWindow
    {
        get;
        init;
    }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public double Confidence { get; init; }
    public required IReadOnlyList<DiagnosticEvidenceContract> Evidence
    {
        get;
        init;
    }
}

public sealed record DiagnosticQueryContract
{
    public IReadOnlyList<string> RuleIds { get; init; } = [];
    public IReadOnlyList<string> States { get; init; } = [];
    public long? FromEpochMs { get; init; }
    public long? ToEpochMs { get; init; }
    public int MaxEvents { get; init; } =
        DiagnosticPolicyLimits.DefaultMaxEvents;
}

public sealed record DiagnosticsContract
{
    public required string ContractVersion { get; init; }
    public required string ProductVersion { get; init; }
    public required string InstanceId { get; init; }
    public required DiagnosticQueryContract Query { get; init; }
    public int EventCount { get; init; }
    public bool Truncated { get; init; }
    public required IReadOnlyList<DiagnosticEventContract> Events
    {
        get;
        init;
    }
}

public sealed record DiagnosticRuleCapabilityContract
{
    public required string RuleId { get; init; }
    public required string RuleVersion { get; init; }
    public required string Severity { get; init; }
    public required string ActivateWhen { get; init; }
    public required string RecoverWhen { get; init; }
}

public sealed record DiagnosticsCapabilityContract
{
    public int DefaultMaxEvents { get; init; }
    public int MaxEvents { get; init; }
    public bool ActionsSupported { get; init; }
    public required IReadOnlyList<DiagnosticRuleCapabilityContract> Rules
    {
        get;
        init;
    }
}

public static class DiagnosticQueryValidation
{
    private static readonly HashSet<string> KnownRuleIds =
        new(DiagnosticRuleIds.All, StringComparer.Ordinal);
    private static readonly HashSet<string> KnownStates =
        new(DiagnosticStates.All, StringComparer.Ordinal);

    public static ValidatedDiagnosticQuery Validate(
        DiagnosticQueryContract query)
    {
        if (query.FromEpochMs is null ||
            query.ToEpochMs is null ||
            query.FromEpochMs > query.ToEpochMs)
        {
            throw new ArgumentException(
                "diagnostic_range_invalid",
                nameof(query));
        }

        var range = checked(
            query.ToEpochMs.Value -
            query.FromEpochMs.Value + 1);
        if (range >
            DiagnosticPolicyLimits.MaxQueryRange.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "diagnostic_range_too_large");
        }

        if (query.MaxEvents <= 0 ||
            query.MaxEvents > DiagnosticPolicyLimits.MaxEvents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "diagnostic_max_events_invalid");
        }

        ValidateFilter(
            query.RuleIds,
            KnownRuleIds,
            DiagnosticPolicyLimits.MaxRuleIds,
            "diagnostic_rule_ids_invalid");
        ValidateFilter(
            query.States,
            KnownStates,
            DiagnosticStates.All.Count,
            "diagnostic_states_invalid");
        return new ValidatedDiagnosticQuery(
            query.FromEpochMs.Value,
            query.ToEpochMs.Value,
            query.MaxEvents,
            new HashSet<string>(
                query.RuleIds,
                StringComparer.Ordinal),
            new HashSet<string>(
                query.States,
                StringComparer.Ordinal));
    }

    private static void ValidateFilter(
        IReadOnlyList<string> values,
        IReadOnlySet<string> known,
        int maxCount,
        string errorCode)
    {
        if (values.Count > maxCount ||
            values.Distinct(StringComparer.Ordinal).Count() !=
                values.Count ||
            values.Any(value => !known.Contains(value)))
        {
            throw new ArgumentException(errorCode);
        }
    }
}

public sealed record ValidatedDiagnosticQuery(
    long FromEpochMs,
    long ToEpochMs,
    int MaxEvents,
    IReadOnlySet<string> RuleIds,
    IReadOnlySet<string> States)
{
    public bool Matches(DiagnosticEventContract diagnosticEvent)
    {
        var time = diagnosticEvent.LastSeenUtc
            .ToUnixTimeMilliseconds();
        return time >= FromEpochMs &&
            time <= ToEpochMs &&
            (RuleIds.Count == 0 ||
                RuleIds.Contains(diagnosticEvent.RuleId)) &&
            (States.Count == 0 ||
                States.Contains(diagnosticEvent.State));
    }
}
