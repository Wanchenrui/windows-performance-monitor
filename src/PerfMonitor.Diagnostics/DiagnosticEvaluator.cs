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
    private string? _instanceId;
    private long _lastSequence;
    private long? _lastDeliverySequence;
    private double? _lastElapsedSeconds;

    public DiagnosticEvaluator(DiagnosticPolicy policy)
    {
        Policy = policy.Validate();
        _reader = new DiagnosticSnapshotReader(Policy);
    }

    public DiagnosticPolicy Policy { get; }

    public IReadOnlyList<DiagnosticEventContract> Evaluate(AgentSnapshot snapshot)
    {
        if (!StringComparer.Ordinal.Equals(_instanceId, snapshot.InstanceId))
        {
            // Process restarts cannot inherit pending evidence or cooldown.
            _states.Clear();
            _reader.Reset();
            _instanceId = snapshot.InstanceId;
            _lastSequence = 0;
            _lastDeliverySequence = null;
            _lastElapsedSeconds = null;
        }

        if (snapshot.Sequence < _lastSequence ||
            snapshot.DeliverySequence is { } delivered &&
                _lastDeliverySequence is { } previousDelivery && delivered <= previousDelivery ||
            snapshot.Sequence == _lastSequence && snapshot.DeliverySequence is null)
        {
            return [];
        }

        if (snapshot.ElapsedSeconds is not { } elapsed ||
            !double.IsFinite(elapsed) || elapsed < 0 ||
            snapshot.CompletedAtUtc is null)
        {
            BreakAllContinuity();
            _reader.Reset();
            _lastSequence = snapshot.Sequence;
            _lastDeliverySequence = snapshot.DeliverySequence;
            _lastElapsedSeconds = null;
            return [];
        }

        if (_lastElapsedSeconds is { } previous && elapsed < previous)
        {
            // A monotonic clock regression is an invalid epoch, not recovery.
            _states.Clear();
            _reader.Reset();
        }
        else if (snapshot.DeliverySequence is { } delivery &&
            _lastDeliverySequence is { } lastDelivery && delivery > lastDelivery + 1)
        {
            // Queue loss is a gap even when the next observed duration is short.
            BreakAllContinuity();
        }

        _lastSequence = snapshot.Sequence;
        _lastDeliverySequence = snapshot.DeliverySequence;
        _lastElapsedSeconds = elapsed;
        var events = new List<DiagnosticEventContract>();
        var present = new HashSet<DiagnosticRuleKey>();
        foreach (var observation in _reader.Read(snapshot))
        {
            var key = new DiagnosticRuleKey(observation.InstanceId,
                observation.RuleId, observation.RuleVersion, observation.SubjectId);
            present.Add(key);
            if (!_states.TryGetValue(key, out var state) ||
                observation.ContinuityId is { } identity &&
                    !StringComparer.Ordinal.Equals(identity, state.ContinuityId))
            {
                // The public subject remains process:name. Its contributing
                // instance set must nevertheless start a new evidence episode.
                state = new RuleState { ContinuityId = observation.ContinuityId };
                _states[key] = state;
            }

            if (!observation.IsKnown)
            {
                state.BreakContinuity();
                state.LastObservationSequence = Math.Max(
                    state.LastObservationSequence, observation.ObservationSequence);
                continue;
            }

            var diagnosticEvent = EvaluateObservation(observation, state);
            if (diagnosticEvent is not null)
            {
                events.Add(diagnosticEvent);
            }
        }

        foreach (var entry in _states)
        {
            if (!present.Contains(entry.Key))
            {
                // Missing metrics, warmup, stale groups and absent dependencies
                // never establish low values or bridge a debounce interval.
                entry.Value.BreakContinuity();
            }
        }

        return events;
    }

    private void BreakAllContinuity()
    {
        foreach (var state in _states.Values)
        {
            state.BreakContinuity();
        }
    }

    private static DiagnosticEventContract? EvaluateObservation(
        DiagnosticObservation observation, RuleState state)
    {
        if (observation.ObservationSequence <= state.LastObservationSequence)
        {
            return null;
        }

        if (state.LastObservedSeconds is { } last && observation.ElapsedSeconds < last)
        {
            state.BreakContinuity();
            state.Active = false;
            state.EpisodeReported = false;
            state.EpisodeFirstSeenUtc = null;
            state.LastActiveEventSeconds = null;
            state.LastObservationSequence = observation.ObservationSequence;
            state.LastObservedSeconds = observation.ElapsedSeconds;
            return null;
        }

        if (state.LastObservedSeconds is { } previous &&
            observation.ElapsedSeconds - previous > observation.MaxObservationGapSeconds)
        {
            state.BreakContinuity();
        }

        state.LastObservationSequence = observation.ObservationSequence;
        state.LastObservedSeconds = observation.ElapsedSeconds;
        AppendEvidence(observation, state);

        if (!state.Active)
        {
            state.RecoverySinceSeconds = null;
            if (!observation.IsBreach)
            {
                state.BreachSinceSeconds = null;
                state.BreachSinceUtc = null;
                return null;
            }

            if (state.BreachSinceSeconds is null)
            {
                state.BreachSinceSeconds = observation.ElapsedSeconds;
                state.BreachSinceUtc = observation.ObservedAtUtc;
                state.BreachFirstSequence = observation.ObservationSequence;
            }

            if (observation.ElapsedSeconds - state.BreachSinceSeconds.Value <
                observation.ActivateDebounceSeconds)
            {
                return null;
            }

            state.Active = true;
            state.EpisodeFirstSeenUtc = state.BreachSinceUtc;
            state.EpisodeFirstSequence = state.BreachFirstSequence;
            state.BreachSinceSeconds = null;
            state.BreachSinceUtc = null;
            state.EpisodeReported = state.LastActiveEventSeconds is null ||
                observation.ElapsedSeconds - state.LastActiveEventSeconds.Value >=
                    observation.CooldownSeconds;
            if (!state.EpisodeReported)
            {
                return null;
            }

            state.LastActiveEventSeconds = observation.ElapsedSeconds;
            return BuildEvent(observation, state, DiagnosticStates.Active,
                state.EpisodeFirstSeenUtc!.Value);
        }

        if (!observation.IsRecovery)
        {
            state.RecoverySinceSeconds = null;
            return null;
        }

        state.RecoverySinceSeconds ??= observation.ElapsedSeconds;
        if (observation.ElapsedSeconds - state.RecoverySinceSeconds.Value <
            observation.RecoverDebounceSeconds)
        {
            return null;
        }

        var firstSeen = state.EpisodeFirstSeenUtc ?? observation.ObservedAtUtc;
        var reported = state.EpisodeReported;
        state.Active = false;
        state.RecoverySinceSeconds = null;
        state.EpisodeFirstSeenUtc = null;
        state.EpisodeReported = false;
        return reported ? BuildEvent(observation, state,
            DiagnosticStates.Resolved, firstSeen) : null;
    }

    private static void AppendEvidence(
        DiagnosticObservation observation, RuleState state)
    {
        state.Evidence.Enqueue(new EvidenceSample(
            observation.ElapsedSeconds, observation.ToEvidence()));
        var cutoff = observation.ElapsedSeconds - observation.EvidenceWindowSeconds;
        while (state.Evidence.Count > 0 &&
            (state.Evidence.Peek().ElapsedSeconds < cutoff ||
                state.Evidence.Count > MaxEvidenceSamplesPerRule))
        {
            _ = state.Evidence.Dequeue();
        }
    }

    private static DiagnosticEventContract BuildEvent(
        DiagnosticObservation observation,
        RuleState state,
        string eventState,
        DateTimeOffset firstSeenUtc)
    {
        var evidence = state.Evidence.Select(static item => item.Evidence).ToArray();
        return new DiagnosticEventContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            EventId = CreateEventId(observation, state.EpisodeFirstSequence, eventState),
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
                ActivateSeconds = observation.ActivateDebounceSeconds,
                RecoverSeconds = observation.RecoverDebounceSeconds,
            },
            CooldownSeconds = observation.CooldownSeconds,
            EvidenceWindow = new DiagnosticEvidenceWindowContract
            {
                FromUtc = evidence.Length == 0
                    ? observation.ObservedAtUtc : evidence[0].ObservedAtUtc,
                ToUtc = observation.ObservedAtUtc,
                SampleCount = evidence.Length,
            },
            FirstSeenUtc = firstSeenUtc,
            LastSeenUtc = observation.ObservedAtUtc,
            ObservationSequence = observation.ObservationSequence,
            Confidence = 1,
            Evidence = evidence,
        };
    }

    private static string CreateEventId(
        DiagnosticObservation observation, long firstSequence, string eventState)
    {
        var material = string.Join('\n', observation.InstanceId, observation.RuleId,
            observation.RuleVersion, observation.SubjectId, eventState,
            firstSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            observation.ObservationSequence.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private sealed record EvidenceSample(
        double ElapsedSeconds, DiagnosticEvidenceContract Evidence);

    private sealed class RuleState
    {
        public string? ContinuityId { get; init; }
        public bool Active { get; set; }
        public bool EpisodeReported { get; set; }
        public long LastObservationSequence { get; set; }
        public double? LastObservedSeconds { get; set; }
        public double? BreachSinceSeconds { get; set; }
        public DateTimeOffset? BreachSinceUtc { get; set; }
        public long BreachFirstSequence { get; set; }
        public double? RecoverySinceSeconds { get; set; }
        public DateTimeOffset? EpisodeFirstSeenUtc { get; set; }
        public long EpisodeFirstSequence { get; set; }
        public double? LastActiveEventSeconds { get; set; }
        public Queue<EvidenceSample> Evidence { get; } = new();

        public void BreakContinuity()
        {
            BreachSinceSeconds = null;
            BreachSinceUtc = null;
            RecoverySinceSeconds = null;
            Evidence.Clear();
        }
    }
}
