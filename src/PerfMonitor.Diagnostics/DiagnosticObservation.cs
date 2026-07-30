using PerfMonitor.Contracts;

namespace PerfMonitor.Diagnostics;

internal sealed record DiagnosticObservation(
    string InstanceId,
    string RuleId,
    string RuleVersion,
    string Severity,
    string SubjectId,
    string Signal,
    DateTimeOffset ObservedAtUtc,
    double? Value,
    string? Unit,
    bool IsBreach,
    bool IsRecovery,
    string ActivateWhen,
    string RecoverWhen,
    double ActivateDebounceSeconds,
    double RecoverDebounceSeconds,
    double CooldownSeconds,
    double EvidenceWindowSeconds,
    double MaxObservationGapSeconds)
{
    public DiagnosticEvidenceContract ToEvidence() =>
        new()
        {
            ObservedAtUtc = ObservedAtUtc,
            SubjectId = SubjectId,
            Signal = Signal,
            Value = Value,
            Unit = Unit,
            Condition = IsBreach
                ? "breach"
                : IsRecovery
                    ? "recovery"
                    : "hysteresis_band",
        };
}

internal readonly record struct DiagnosticRuleKey(
    string InstanceId,
    string RuleId,
    string RuleVersion,
    string SubjectId);
