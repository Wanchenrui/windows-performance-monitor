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

    public static async Task<IReadOnlyList<DiagnosticEventContract>>
        ReplayAsync(
            IAsyncEnumerable<AgentSnapshot> orderedSnapshots,
            DiagnosticPolicy policy,
            CancellationToken cancellationToken = default)
    {
        var evaluator = new DiagnosticEvaluator(policy);
        var events = new List<DiagnosticEventContract>();
        DateTimeOffset? previousTime = null;
        string? previousInstanceId = null;
        long previousSequence = 0;
        await foreach (var snapshot in orderedSnapshots
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (snapshot.CompletedAtUtc is null)
            {
                continue;
            }

            if (previousTime is { } time &&
                (snapshot.CompletedAtUtc < time ||
                    snapshot.CompletedAtUtc == time &&
                    StringComparer.Ordinal.Compare(
                        snapshot.InstanceId,
                        previousInstanceId) < 0 ||
                    snapshot.CompletedAtUtc == time &&
                    StringComparer.Ordinal.Equals(
                        snapshot.InstanceId,
                        previousInstanceId) &&
                    snapshot.Sequence < previousSequence))
            {
                throw new InvalidDataException(
                    "diagnostic_replay_order_invalid");
            }

            previousTime = snapshot.CompletedAtUtc;
            previousInstanceId = snapshot.InstanceId;
            previousSequence = snapshot.Sequence;
            events.AddRange(evaluator.Evaluate(snapshot));
        }

        return events;
    }
}
