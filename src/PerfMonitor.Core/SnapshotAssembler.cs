using System.Text.Json.Nodes;
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
    JsonNode? Data);

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
    IReadOnlyDictionary<string, SnapshotGroup> Groups);

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
    private readonly Dictionary<string, ProviderResult> _latestResults =
        new(StringComparer.Ordinal);
    private readonly AtomicSnapshotCache _cache;
    private readonly string _instanceId;
    private long _sequence;
    private ProviderExecution? _latestExecution;

    public SnapshotAssembler(
        IEnumerable<ProviderDescriptor> descriptors,
        TimeProvider? timeProvider = null,
        string? instanceId = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        _cache = new AtomicSnapshotCache(BuildSnapshot(_timeProvider.GetUtcNow()));
    }

    public string InstanceId => _instanceId;

    public AgentSnapshot Read() =>
        RefreshAgeAndFreshness(_cache.Read(), _timeProvider.GetUtcNow());

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

            _latestResults[result.GroupId] = result with
            {
                Data = result.Data?.DeepClone(),
            };
            _latestExecution = execution;
            _sequence++;
            _cache.Publish(BuildSnapshot(execution.CompletedAtUtc));
        }

        return ValueTask.CompletedTask;
    }

    private AgentSnapshot BuildSnapshot(DateTimeOffset now)
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
                    now);
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
                now);
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
            .Where(group => group.ObservedAtUtc is not null)
            .Select(group => group.ObservedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();
        double? dataAge = observed == default
            ? null
            : Math.Max(0, (now - observed).TotalSeconds);

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
            groups);
    }

    private SnapshotGroup BuildGroup(
        ProviderDescriptor descriptor,
        ProviderResult result,
        DateTimeOffset now)
    {
        var freshness = result.ObservedAtUtc is null
            ? FreshnessStates.WarmingUp
            : now - result.ObservedAtUtc.Value >
                TimeSpan.FromTicks(descriptor.DefaultPeriod.Ticks * 3)
                ? FreshnessStates.Stale
                : FreshnessStates.Fresh;
        return new SnapshotGroup(
            result.ProviderId,
            result.ObservedAtUtc,
            result.Availability,
            freshness,
            result.Coverage,
            result.Errors,
            result.Data?.DeepClone());
    }

    private static SnapshotGroup BuildSamplerGroup(
        ProviderExecution execution,
        DateTimeOffset now)
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
        var stale = now - execution.CompletedAtUtc >
            TimeSpan.FromTicks(
                execution.Descriptor.DefaultPeriod.Ticks * 3);
        return new SnapshotGroup(
            ProviderIds.Sampler,
            execution.CompletedAtUtc,
            AvailabilityStates.Available,
            stale ? FreshnessStates.Stale : FreshnessStates.Fresh,
            ProviderCoverage.Complete,
            [],
            data);
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
        AgentSnapshot snapshot,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(snapshot, _cache.Read()))
            {
                snapshot = _cache.Read();
            }

            var refreshed = BuildSnapshot(now);
            return refreshed with
            {
                Sequence = snapshot.Sequence,
            };
        }
    }
}
