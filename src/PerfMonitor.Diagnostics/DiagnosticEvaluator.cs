using System.Security.Cryptography;
using System.Text;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public sealed class DiagnosticEvaluator
{
    private const int MaxEvidenceSamplesPerRule = 256;
    private readonly DiagnosticSnapshotReader _reader;
    private readonly Dictionary<DiagnosticRuleKey, RuleState> _states = [];

    public DiagnosticEvaluator(DiagnosticPolicy policy)
    {
        Policy = policy.Validate();
        _reader = new DiagnosticSnapshotReader(Policy);
    }

    public DiagnosticPolicy Policy { get; }

    public IReadOnlyList<DiagnosticEventContract> Evaluate(
        AgentSnapshot snapshot)
    {
        var events = new List<DiagnosticEventContract>();
        foreach (var observation in _reader.Read(snapshot))
        {
            var key = new DiagnosticRuleKey(
                observation.InstanceId,
                observation.RuleId,
                observation.RuleVersion,
                observation.SubjectId);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new RuleState();
                _states.Add(key, state);
            }

            var diagnosticEvent = EvaluateObservation(
                observation,
                state);
            if (diagnosticEvent is not null)
            {
                events.Add(diagnosticEvent);
            }
        }

        events.Sort(
            static (left, right) =>
            {
                var result = left.LastSeenUtc.CompareTo(
                    right.LastSeenUtc);
                if (result != 0)
                {
                    return result;
                }

                result = StringComparer.Ordinal.Compare(
                    left.RuleId,
                    right.RuleId);
                if (result != 0)
                {
                    return result;
                }

                result = StringComparer.Ordinal.Compare(
                    left.SubjectId,
                    right.SubjectId);
                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(
                        left.State,
                        right.State);
            });
        return events;
    }

    private static DiagnosticEventContract? EvaluateObservation(
        DiagnosticObservation observation,
        RuleState state)
    {
        if (state.LastObservedAtUtc is { } last &&
            observation.ObservedAtUtc <= last)
        {
            return null;
        }

        if (state.LastObservedAtUtc is { } previous &&
            observation.ObservedAtUtc - previous >
                TimeSpan.FromSeconds(
                    observation.MaxObservationGapSeconds))
        {
            state.BreachSinceUtc = null;
            state.RecoverySinceUtc = null;
            state.Evidence.Clear();
        }

        state.LastObservedAtUtc = observation.ObservedAtUtc;
        AppendEvidence(observation, state);

        if (!state.Active)
        {
            state.RecoverySinceUtc = null;
            if (!observation.IsBreach)
            {
                state.BreachSinceUtc = null;
                return null;
            }

            state.BreachSinceUtc ??= observation.ObservedAtUtc;
            if (observation.ObservedAtUtc -
                    state.BreachSinceUtc.Value <
                TimeSpan.FromSeconds(
                    observation.ActivateDebounceSeconds))
            {
                return null;
            }

            state.Active = true;
            state.EpisodeFirstSeenUtc = state.BreachSinceUtc.Value;
            state.BreachSinceUtc = null;
            state.EpisodeReported =
                state.LastActiveEventUtc is null ||
                observation.ObservedAtUtc -
                    state.LastActiveEventUtc.Value >=
                TimeSpan.FromSeconds(observation.CooldownSeconds);
            if (!state.EpisodeReported)
            {
                return null;
            }

            state.LastActiveEventUtc = observation.ObservedAtUtc;
            return BuildEvent(
                observation,
                state,
                DiagnosticStates.Active,
                state.EpisodeFirstSeenUtc.Value,
                observation.ActivateDebounceSeconds);
        }

        if (!observation.IsRecovery)
        {
            state.RecoverySinceUtc = null;
            return null;
        }

        state.RecoverySinceUtc ??= observation.ObservedAtUtc;
        if (observation.ObservedAtUtc -
                state.RecoverySinceUtc.Value <
            TimeSpan.FromSeconds(
                observation.RecoverDebounceSeconds))
        {
            return null;
        }

        var firstSeen = state.EpisodeFirstSeenUtc ??
            observation.ObservedAtUtc;
        var reported = state.EpisodeReported;
        state.Active = false;
        state.RecoverySinceUtc = null;
        state.EpisodeFirstSeenUtc = null;
        state.EpisodeReported = false;
        return reported
            ? BuildEvent(
                observation,
                state,
                DiagnosticStates.Resolved,
                firstSeen,
                observation.RecoverDebounceSeconds)
            : null;
    }

    private static void AppendEvidence(
        DiagnosticObservation observation,
        RuleState state)
    {
        state.Evidence.Enqueue(observation.ToEvidence());
        var cutoff = observation.ObservedAtUtc -
            TimeSpan.FromSeconds(observation.EvidenceWindowSeconds);
        while (state.Evidence.Count > 0 &&
            (state.Evidence.Peek().ObservedAtUtc < cutoff ||
                state.Evidence.Count > MaxEvidenceSamplesPerRule))
        {
            _ = state.Evidence.Dequeue();
        }
    }

    private static DiagnosticEventContract BuildEvent(
        DiagnosticObservation observation,
        RuleState state,
        string eventState,
        DateTimeOffset firstSeenUtc,
        double requiredDurationSeconds)
    {
        var evidence = state.Evidence.ToArray();
        var windowFrom = evidence.Length == 0
            ? observation.ObservedAtUtc
            : evidence[0].ObservedAtUtc;
        var confidence = requiredDurationSeconds <= 0
            ? 1
            : Math.Clamp(
                (observation.ObservedAtUtc - (
                    eventState == DiagnosticStates.Active
                        ? firstSeenUtc
                        : state.RecoverySinceUtc ??
                            observation.ObservedAtUtc))
                    .TotalSeconds /
                requiredDurationSeconds,
                0,
                1);
        // State transitions are emitted only after their full debounce
        // interval, so a transition is deterministically fully confident.
        confidence = Math.Max(confidence, 1);

        return new DiagnosticEventContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            EventId = CreateEventId(
                observation,
                eventState,
                firstSeenUtc),
            InstanceId = observation.InstanceId,
            RuleId = observation.RuleId,
            RuleVersion = observation.RuleVersion,
            Severity = observation.Severity,
            State = eventState,
            SubjectId = observation.SubjectId,
            Hysteresis = new DiagnosticHysteresisContract
            {
                ActivateWhen = observation.ActivateWhen,
                RecoverWhen = observation.RecoverWhen,
            },
            Debounce = new DiagnosticDebounceContract
            {
                ActivateSeconds =
                    observation.ActivateDebounceSeconds,
                RecoverSeconds =
                    observation.RecoverDebounceSeconds,
            },
            CooldownSeconds = observation.CooldownSeconds,
            EvidenceWindow = new DiagnosticEvidenceWindowContract
            {
                FromUtc = windowFrom,
                ToUtc = observation.ObservedAtUtc,
                SampleCount = evidence.Length,
            },
            FirstSeenUtc = firstSeenUtc,
            LastSeenUtc = observation.ObservedAtUtc,
            Confidence = confidence,
            Evidence = evidence,
        };
    }

    private static string CreateEventId(
        DiagnosticObservation observation,
        string eventState,
        DateTimeOffset firstSeenUtc)
    {
        var material = string.Join(
            '\n',
            observation.InstanceId,
            observation.RuleId,
            observation.RuleVersion,
            observation.SubjectId,
            eventState,
            firstSeenUtc.UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            observation.ObservedAtUtc.UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed class RuleState
    {
        public bool Active { get; set; }
        public bool EpisodeReported { get; set; }
        public DateTimeOffset? LastObservedAtUtc { get; set; }
        public DateTimeOffset? BreachSinceUtc { get; set; }
        public DateTimeOffset? RecoverySinceUtc { get; set; }
        public DateTimeOffset? EpisodeFirstSeenUtc { get; set; }
        public DateTimeOffset? LastActiveEventUtc { get; set; }
        public Queue<DiagnosticEvidenceContract> Evidence { get; } = new();
    }
}
