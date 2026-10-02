using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

public sealed record SnapshotSummary(
    string Availability,
    string Freshness);

public sealed record SnapshotRetention(
    double HistoryWindowSeconds,
    int HistoryPointLimit);

public sealed record SnapshotGroup(
    string ProviderId,
    DateTimeOffset? ObservedAtUtc,
    string Availability,
    string Freshness,
    ProviderCoverage Coverage,
    IReadOnlyList<ProviderError> Errors,
    JsonNode? Data)
{
    // Both fields belong to the containing snapshot's Agent instance.
    // A held value retains its original observation sequence and time.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ObservationSequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ObservedElapsedSeconds { get; init; }
}

public sealed record AgentSnapshot(
    string ContractVersion,
    string ProductVersion,
    string InstanceId,
    long Sequence,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    double? DataAgeSeconds,
    SnapshotSummary Summary,
    SnapshotRetention Retention,
    IReadOnlyDictionary<string, SnapshotGroup> Groups)
{
    // Monotonic seconds since this assembler was created. UTC is display
    // metadata; it cannot establish age or a diagnostic duration.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElapsedSeconds { get; init; }

    // Assigned by SnapshotFanout for actual consumer deliveries. Provider
    // update Sequence may legitimately jump between those deliveries.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DeliverySequence { get; init; }
}

public sealed class AtomicSnapshotCache
{
    private AgentSnapshot _latest;

    public AtomicSnapshotCache(AgentSnapshot initial)
    {
        _latest = initial;
    }

    public AgentSnapshot Read() => Volatile.Read(ref _latest);

    public void Publish(AgentSnapshot snapshot) =>
        Volatile.Write(ref _latest, snapshot);
}

public sealed class SnapshotAssembler : IProviderResultSink
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<string, ProviderDescriptor> _descriptors;
    private readonly Dictionary<string, ObservedResult> _latestResults =
        new(StringComparer.Ordinal);
    private readonly AtomicSnapshotCache _cache;
    private readonly string _instanceId;
    private readonly long _originTimestamp;
    private long _sequence;
    private ProviderExecution? _latestExecution;
    private double _latestExecutionElapsedSeconds;

    public SnapshotAssembler(
        IEnumerable<ProviderDescriptor> descriptors,
        TimeProvider? timeProvider = null,
        string? instanceId = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _originTimestamp = _timeProvider.GetTimestamp();
        _instanceId = instanceId ?? Guid.NewGuid().ToString("N");
        _descriptors = descriptors.ToDictionary(
            descriptor => descriptor.GroupId,
            StringComparer.Ordinal);
        if (_descriptors.Count == 0)
        {
            throw new ArgumentException(
                "At least one descriptor is required.",
                nameof(descriptors));
        }

        _cache = new AtomicSnapshotCache(BuildSnapshot(GetElapsedSeconds()));
    }

    public string InstanceId => _instanceId;

    public AgentSnapshot Read() =>
        RefreshAgeAndFreshness(_cache.Read());

    public ValueTask PublishAsync(
        ProviderResult result,
        ProviderExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_descriptors.TryGetValue(result.GroupId, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"Unregistered provider group: {result.GroupId}");
            }

            if (!StringComparer.Ordinal.Equals(
                    descriptor.ProviderId,
                    result.ProviderId))
            {
                throw new InvalidOperationException(
                    $"Provider ID mismatch for group {result.GroupId}.");
            }

            var elapsedSeconds = GetElapsedSeconds();
            _sequence++;
            // Current schedulers provide exact local collection timestamps.
            // Duration remains a compatible fallback for synthetic callers.
            var durationSeconds = double.IsFinite(execution.DurationMilliseconds)
                ? Math.Max(0, execution.DurationMilliseconds / 1000)
                : 0;
            _latestResults[result.GroupId] = new ObservedResult(
                result with { Data = result.Data?.DeepClone() },
                result.ObservedAtUtc is null
                    ? null
                    : execution.StartedTimestamp is { } started
                        ? ElapsedSecondsAt(started)
                        : Math.Max(0, elapsedSeconds - durationSeconds),
                _sequence);
            _latestExecution = execution;
            _latestExecutionElapsedSeconds = execution.CompletedTimestamp is { } completed
                ? ElapsedSecondsAt(completed)
                : elapsedSeconds;
            _cache.Publish(BuildSnapshot(elapsedSeconds));
        }

        return ValueTask.CompletedTask;
    }

    private AgentSnapshot BuildSnapshot(double elapsedSeconds)
    {
        var groups = new Dictionary<string, SnapshotGroup>(
            StringComparer.Ordinal);
        foreach (var descriptor in _descriptors.Values)
        {
            if (_latestResults.TryGetValue(
                    descriptor.GroupId,
                    out var result))
            {
                groups[descriptor.GroupId] = BuildGroup(
                    descriptor,
                    result,
                    elapsedSeconds);
            }
            else
            {
                groups[descriptor.GroupId] = new SnapshotGroup(
                    descriptor.ProviderId,
                    null,
                    AvailabilityStates.Unavailable,
                    FreshnessStates.WarmingUp,
                    ProviderCoverage.Limited,
                    [],
                    null);
            }
        }

        if (_latestExecution is not null)
        {
            groups[GroupIds.Sampler] = BuildSamplerGroup(
                _latestExecution,
                elapsedSeconds);
        }
        else
        {
            groups[GroupIds.Sampler] = new SnapshotGroup(
                ProviderIds.Sampler,
                null,
                AvailabilityStates.Unavailable,
                FreshnessStates.WarmingUp,
                ProviderCoverage.Limited,
                [],
                null);
        }

        var summary = Summarize(groups.Values);
        var observed = groups.Values
            .Select(group => group.ObservedElapsedSeconds)
            .Max();
        double? dataAge = observed is null
            ? null
            : Math.Max(0, elapsedSeconds - observed.Value);

        return new AgentSnapshot(
            ContractVersions.V1,
            ProductVersions.Agent,
            _instanceId,
            _sequence,
            _latestExecution?.ScheduledAtUtc,
            _latestExecution?.StartedAtUtc,
            _latestExecution?.CompletedAtUtc,
            dataAge,
            summary,
            new SnapshotRetention(3600, 86_400),
            groups)
        {
            ElapsedSeconds = elapsedSeconds,
        };
    }

    private SnapshotGroup BuildGroup(
        ProviderDescriptor descriptor,
        ObservedResult observed,
        double elapsedSeconds)
    {
        var result = observed.Result;
        var freshness = observed.ElapsedSeconds is null
            ? FreshnessStates.WarmingUp
            : elapsedSeconds - observed.ElapsedSeconds.Value >
                descriptor.DefaultPeriod.TotalSeconds * 3
                ? FreshnessStates.Stale
                : FreshnessStates.Fresh;
        return new SnapshotGroup(
            result.ProviderId,
            result.ObservedAtUtc,
            result.Availability,
            freshness,
            result.Coverage,
            result.Errors,
            result.Data?.DeepClone())
        {
            ObservationSequence = observed.Sequence,
            ObservedElapsedSeconds = observed.ElapsedSeconds,
        };
    }

    private SnapshotGroup BuildSamplerGroup(
        ProviderExecution execution,
        double elapsedSeconds)
    {
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.SamplerIntervalSeconds] = MetricJson.Value(
                    execution.Descriptor.DefaultPeriod.TotalSeconds,
                    Units.Second,
                    SourceIds.Scheduler),
                [MetricIds.SamplerDurationMilliseconds] = MetricJson.Value(
                    execution.DurationMilliseconds,
                    Units.Millisecond,
                    SourceIds.Scheduler),
                [MetricIds.SamplerJitterMilliseconds] = MetricJson.Value(
                    execution.JitterMilliseconds,
                    Units.Millisecond,
                    SourceIds.Scheduler),
                [MetricIds.SamplerMissedIntervals] = MetricJson.Value(
                    execution.MissedIntervalsTotal,
                    Units.Count,
                    SourceIds.Scheduler),
                [MetricIds.SamplerSkippedIntervals] = MetricJson.Value(
                    execution.SkippedBusyIntervalsTotal,
                    Units.Count,
                    SourceIds.Scheduler),
            },
        };
        var stale = elapsedSeconds - _latestExecutionElapsedSeconds >
            execution.Descriptor.DefaultPeriod.TotalSeconds * 3;
        return new SnapshotGroup(
            ProviderIds.Sampler,
            execution.CompletedAtUtc,
            AvailabilityStates.Available,
            stale ? FreshnessStates.Stale : FreshnessStates.Fresh,
            ProviderCoverage.Complete,
            [],
            data)
        {
            ObservationSequence = _sequence,
            ObservedElapsedSeconds = _latestExecutionElapsedSeconds,
        };
    }

    private static SnapshotSummary Summarize(
        IEnumerable<SnapshotGroup> groups)
    {
        var materialized = groups.ToArray();
        var available = materialized.Count(
            group => group.Availability == AvailabilityStates.Available);
        var availability = available == materialized.Length
            ? AvailabilityStates.Available
            : available > 0
                ? AvailabilityStates.Partial
                : AvailabilityStates.Error;
        var freshness = materialized.Any(
            group => group.Freshness == FreshnessStates.Stale)
            ? FreshnessStates.Stale
            : materialized.Any(
                group => group.Freshness == FreshnessStates.WarmingUp)
                ? FreshnessStates.WarmingUp
                : FreshnessStates.Fresh;
        return new SnapshotSummary(availability, freshness);
    }

    private AgentSnapshot RefreshAgeAndFreshness(
        AgentSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(snapshot, _cache.Read()))
            {
                snapshot = _cache.Read();
            }

            // Capture time after acquiring the gate: otherwise a concurrent
            // publication could have a later observation than this envelope.
            var refreshed = BuildSnapshot(GetElapsedSeconds());
            return refreshed with
            {
                Sequence = snapshot.Sequence,
            };
        }
    }

    private double GetElapsedSeconds() => ElapsedSecondsAt(_timeProvider.GetTimestamp());

    private double ElapsedSecondsAt(long timestamp) => Math.Max(
        0,
        _timeProvider.GetElapsedTime(
            _originTimestamp,
            timestamp).TotalSeconds);

    private sealed record ObservedResult(
        ProviderResult Result,
        double? ElapsedSeconds,
        long Sequence);
}
