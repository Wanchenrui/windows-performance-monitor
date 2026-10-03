using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public static class DiagnosticReplay
{
    public const int ProjectionVersion = 1;

    public static string GetPolicyFingerprint(DiagnosticPolicy policy) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(policy.Validate(), ContractJson.Options))))
            .ToLowerInvariant();

    public static IReadOnlyList<DiagnosticEventContract> Replay(
        IEnumerable<AgentSnapshot> snapshots, DiagnosticPolicy policy)
    {
        var evaluator = new DiagnosticEvaluator(policy);
        var fingerprint = GetPolicyFingerprint(evaluator.Policy);
        var events = new List<DiagnosticEventContract>();
        // Sequence defines order within an instance. Instances retain their
        // first appearance order; UTC cannot place a restarted Agent epoch.
        foreach (var instance in ValidateInstanceOrder(snapshots)
            .GroupBy(static snapshot => snapshot.InstanceId, StringComparer.Ordinal))
        {
            var order = new ReplayOrder();
            foreach (var snapshot in instance.OrderBy(static snapshot =>
                snapshot.DeliverySequence ?? snapshot.Sequence))
            {
                ValidateSnapshot(snapshot, fingerprint);
                order.Accept(snapshot);
                events.AddRange(evaluator.Evaluate(snapshot));
            }
        }

        return events;
    }

    private static IEnumerable<AgentSnapshot> ValidateInstanceOrder(
        IEnumerable<AgentSnapshot> snapshots)
    {
        string? previousInstance = null;
        var instances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Sequence <= 0 || snapshot.CompletedAtUtc is null)
            {
                continue;
            }

            if (!StringComparer.Ordinal.Equals(previousInstance, snapshot.InstanceId) &&
                !instances.Add(snapshot.InstanceId))
            {
                throw new InvalidDataException("diagnostic_replay_order_invalid");
            }

            previousInstance = snapshot.InstanceId;
            yield return snapshot;
        }
    }

    public static async Task<IReadOnlyList<DiagnosticEventContract>> ReplayAsync(
        IAsyncEnumerable<AgentSnapshot> orderedSnapshots,
        DiagnosticPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var evaluator = new DiagnosticEvaluator(policy);
        var fingerprint = GetPolicyFingerprint(evaluator.Policy);
        var events = new List<DiagnosticEventContract>();
        var order = new ReplayOrder();
        await foreach (var snapshot in orderedSnapshots
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (snapshot.Sequence <= 0 || snapshot.CompletedAtUtc is null)
            {
                continue;
            }

            ValidateSnapshot(snapshot, fingerprint);
            order.Accept(snapshot);
            events.AddRange(evaluator.Evaluate(snapshot));
        }

        return events;
    }

    private static void ValidateSnapshot(AgentSnapshot snapshot, string fingerprint)
    {
        if (snapshot.ElapsedSeconds is not { } elapsed ||
            !double.IsFinite(elapsed) || elapsed < 0 ||
            snapshot.Groups.Values.Any(group =>
                group.ObservedAtUtc is not null &&
                (group.ObservedElapsedSeconds is not { } observed ||
                    !double.IsFinite(observed) || observed < 0 || observed > elapsed ||
                    group.ObservationSequence is not > 0 ||
                    group.ObservationSequence > snapshot.Sequence)))
        {
            // Older rows remain readable as history, but cannot prove a
            // duration through clock jumps. Never silently reconstruct UTC time.
            throw new InvalidDataException("diagnostic_replay_time_unavailable");
        }

        if (snapshot.Groups.TryGetValue(GroupIds.Sampler, out var sampler) &&
            sampler.Data is JsonObject data && data.ContainsKey("storageReplay"))
        {
            if (data["storageReplay"] is not JsonObject projection ||
                projection["projectionVersion"] is not JsonValue version ||
                !version.TryGetValue<int>(out var number) || number != ProjectionVersion ||
                projection["policyFingerprint"] is not JsonValue policyValue ||
                !policyValue.TryGetValue<string>(out var saved) ||
                !StringComparer.Ordinal.Equals(saved, fingerprint))
            {
                throw new InvalidDataException("diagnostic_replay_projection_incompatible");
            }
        }
    }

    private sealed class ReplayOrder
    {
        private readonly HashSet<string> _closedInstances = new(StringComparer.Ordinal);
        private string? _instanceId;
        private long _sequence;
        private long? _deliverySequence;
        private double _elapsedSeconds;

        public void Accept(AgentSnapshot snapshot)
        {
            if (!StringComparer.Ordinal.Equals(_instanceId, snapshot.InstanceId))
            {
                if (_closedInstances.Contains(snapshot.InstanceId))
                {
                    throw new InvalidDataException("diagnostic_replay_order_invalid");
                }

                if (_instanceId is not null)
                {
                    _closedInstances.Add(_instanceId);
                }

                _instanceId = snapshot.InstanceId;
                _sequence = 0;
                _deliverySequence = null;
                _elapsedSeconds = 0;
            }

            if (snapshot.Sequence < _sequence ||
                snapshot.DeliverySequence is { } delivery &&
                    _deliverySequence is { } previousDelivery && delivery < previousDelivery ||
                snapshot.ElapsedSeconds!.Value < _elapsedSeconds)
            {
                throw new InvalidDataException("diagnostic_replay_order_invalid");
            }

            _sequence = snapshot.Sequence;
            _deliverySequence = snapshot.DeliverySequence;
            _elapsedSeconds = snapshot.ElapsedSeconds.Value;
        }
    }
}
