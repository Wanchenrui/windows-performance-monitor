using System.Collections.ObjectModel;
using System.Text.Json;
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

[JsonConverter(typeof(SnapshotGroupJsonConverter))]
public sealed record SnapshotGroup(
    string ProviderId,
    DateTimeOffset? ObservedAtUtc,
    string Availability,
    string Freshness,
    ProviderCoverage Coverage,
    IReadOnlyList<ProviderError> Errors,
    JsonNode? Data)
{
    private readonly FrozenPayload? _payload = FreezeData(Data);
    private readonly JsonNodeOptions? _nodeOptions = Data?.Options;
    private readonly ProviderCoverage _coverage = FreezeCoverage(Coverage);
    private readonly IReadOnlyList<ProviderError> _errors = Array.AsReadOnly(Errors.ToArray());

    /// <summary>
    /// Returns an isolated, lazily materialized JsonNode view of the frozen
    /// payload. Keep a local view for repeated access; mutations affect only
    /// that view. Replace data explicitly with <c>group with { Data = view }</c>.
    /// </summary>
    public JsonNode? Data
    {
        get => _payload?.NonJsonData is { } nonJson
            ? CloneNonJson(nonJson, replaceNonFinite: false)
            : CreateDataView(_payload?.Element, _nodeOptions);
        init
        {
            _payload = FreezeData(value);
            _nodeOptions = value?.Options;
        }
    }

    public ProviderCoverage Coverage
    {
        get => _coverage;
        init => _coverage = FreezeCoverage(value);
    }

    public IReadOnlyList<ProviderError> Errors
    {
        get => _errors;
        init => _errors = Array.AsReadOnly(value.ToArray());
    }

    // Both fields belong to the containing snapshot's Agent instance.
    // A held value retains its original observation sequence and time.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ObservationSequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ObservedElapsedSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollectionState { get; init; }

    // The converter writes the owned immutable element directly. No mutable
    // JsonNode view escapes through this serialization path.
    internal JsonElement? SerializedData => _payload?.Element;

    internal void EnsureSerializable()
    {
        if (_payload?.NonJsonData is not null)
            throw new ArgumentException("Non-finite numbers cannot be written as valid snapshot JSON.");
    }

    /// <summary>
    /// Reads the owned immutable JSON without materializing a mutable tree.
    /// The element remains valid for the lifetime of this group and its copies.
    /// Non-finite in-memory numbers appear as null; wire serialization rejects
    /// that original invalid payload. Property access uses JSON's exact key names.
    /// </summary>
    [JsonIgnore]
    public JsonElement? ReadOnlyData => _payload?.Element;

    // Read projections need the sampler period from this publication, rather
    // than the mutable latest execution belonging to a later publication.
    internal double SamplerPeriodSeconds { get; init; }

    // Each real observation receives its own budget. Later period changes may
    // only tighten that budget; another slow setting cannot revive old data.
    [JsonIgnore]
    internal double ObservationPeriodBudgetSeconds { get; init; }

    internal static SnapshotGroup FromJson(
        string providerId,
        DateTimeOffset? observedAtUtc,
        string availability,
        string freshness,
        ProviderCoverage coverage,
        IReadOnlyList<ProviderError> errors,
        JsonElement? data) =>
        new(providerId, observedAtUtc, availability, freshness, coverage, errors, data);

    private SnapshotGroup(
        string providerId,
        DateTimeOffset? observedAtUtc,
        string availability,
        string freshness,
        ProviderCoverage coverage,
        IReadOnlyList<ProviderError> errors,
        JsonElement? data)
        : this(providerId, observedAtUtc, availability, freshness, coverage, errors, (JsonNode?)null)
    {
        _payload = data is { } element ? new FrozenPayload(element.Clone(), null) : null;
    }

    private static FrozenPayload? FreezeData(JsonNode? data)
    {
        if (data is null) return null;
        try
        {
            return new FrozenPayload(JsonSerializer.SerializeToElement(data), null);
        }
        catch (ArgumentException) when (ContainsNonFinite(data))
        {
            // Legacy in-memory consumers can inspect and reject NaN/Infinity.
            // Preserve that behavior without publishing mutable input aliases.
            // Only the immutable read view maps these non-JSON numbers to null;
            // wire serialization still rejects the original invalid payload.
            var owned = CloneNonJson(data, replaceNonFinite: false)!;
            var readable = CloneNonJson(owned, replaceNonFinite: true);
            return new FrozenPayload(JsonSerializer.SerializeToElement(readable), owned);
        }
    }

    private static bool ContainsNonFinite(JsonNode? node) => node switch
    {
        JsonValue value => IsNonFinite(value),
        JsonObject obj => obj.Any(item => ContainsNonFinite(item.Value)),
        JsonArray array => array.Any(ContainsNonFinite),
        _ => false,
    };

    private static bool IsNonFinite(JsonValue value) =>
        value.TryGetValue<double>(out var number) && !double.IsFinite(number) ||
        value.TryGetValue<float>(out var single) && !float.IsFinite(single);

    private static JsonNode? CloneNonJson(JsonNode? node, bool replaceNonFinite)
    {
        if (node is JsonValue value && IsNonFinite(value))
        {
            if (replaceNonFinite) return null;
            return value.TryGetValue<double>(out var number) ? JsonValue.Create(number)
                : JsonValue.Create(value.GetValue<float>());
        }
        if (node is JsonObject obj)
        {
            var copy = new JsonObject(obj.Options);
            foreach (var (key, child) in obj) copy[key] = CloneNonJson(child, replaceNonFinite);
            return copy;
        }
        if (node is JsonArray array)
        {
            var copy = new JsonArray(array.Options);
            foreach (var child in array) copy.Add(CloneNonJson(child, replaceNonFinite));
            return copy;
        }
        return node?.DeepClone();
    }

    private static JsonNode? CreateDataView(JsonElement? data, JsonNodeOptions? options) =>
        data?.ValueKind switch
        {
            JsonValueKind.Object => JsonObject.Create(data.Value, options),
            JsonValueKind.Array => JsonArray.Create(data.Value, options),
            null or JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => JsonValue.Create(data.Value, options),
        };

    private static ProviderCoverage FreezeCoverage(ProviderCoverage coverage) =>
        coverage.SkippedByReason is null
            ? coverage
            : coverage with
            {
                SkippedByReason = new ReadOnlyDictionary<string, int>(
                    new Dictionary<string, int>(coverage.SkippedByReason, StringComparer.Ordinal)),
            };

    private sealed record FrozenPayload(JsonElement Element, JsonNode? NonJsonData);
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
    private readonly IReadOnlyDictionary<string, SnapshotGroup> _groups = OwnGroups(Groups);

    public IReadOnlyDictionary<string, SnapshotGroup> Groups
    {
        get => _groups;
        init => _groups = OwnGroups(value);
    }

    // Monotonic seconds since this assembler was created. UTC is display
    // metadata; it cannot establish age or a diagnostic duration.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElapsedSeconds { get; init; }

    // Assigned by SnapshotFanout for actual consumer deliveries. Provider
    // update Sequence may legitimately jump between those deliveries.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DeliverySequence { get; init; }

    private static IReadOnlyDictionary<string, SnapshotGroup> OwnGroups(
        IReadOnlyDictionary<string, SnapshotGroup> groups) =>
        groups is OwnedGroups ? groups : new OwnedGroups(groups);

    // Recognize only this privately owned wrapper when reusing a dictionary.
    // An arbitrary IReadOnlyDictionary can still wrap an externally writable one.
    private sealed class OwnedGroups(IReadOnlyDictionary<string, SnapshotGroup> groups)
        : ReadOnlyDictionary<string, SnapshotGroup>(
            new Dictionary<string, SnapshotGroup>(groups, StringComparer.Ordinal));
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
    private readonly Dictionary<string, SnapshotGroup> _latestGroups =
        new(StringComparer.Ordinal);
    private readonly AtomicSnapshotCache _cache;
    private readonly string _instanceId;
    private readonly long _originTimestamp;
    private long _sequence;
    private ProviderExecution? _latestExecution;
    private double _latestExecutionElapsedSeconds;
    private SnapshotGroup? _latestSampler;
    private int _lastPeriodMultiplier = 1;

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

    public ProviderSamplingMode? SamplingMode { get; init; }

    public AgentSnapshot Read()
    {
        if (SamplingMode is not null && SamplingMode.HardwarePeriodMultiplier !=
            Volatile.Read(ref _lastPeriodMultiplier))
        {
            lock (_gate)
            {
                var multiplier = SamplingMode.HardwarePeriodMultiplier;
                _lastPeriodMultiplier = multiplier;
                _cache.Publish(BuildSnapshot(GetElapsedSeconds(), multiplier));
            }
        }
        // One atomic version is captured before reading the clock. Its groups,
        // execution metadata and sequence always belong to that same version.
        var snapshot = _cache.Read();
        return RefreshAgeAndFreshness(snapshot, GetElapsedSeconds());
    }

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

            // The mode and publication share this boundary. After applying a
            // pause, an already-running collector cannot republish fresh data.
            if (SamplingMode?.Rejects(result.GroupId, result.CollectionRevision) == true)
            {
                result = ProviderResult.Paused(descriptor) with
                {
                    CollectionState = SamplingMode.IsPaused(result.GroupId) ? "paused" : null,
                };
            }

            var elapsedSeconds = GetElapsedSeconds();
            _sequence++;
            // Current schedulers provide exact local collection timestamps.
            // Duration remains a compatible fallback for synthetic callers.
            var durationSeconds = double.IsFinite(execution.DurationMilliseconds)
                ? Math.Max(0, execution.DurationMilliseconds / 1000)
                : 0;
            var observedElapsedSeconds = result.ObservedAtUtc is null
                    ? null
                    : execution.StartedTimestamp is { } started
                        ? ElapsedSecondsAt(started)
                        : (double?)Math.Max(0, elapsedSeconds - durationSeconds);
            var observationPeriod = Math.Min(ExpectedPeriodSeconds(descriptor, execution.ExpectedPeriodSeconds),
                SamplingMode?.GetEffectivePeriod(descriptor).TotalSeconds ?? descriptor.DefaultPeriod.TotalSeconds);
            _latestGroups[result.GroupId] = new SnapshotGroup(
                result.ProviderId,
                result.ObservedAtUtc,
                result.Availability,
                Freshness(observationPeriod, observedElapsedSeconds, elapsedSeconds),
                result.Coverage,
                result.Errors,
                result.Data)
            {
                ObservationSequence = _sequence,
                ObservedElapsedSeconds = observedElapsedSeconds,
                CollectionState = result.CollectionState,
                ObservationPeriodBudgetSeconds = observationPeriod,
            };
            _latestExecution = execution;
            _latestExecutionElapsedSeconds = execution.CompletedTimestamp is { } completed
                ? ElapsedSecondsAt(completed)
                : elapsedSeconds;
            _latestSampler = BuildSamplerGroup(execution, elapsedSeconds);
            _cache.Publish(BuildSnapshot(elapsedSeconds));
        }

        return ValueTask.CompletedTask;
    }

    private AgentSnapshot BuildSnapshot(double elapsedSeconds, int? periodMultiplier = null)
    {
        var multiplier = periodMultiplier ?? SamplingMode?.HardwarePeriodMultiplier ?? 1;
        var groups = new Dictionary<string, SnapshotGroup>(
            StringComparer.Ordinal);
        foreach (var descriptor in _descriptors.Values)
        {
            if (_latestGroups.TryGetValue(
                    descriptor.GroupId,
                    out var group))
            {
                var currentPeriod = descriptor.DefaultPeriod.TotalSeconds *
                    (descriptor.GroupId is GroupIds.Gpu or GroupIds.Sensors ? multiplier : 1);
                var budget = Math.Min(FreshnessPeriodSeconds(descriptor, group), currentPeriod);
                if (budget != group.ObservationPeriodBudgetSeconds)
                {
                    group = group with { ObservationPeriodBudgetSeconds = budget };
                    _latestGroups[descriptor.GroupId] = group;
                }
                var freshness = Freshness(budget,
                    group.ObservedElapsedSeconds, elapsedSeconds);
                groups[descriptor.GroupId] = freshness == group.Freshness
                    ? group
                    : group with { Freshness = freshness };
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

        if (_latestSampler is not null)
        {
            groups[GroupIds.Sampler] = _latestSampler;
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

    private SnapshotGroup BuildSamplerGroup(
        ProviderExecution execution,
        double elapsedSeconds)
    {
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.SamplerIntervalSeconds] = MetricJson.Value(
                    ExpectedPeriodSeconds(execution.Descriptor, execution.ExpectedPeriodSeconds),
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
            ExpectedPeriodSeconds(execution.Descriptor, execution.ExpectedPeriodSeconds) * 3;
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
            SamplerPeriodSeconds = ExpectedPeriodSeconds(execution.Descriptor, execution.ExpectedPeriodSeconds),
        };
    }

    private static SnapshotSummary Summarize(
        IEnumerable<SnapshotGroup> groups)
    {
        var available = 0;
        var count = 0;
        var stale = false;
        var warmingUp = false;
        foreach (var group in groups)
        {
            count++;
            if (group.Availability == AvailabilityStates.Available) available++;
            stale |= group.Freshness == FreshnessStates.Stale;
            warmingUp |= group.Freshness == FreshnessStates.WarmingUp;
        }
        var availability = available == count
            ? AvailabilityStates.Available
            : available > 0
                ? AvailabilityStates.Partial
                : AvailabilityStates.Error;
        var freshness = stale
            ? FreshnessStates.Stale
            : warmingUp
                ? FreshnessStates.WarmingUp
                : FreshnessStates.Fresh;
        return new SnapshotSummary(availability, freshness);
    }

    private AgentSnapshot RefreshAgeAndFreshness(
        AgentSnapshot snapshot,
        double elapsedSeconds)
    {
        Dictionary<string, SnapshotGroup>? refreshedGroups = null;
        double? latestObserved = null;
        foreach (var (groupId, group) in snapshot.Groups)
        {
            var periodSeconds = groupId == GroupIds.Sampler
                ? group.SamplerPeriodSeconds
                : FreshnessPeriodSeconds(_descriptors[groupId], group);
            var freshness = Freshness(periodSeconds, group.ObservedElapsedSeconds, elapsedSeconds);
            if (freshness != group.Freshness)
            {
                refreshedGroups ??= new Dictionary<string, SnapshotGroup>(snapshot.Groups, StringComparer.Ordinal);
                refreshedGroups[groupId] = group with { Freshness = freshness };
            }
            if (group.ObservedElapsedSeconds is { } observed &&
                (latestObserved is null || observed > latestObserved.Value))
                latestObserved = observed;
        }

        var groups = refreshedGroups ?? snapshot.Groups;
        return snapshot with
        {
            DataAgeSeconds = latestObserved is null ? null : Math.Max(0, elapsedSeconds - latestObserved.Value),
            ElapsedSeconds = elapsedSeconds,
            Groups = groups,
            Summary = refreshedGroups is null ? snapshot.Summary : Summarize(groups.Values),
        };
    }

    private static string Freshness(double periodSeconds, double? observedElapsedSeconds, double elapsedSeconds) =>
        observedElapsedSeconds is null
            ? FreshnessStates.WarmingUp
            : elapsedSeconds - observedElapsedSeconds.Value > periodSeconds * 3
                ? FreshnessStates.Stale
                : FreshnessStates.Fresh;

    private static double ExpectedPeriodSeconds(ProviderDescriptor descriptor, double? executionPeriod) =>
        executionPeriod is { } value && double.IsFinite(value) && value > 0
            ? value : descriptor.DefaultPeriod.TotalSeconds;

    private static double FreshnessPeriodSeconds(ProviderDescriptor descriptor, SnapshotGroup group) =>
        group.ObservationPeriodBudgetSeconds > 0
            ? group.ObservationPeriodBudgetSeconds : descriptor.DefaultPeriod.TotalSeconds;

    private double GetElapsedSeconds() => ElapsedSecondsAt(_timeProvider.GetTimestamp());

    private double ElapsedSecondsAt(long timestamp) => Math.Max(
        0,
        _timeProvider.GetElapsedTime(
            _originTimestamp,
            timestamp).TotalSeconds);

}
