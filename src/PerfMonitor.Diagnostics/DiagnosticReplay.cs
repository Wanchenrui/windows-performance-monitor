using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public static class DiagnosticReplay
{
    public static IReadOnlyList<DiagnosticEventContract> Replay(
        IEnumerable<AgentSnapshot> snapshots,
        DiagnosticPolicy policy)
    {
        var evaluator = new DiagnosticEvaluator(policy);
        var events = new List<DiagnosticEventContract>();
        foreach (var snapshot in snapshots
            .Where(static snapshot =>
                snapshot.CompletedAtUtc is not null)
            .OrderBy(static snapshot => snapshot.CompletedAtUtc)
            .ThenBy(
                static snapshot => snapshot.InstanceId,
                StringComparer.Ordinal)
            .ThenBy(static snapshot => snapshot.Sequence))
        {
            events.AddRange(evaluator.Evaluate(snapshot));
        }

        return events;
    }
}
