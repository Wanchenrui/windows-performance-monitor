using System.Text.Json;
using System.Threading.Channels;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Diagnostics;

public sealed record DiagnosticEngineOptions
{
    public int QueueCapacity { get; init; } = 256;
    public int RecentEventLimit { get; init; } = 2_000;

    internal DiagnosticEngineOptions Validate()
    {
        if (QueueCapacity is <= 0 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }

        if (RecentEventLimit is <= 0 or >
            DiagnosticPolicyLimits.MaxEvents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecentEventLimit));
        }

        return this;
    }
}

public sealed record DiagnosticEngineHealth(
    long AcceptedSnapshots,
    long EvaluatedSnapshots,
    long DroppedSnapshots,
    long EmittedEvents,
    long EvaluationFailures,
    long SinkFailures);

public sealed class DiagnosticEngine :
    ISnapshotConsumer,
    IDiagnosticEventReader,
    IAsyncDisposable
{
    private readonly DiagnosticEvaluator _evaluator;
    private readonly DiagnosticEngineOptions _options;
    private readonly IReadOnlyList<IDiagnosticEventSink> _sinks;
    private readonly Channel<AgentSnapshot> _channel;
    private readonly object _recentGate = new();
    private readonly List<DiagnosticEventContract> _recent = [];
    private Task? _workerTask;
    private int _started;
    private long _acceptedSnapshots;
    private long _evaluatedSnapshots;
    private long _droppedSnapshots;
    private long _emittedEvents;
    private long _evaluationFailures;
    private long _sinkFailures;

    public DiagnosticEngine(
        DiagnosticPolicy policy,
        IEnumerable<IDiagnosticEventSink>? sinks = null,
        DiagnosticEngineOptions? options = null)
    {
        _evaluator = new DiagnosticEvaluator(policy);
        _options = (options ?? new DiagnosticEngineOptions())
            .Validate();
        _sinks = sinks?.ToArray() ?? [];
        _channel = Channel.CreateBounded<AgentSnapshot>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public DiagnosticPolicy Policy => _evaluator.Policy;

    public DiagnosticEngineHealth Health => new(
        Interlocked.Read(ref _acceptedSnapshots),
        Interlocked.Read(ref _evaluatedSnapshots),
        Interlocked.Read(ref _droppedSnapshots),
        Interlocked.Read(ref _emittedEvents),
        Interlocked.Read(ref _evaluationFailures),
        Interlocked.Read(ref _sinkFailures));

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _workerTask = Task.Run(RunAsync, CancellationToken.None);
    }

    public bool TryPublish(AgentSnapshot snapshot)
    {
        if (Volatile.Read(ref _started) == 0 ||
            !_channel.Writer.TryWrite(snapshot))
        {
            Interlocked.Increment(ref _droppedSnapshots);
            return false;
        }

        Interlocked.Increment(ref _acceptedSnapshots);
        return true;
    }

    public async Task WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var acceptedTarget =
            Interlocked.Read(ref _acceptedSnapshots);
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        while (Interlocked.Read(ref _evaluatedSnapshots) <
            acceptedTarget)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeoutCancellation.Token).ConfigureAwait(false);
        }
    }

    public ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validated = DiagnosticQueryValidation.Validate(query);
        DiagnosticEventContract[] snapshot;
        lock (_recentGate)
        {
            snapshot = _recent.ToArray();
        }

        var filtered = snapshot
            .Where(item => validated.Matches(item))
            .OrderByDescending(static item => item.LastSeenUtc)
            .ThenByDescending(
                static item => item.EventId,
                StringComparer.Ordinal)
            .ToArray();
        var selected = filtered
            .Take(validated.MaxEvents)
            .Reverse()
            .ToArray();
        return ValueTask.FromResult(new DiagnosticsContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            InstanceId = responseInstanceId,
            Query = query,
            EventCount = selected.Length,
            Truncated = filtered.Length > selected.Length,
            Events = selected,
        });
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_workerTask is not null)
        {
            await _workerTask.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        await foreach (var snapshot in _channel.Reader.ReadAllAsync()
            .ConfigureAwait(false))
        {
            try
            {
                foreach (var diagnosticEvent in
                    _evaluator.Evaluate(snapshot))
                {
                    Remember(diagnosticEvent);
                    Interlocked.Increment(ref _emittedEvents);
                    foreach (var sink in _sinks)
                    {
                        try
                        {
                            if (!sink.TryPublishDiagnostic(
                                    diagnosticEvent))
                            {
                                Interlocked.Increment(
                                    ref _sinkFailures);
                            }
                        }
                        catch
                        {
                            // v0.6.0: event sinks are outside the
                            // deterministic evaluator and cannot stop it.
                            Interlocked.Increment(ref _sinkFailures);
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is JsonException or
                InvalidOperationException or
                ArgumentException or
                ArithmeticException)
            {
                Interlocked.Increment(ref _evaluationFailures);
            }
            finally
            {
                Interlocked.Increment(ref _evaluatedSnapshots);
            }
        }
    }

    private void Remember(DiagnosticEventContract diagnosticEvent)
    {
        lock (_recentGate)
        {
            _recent.Add(diagnosticEvent);
            var excess = _recent.Count - _options.RecentEventLimit;
            if (excess > 0)
            {
                _recent.RemoveRange(0, excess);
            }
        }
    }
}

public static class DiagnosticQueryValidation
{
    private static readonly HashSet<string> KnownRuleIds =
        new(DiagnosticRuleIds.All, StringComparer.Ordinal);
    private static readonly HashSet<string> KnownStates =
        new(DiagnosticStates.All, StringComparer.Ordinal);

    public static ValidatedDiagnosticQuery Validate(
        DiagnosticQueryContract query)
    {
        if (query.FromEpochMs is null ||
            query.ToEpochMs is null ||
            query.FromEpochMs > query.ToEpochMs)
        {
            throw new ArgumentException(
                "diagnostic_range_invalid",
                nameof(query));
        }

        var range = checked(
            query.ToEpochMs.Value -
            query.FromEpochMs.Value + 1);
        if (range >
            DiagnosticPolicyLimits.MaxQueryRange.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "diagnostic_range_too_large");
        }

        if (query.MaxEvents <= 0 ||
            query.MaxEvents > DiagnosticPolicyLimits.MaxEvents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "diagnostic_max_events_invalid");
        }

        ValidateFilter(
            query.RuleIds,
            KnownRuleIds,
            DiagnosticPolicyLimits.MaxRuleIds,
            "diagnostic_rule_ids_invalid");
        ValidateFilter(
            query.States,
            KnownStates,
            DiagnosticStates.All.Count,
            "diagnostic_states_invalid");
        return new ValidatedDiagnosticQuery(
            query.FromEpochMs.Value,
            query.ToEpochMs.Value,
            query.MaxEvents,
            new HashSet<string>(
                query.RuleIds,
                StringComparer.Ordinal),
            new HashSet<string>(
                query.States,
                StringComparer.Ordinal));
    }

    private static void ValidateFilter(
        IReadOnlyList<string> values,
        IReadOnlySet<string> known,
        int maxCount,
        string errorCode)
    {
        if (values.Count > maxCount ||
            values.Distinct(StringComparer.Ordinal).Count() !=
                values.Count ||
            values.Any(value => !known.Contains(value)))
        {
            throw new ArgumentException(errorCode);
        }
    }
}

public sealed record ValidatedDiagnosticQuery(
    long FromEpochMs,
    long ToEpochMs,
    int MaxEvents,
    IReadOnlySet<string> RuleIds,
    IReadOnlySet<string> States)
{
    public bool Matches(DiagnosticEventContract diagnosticEvent)
    {
        var time = diagnosticEvent.LastSeenUtc
            .ToUnixTimeMilliseconds();
        return time >= FromEpochMs &&
            time <= ToEpochMs &&
            (RuleIds.Count == 0 ||
                RuleIds.Contains(diagnosticEvent.RuleId)) &&
            (States.Count == 0 ||
                States.Contains(diagnosticEvent.State));
    }
}
