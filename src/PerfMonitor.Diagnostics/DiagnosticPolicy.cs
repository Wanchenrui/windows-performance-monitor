using System.Text.Json;
using PerfMonitor.Contracts;

namespace PerfMonitor.Diagnostics;

public sealed record ThresholdRulePolicy
{
    public bool Enabled { get; init; } = true;
    public required string RuleVersion { get; init; }
    public required string Severity { get; init; }
    public double ActivateAtOrAbove { get; init; }
    public double RecoverAtOrBelow { get; init; }
    public double ActivateDebounceSeconds { get; init; }
    public double RecoverDebounceSeconds { get; init; }
    public double CooldownSeconds { get; init; }
    public double EvidenceWindowSeconds { get; init; }
    public double MaxObservationGapSeconds { get; init; }
}

public sealed record StateRulePolicy
{
    public bool Enabled { get; init; } = true;
    public required string RuleVersion { get; init; }
    public required string Severity { get; init; }
    public double ActivateDebounceSeconds { get; init; }
    public double RecoverDebounceSeconds { get; init; }
    public double CooldownSeconds { get; init; }
    public double EvidenceWindowSeconds { get; init; }
    public double MaxObservationGapSeconds { get; init; }
}

public sealed record AgentResourceRulePolicy
{
    public bool Enabled { get; init; } = true;
    public required string RuleVersion { get; init; }
    public required string Severity { get; init; }
    public double ActivateCpuCoreEquivalentPercent { get; init; }
    public double RecoverCpuCoreEquivalentPercent { get; init; }
    public long ActivatePrivateBytes { get; init; }
    public long RecoverPrivateBytes { get; init; }
    public double ActivateDebounceSeconds { get; init; }
    public double RecoverDebounceSeconds { get; init; }
    public double CooldownSeconds { get; init; }
    public double EvidenceWindowSeconds { get; init; }
    public double MaxObservationGapSeconds { get; init; }
}

public sealed record DiagnosticPolicy
{
    public int SchemaVersion { get; init; } = 1;
    public required ThresholdRulePolicy HighCpu { get; init; }
    public required ThresholdRulePolicy MemoryPressure { get; init; }
    public required ThresholdRulePolicy SystemDiskLow { get; init; }
    public required ThresholdRulePolicy ProcessCpuSpike { get; init; }
    public required ThresholdRulePolicy SamplingGap { get; init; }
    public required StateRulePolicy ProviderUnavailable { get; init; }
    public required AgentResourceRulePolicy AgentResourceAnomaly
    {
        get;
        init;
    }
    public IReadOnlyList<string> WatchedProcessNames { get; init; } = [];

    public static DiagnosticPolicy Default { get; } = CreateDefault();

    public DiagnosticPolicy Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "diagnostic_policy_schema_unsupported");
        }

        ValidateThreshold(HighCpu, nameof(HighCpu));
        ValidateThreshold(MemoryPressure, nameof(MemoryPressure));
        ValidateThreshold(SystemDiskLow, nameof(SystemDiskLow));
        ValidateThreshold(ProcessCpuSpike, nameof(ProcessCpuSpike));
        ValidateThreshold(SamplingGap, nameof(SamplingGap));
        ValidateState(ProviderUnavailable, nameof(ProviderUnavailable));
        ValidateAgentResource(AgentResourceAnomaly);

        if (WatchedProcessNames.Count > 32)
        {
            throw new InvalidDataException(
                "diagnostic_policy_process_limit");
        }

        var names = WatchedProcessNames
            .Select(ValidateProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length != WatchedProcessNames.Count)
        {
            throw new InvalidDataException(
                "diagnostic_policy_process_duplicate");
        }

        return this with
        {
            WatchedProcessNames = names,
        };
    }

    public DiagnosticsCapabilityContract ToCapabilities()
    {
        var rules = new List<DiagnosticRuleCapabilityContract>(7);
        AddThresholdCapability(
            rules,
            DiagnosticRuleIds.HighCpu,
            HighCpu,
            MetricIds.SystemCpuUtilization);
        AddThresholdCapability(
            rules,
            DiagnosticRuleIds.MemoryPressure,
            MemoryPressure,
            MetricIds.MemoryUtilization);
        AddThresholdCapability(
            rules,
            DiagnosticRuleIds.SystemDiskLow,
            SystemDiskLow,
            MetricIds.VolumeUtilization);
        AddThresholdCapability(
            rules,
            DiagnosticRuleIds.ProcessCpuSpike,
            ProcessCpuSpike,
            MetricIds.ProcessCpuNormalized);
        AddThresholdCapability(
            rules,
            DiagnosticRuleIds.SamplingGap,
            SamplingGap,
            "snapshot.completed_at.gap_seconds");
        if (ProviderUnavailable.Enabled)
        {
            rules.Add(new DiagnosticRuleCapabilityContract
            {
                RuleId = DiagnosticRuleIds.ProviderUnavailable,
                RuleVersion = ProviderUnavailable.RuleVersion,
                Severity = ProviderUnavailable.Severity,
                ActivateWhen = "provider remains unavailable",
                RecoverWhen = "provider remains available",
            });
        }

        if (AgentResourceAnomaly.Enabled)
        {
            rules.Add(new DiagnosticRuleCapabilityContract
            {
                RuleId = DiagnosticRuleIds.AgentResourceAnomaly,
                RuleVersion = AgentResourceAnomaly.RuleVersion,
                Severity = AgentResourceAnomaly.Severity,
                ActivateWhen =
                    $"cpu >= {AgentResourceAnomaly.ActivateCpuCoreEquivalentPercent:R}% OR private >= {AgentResourceAnomaly.ActivatePrivateBytes} B",
                RecoverWhen =
                    $"cpu <= {AgentResourceAnomaly.RecoverCpuCoreEquivalentPercent:R}% AND private <= {AgentResourceAnomaly.RecoverPrivateBytes} B",
            });
        }

        rules.Sort(
            static (left, right) => StringComparer.Ordinal.Compare(
                left.RuleId,
                right.RuleId));
        return new DiagnosticsCapabilityContract
        {
            DefaultMaxEvents =
                DiagnosticPolicyLimits.DefaultMaxEvents,
            MaxEvents = DiagnosticPolicyLimits.MaxEvents,
            ActionsSupported = false,
            Rules = rules,
        };
    }

    private static DiagnosticPolicy CreateDefault() =>
        new()
        {
            HighCpu = Threshold(
                DiagnosticSeverities.Warning,
                activate: 90,
                recover: 75,
                activateSeconds: 30,
                recoverSeconds: 30,
                cooldownSeconds: 300,
                evidenceSeconds: 60,
                maxGapSeconds: 5),
            MemoryPressure = Threshold(
                DiagnosticSeverities.Warning,
                activate: 90,
                recover: 80,
                activateSeconds: 30,
                recoverSeconds: 30,
                cooldownSeconds: 300,
                evidenceSeconds: 60,
                maxGapSeconds: 5),
            SystemDiskLow = Threshold(
                DiagnosticSeverities.Warning,
                activate: 90,
                recover: 85,
                activateSeconds: 60,
                recoverSeconds: 60,
                cooldownSeconds: 900,
                evidenceSeconds: 180,
                maxGapSeconds: 95),
            ProcessCpuSpike = Threshold(
                DiagnosticSeverities.Warning,
                activate: 80,
                recover: 50,
                activateSeconds: 20,
                recoverSeconds: 20,
                cooldownSeconds: 300,
                evidenceSeconds: 60,
                maxGapSeconds: 8),
            SamplingGap = Threshold(
                DiagnosticSeverities.Warning,
                activate: 5,
                recover: 2,
                activateSeconds: 0,
                recoverSeconds: 6,
                cooldownSeconds: 120,
                evidenceSeconds: 60,
                maxGapSeconds: 120),
            ProviderUnavailable = new StateRulePolicy
            {
                RuleVersion = "1.0.0",
                Severity = DiagnosticSeverities.Warning,
                ActivateDebounceSeconds = 120,
                RecoverDebounceSeconds = 30,
                CooldownSeconds = 300,
                EvidenceWindowSeconds = 180,
                MaxObservationGapSeconds = 95,
            },
            AgentResourceAnomaly = new AgentResourceRulePolicy
            {
                RuleVersion = "1.0.0",
                Severity = DiagnosticSeverities.Warning,
                ActivateCpuCoreEquivalentPercent = 20,
                RecoverCpuCoreEquivalentPercent = 10,
                ActivatePrivateBytes = 256L * 1024 * 1024,
                RecoverPrivateBytes = 192L * 1024 * 1024,
                ActivateDebounceSeconds = 60,
                RecoverDebounceSeconds = 60,
                CooldownSeconds = 300,
                EvidenceWindowSeconds = 120,
                MaxObservationGapSeconds = 5,
            },
            WatchedProcessNames = [],
        };

    private static ThresholdRulePolicy Threshold(
        string severity,
        double activate,
        double recover,
        double activateSeconds,
        double recoverSeconds,
        double cooldownSeconds,
        double evidenceSeconds,
        double maxGapSeconds) =>
        new()
        {
            RuleVersion = "1.0.0",
            Severity = severity,
            ActivateAtOrAbove = activate,
            RecoverAtOrBelow = recover,
            ActivateDebounceSeconds = activateSeconds,
            RecoverDebounceSeconds = recoverSeconds,
            CooldownSeconds = cooldownSeconds,
            EvidenceWindowSeconds = evidenceSeconds,
            MaxObservationGapSeconds = maxGapSeconds,
        };

    private static void AddThresholdCapability(
        ICollection<DiagnosticRuleCapabilityContract> destination,
        string ruleId,
        ThresholdRulePolicy policy,
        string signal)
    {
        if (!policy.Enabled)
        {
            return;
        }

        destination.Add(new DiagnosticRuleCapabilityContract
        {
            RuleId = ruleId,
            RuleVersion = policy.RuleVersion,
            Severity = policy.Severity,
            ActivateWhen =
                $"{signal} >= {policy.ActivateAtOrAbove:R}",
            RecoverWhen =
                $"{signal} <= {policy.RecoverAtOrBelow:R}",
        });
    }

    private static void ValidateThreshold(
        ThresholdRulePolicy policy,
        string name)
    {
        ValidateMetadata(policy.RuleVersion, policy.Severity, name);
        if (!double.IsFinite(policy.ActivateAtOrAbove) ||
            !double.IsFinite(policy.RecoverAtOrBelow) ||
            policy.ActivateAtOrAbove <= policy.RecoverAtOrBelow)
        {
            throw new InvalidDataException(
                $"diagnostic_policy_threshold_invalid:{name}");
        }

        ValidateTiming(
            policy.ActivateDebounceSeconds,
            policy.RecoverDebounceSeconds,
            policy.CooldownSeconds,
            policy.EvidenceWindowSeconds,
            policy.MaxObservationGapSeconds,
            name);
    }

    private static void ValidateState(
        StateRulePolicy policy,
        string name)
    {
        ValidateMetadata(policy.RuleVersion, policy.Severity, name);
        ValidateTiming(
            policy.ActivateDebounceSeconds,
            policy.RecoverDebounceSeconds,
            policy.CooldownSeconds,
            policy.EvidenceWindowSeconds,
            policy.MaxObservationGapSeconds,
            name);
    }

    private static void ValidateAgentResource(
        AgentResourceRulePolicy policy)
    {
        const string name = nameof(AgentResourceAnomaly);
        ValidateMetadata(policy.RuleVersion, policy.Severity, name);
        if (!double.IsFinite(
                policy.ActivateCpuCoreEquivalentPercent) ||
            !double.IsFinite(
                policy.RecoverCpuCoreEquivalentPercent) ||
            policy.ActivateCpuCoreEquivalentPercent <=
                policy.RecoverCpuCoreEquivalentPercent ||
            policy.RecoverCpuCoreEquivalentPercent < 0 ||
            policy.ActivatePrivateBytes <=
                policy.RecoverPrivateBytes ||
            policy.RecoverPrivateBytes < 0)
        {
            throw new InvalidDataException(
                "diagnostic_policy_agent_resource_invalid");
        }

        ValidateTiming(
            policy.ActivateDebounceSeconds,
            policy.RecoverDebounceSeconds,
            policy.CooldownSeconds,
            policy.EvidenceWindowSeconds,
            policy.MaxObservationGapSeconds,
            name);
    }

    private static void ValidateMetadata(
        string ruleVersion,
        string severity,
        string name)
    {
        if (string.IsNullOrWhiteSpace(ruleVersion) ||
            ruleVersion.Length > 32 ||
            severity is not (
                DiagnosticSeverities.Info or
                DiagnosticSeverities.Warning or
                DiagnosticSeverities.Critical))
        {
            throw new InvalidDataException(
                $"diagnostic_policy_metadata_invalid:{name}");
        }
    }

    private static void ValidateTiming(
        double activate,
        double recover,
        double cooldown,
        double evidence,
        double maxGap,
        string name)
    {
        double[] values =
            [activate, recover, cooldown, evidence, maxGap];
        if (values.Any(
                value => !double.IsFinite(value) ||
                    value < 0 ||
                    value > TimeSpan.FromDays(7).TotalSeconds) ||
            evidence <= 0 ||
            maxGap <= 0)
        {
            throw new InvalidDataException(
                $"diagnostic_policy_timing_invalid:{name}");
        }
    }

    private static string ValidateProcessName(string value)
    {
        var name = value.Trim();
        if (name.Length is 0 or > 128 ||
            name.Any(char.IsControl) ||
            name.IndexOfAny(
                [Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar,
                    Path.VolumeSeparatorChar]) >= 0)
        {
            throw new InvalidDataException(
                "diagnostic_policy_process_name_invalid");
        }

        return name;
    }
}

public static class DiagnosticPolicyFile
{
    private const int MaxPolicyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        ContractJson.Options)
    {
        WriteIndented = true,
    };

    public static DiagnosticPolicy LoadOrCreate(
        string path,
        out string? warningCode)
    {
        warningCode = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(fullPath))
            {
                var defaultPolicy = DiagnosticPolicy.Default.Validate();
                File.WriteAllText(
                    fullPath,
                    JsonSerializer.Serialize(defaultPolicy, JsonOptions));
                return defaultPolicy;
            }

            var info = new FileInfo(fullPath);
            if (info.Length is <= 0 or > MaxPolicyBytes)
            {
                throw new InvalidDataException(
                    "diagnostic_policy_size_invalid");
            }

            var json = File.ReadAllText(fullPath);
            return (JsonSerializer.Deserialize<DiagnosticPolicy>(
                    json,
                    JsonOptions) ??
                throw new InvalidDataException(
                    "diagnostic_policy_empty"))
                .Validate();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException or
            ArgumentException or
            NotSupportedException)
        {
            // v0.6.0: diagnostics configuration is deliberately
            // fail-open for sampling. The fallback lives in RAM and the
            // invalid persistent file is never overwritten.
            warningCode = "diagnostic_policy_fallback";
            return DiagnosticPolicy.Default.Validate();
        }
    }
}
