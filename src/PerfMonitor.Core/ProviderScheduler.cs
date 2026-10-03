using System.Collections.Concurrent;
using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

public sealed class ProviderScheduler : IAsyncDisposable
{
    private readonly IReadOnlyList<IMetricProvider> _providers;
    private readonly IProviderResultSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly int _logicalProcessorCount;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim? _optionalConcurrency;
    private readonly HashSet<string> _reservedGroupIds;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _runners = [];
    private readonly ConcurrentDictionary<
        IMetricProvider,
        Task<ProviderResult>> _outstandingCollections =
        new(ReferenceEqualityComparer.Instance);
    private int _started;

    public ProviderScheduler(
        IEnumerable<IMetricProvider> providers,
        IProviderResultSink sink,
        int maxConcurrency,
        TimeProvider? timeProvider = null,
        int? logicalProcessorCount = null,
        IEnumerable<string>? reservedGroupIds = null)
    {
        _providers = providers.ToArray();
        if (_providers.Count == 0)
        {
            throw new ArgumentException(
                "At least one provider is required.",
                nameof(providers));
        }

        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        var duplicate = _providers
            .GroupBy(provider => provider.Descriptor.GroupId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Duplicate provider group: {duplicate.Key}",
                nameof(providers));
        }

        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logicalProcessorCount = Math.Max(
            1,
            logicalProcessorCount ?? Environment.ProcessorCount);
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _reservedGroupIds = reservedGroupIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        var knownGroups = _providers.Select(provider => provider.Descriptor.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        if (!_reservedGroupIds.IsSubsetOf(knownGroups))
        {
            throw new ArgumentException("Reserved groups must have registered providers.", nameof(reservedGroupIds));
        }
        // Optional calls acquire this gate before the total gate, preserving one
        // of the existing slots for basic collection. A single-slot configuration
        // keeps shared scheduling compatibility and cannot provide this isolation.
        if (maxConcurrency > 1 && _reservedGroupIds.Count > 0 &&
            _providers.Any(provider => !_reservedGroupIds.Contains(provider.Descriptor.GroupId)))
        {
            _optionalConcurrency = new SemaphoreSlim(maxConcurrency - 1, maxConcurrency - 1);
        }
    }

    public IReadOnlyList<ProviderDescriptor> Descriptors =>
        _providers.Select(provider => provider.Descriptor).ToArray();

    public ProviderSamplingMode? SamplingMode { get; init; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            _runners.Add(
                RunProviderAsync(provider, _stopping.Token));
        }
    }

    public async ValueTask StopAsync()
    {
        _stopping.Cancel();
        if (_runners.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(_runners).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        // v0.7.0: stateful native providers (for example PDH)
        // are disposed only after all isolated collections have stopped.
        foreach (var provider in _providers)
        {
            if (_outstandingCollections.TryGetValue(
                    provider,
                    out var collection))
            {
                _ = DisposeAfterCollectionAsync(
                    provider,
                    collection);
            }
            else
            {
                await DisposeProviderAsync(provider)
                    .ConfigureAwait(false);
            }
        }
        _stopping.Dispose();
    }

    private async Task RunProviderAsync(
        IMetricProvider provider,
        CancellationToken stoppingToken)
    {
        var descriptor = provider.Descriptor;
        var basePeriodTicks = SchedulerMath.ToTimestampTicks(
            _timeProvider,
            descriptor.DefaultPeriod);
        var nextDeadline = _timeProvider.GetTimestamp();
        long missedTotal = 0;
        long skippedBusyTotal = 0;
        var consecutiveFailures = 0;
        Task<ProviderResult>? inFlight = null;
        long? inFlightRevision = null;
        CancellationTokenSource? collectionCancellation = null;
        var slotHeld = false;
        var optional = _optionalConcurrency is not null && !_reservedGroupIds.Contains(descriptor.GroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await DelayUntilAsync(nextDeadline, stoppingToken)
                    .ConfigureAwait(false);

                var wakeTimestamp = _timeProvider.GetTimestamp();
                var wakeUtc = _timeProvider.GetUtcNow();
                var scheduledAtUtc = wakeUtc - _timeProvider.GetElapsedTime(
                    nextDeadline,
                    wakeTimestamp);

                if (SamplingMode?.IsPaused(descriptor.GroupId) == true)
                {
                    collectionCancellation?.Cancel();
                    if (inFlight?.IsCompleted == true)
                    {
                        _ = await inFlight.ConfigureAwait(false);
                        inFlight = null;
                        collectionCancellation?.Dispose();
                        collectionCancellation = null;
                        if (slotHeld)
                        {
                            ReleaseConcurrency(optional);
                            slotHeld = false;
                        }
                    }
                    var pausedExecution = new ProviderExecution(descriptor, scheduledAtUtc,
                        wakeUtc, wakeUtc, 0,
                        Math.Max(0, _timeProvider.GetElapsedTime(nextDeadline, wakeTimestamp).TotalMilliseconds),
                        missedTotal, skippedBusyTotal)
                    {
                        CompletedTimestamp = wakeTimestamp,
                    };
                    await _sink.PublishAsync(ProviderResult.Paused(descriptor), pausedExecution,
                        stoppingToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    nextDeadline = AdvanceDeadline(nextDeadline, basePeriodTicks, 0,
                        wakeTimestamp, ref missedTotal);
                    continue;
                }

                if (inFlight is not null)
                {
                    if (!inFlight.IsCompleted)
                    {
                        skippedBusyTotal++;
                        var timeoutResult = ProviderResult.Timeout(
                            descriptor,
                            wakeUtc) with { CollectionRevision = inFlightRevision };
                        var busyExecution = new ProviderExecution(
                            descriptor,
                            scheduledAtUtc,
                            wakeUtc,
                            wakeUtc,
                            0,
                            Math.Max(
                                0,
                                _timeProvider.GetElapsedTime(
                                    nextDeadline,
                                    wakeTimestamp).TotalMilliseconds),
                            missedTotal,
                            skippedBusyTotal);
                        await _sink.PublishAsync(
                            timeoutResult,
                            busyExecution,
                            stoppingToken).ConfigureAwait(false);
                        nextDeadline = AdvanceDeadline(
                            nextDeadline,
                            basePeriodTicks,
                            consecutiveFailures,
                            wakeTimestamp,
                            ref missedTotal);
                        continue;
                    }

                    _ = await inFlight.ConfigureAwait(false);
                    inFlight = null;
                    collectionCancellation?.Dispose();
                    collectionCancellation = null;
                    if (slotHeld)
                    {
                        ReleaseConcurrency(optional);
                        slotHeld = false;
                    }
                }

                if (!await TryAcquireConcurrencyAsync(descriptor.Timeout, optional, stoppingToken)
                    .ConfigureAwait(false))
                {
                    var capacityTimestamp = _timeProvider.GetTimestamp();
                    var capacityUtc = _timeProvider.GetUtcNow();
                    skippedBusyTotal++;
                    consecutiveFailures++;
                    // Queue expiration is a scheduler status, not a Provider observation.
                    var capacityResult = ProviderResult.Failure(descriptor, capacityUtc,
                        AvailabilityStates.Timeout, StableErrorCodes.Timeout) with
                    {
                        ObservedAtUtc = null,
                    };
                    var capacityExecution = new ProviderExecution(descriptor, scheduledAtUtc,
                        capacityUtc, capacityUtc, 0,
                        Math.Max(0, _timeProvider.GetElapsedTime(nextDeadline, capacityTimestamp).TotalMilliseconds),
                        missedTotal, skippedBusyTotal)
                    {
                        CompletedTimestamp = capacityTimestamp,
                    };
                    await _sink.PublishAsync(capacityResult, capacityExecution, stoppingToken)
                        .ConfigureAwait(false);
                    nextDeadline = AdvanceDeadline(nextDeadline, basePeriodTicks,
                        consecutiveFailures, capacityTimestamp, ref missedTotal);
                    continue;
                }
                if (SamplingMode?.IsPaused(descriptor.GroupId) == true)
                {
                    ReleaseConcurrency(optional);
                    continue;
                }
                slotHeld = true;
                var startedTimestamp = _timeProvider.GetTimestamp();
                var startedAtUtc = _timeProvider.GetUtcNow();
                inFlightRevision = SamplingMode?.Revision;
                var context = new ProviderContext(
                    _timeProvider,
                    startedAtUtc,
                    startedTimestamp,
                    _logicalProcessorCount);
                collectionCancellation = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);
                collectionCancellation.CancelAfter(descriptor.Timeout);
                inFlight = CollectIsolatedAsync(
                    provider,
                    context,
                    collectionCancellation.Token);

                var timeout = Task.Delay(
                    descriptor.Timeout,
                    _timeProvider,
                    stoppingToken);
                var winner = await Task.WhenAny(inFlight, timeout)
                    .ConfigureAwait(false);
                var completedTimestamp = _timeProvider.GetTimestamp();
                var completedAtUtc = _timeProvider.GetUtcNow();
                ProviderResult result;

                if (winner == inFlight)
                {
                    result = await inFlight.ConfigureAwait(false);
                    inFlight = null;
                    collectionCancellation.Dispose();
                    collectionCancellation = null;
                    ReleaseConcurrency(optional);
                    slotHeld = false;
                    consecutiveFailures = IsSuccessful(result)
                        ? 0
                        : consecutiveFailures + 1;
                }
                else
                {
                    collectionCancellation.Cancel();
                    result = ProviderResult.Timeout(
                        descriptor,
                        completedAtUtc);
                    consecutiveFailures++;
                }

                var execution = new ProviderExecution(
                    descriptor,
                    scheduledAtUtc,
                    startedAtUtc,
                    completedAtUtc,
                    Math.Max(
                        0,
                        _timeProvider.GetElapsedTime(
                            startedTimestamp,
                            completedTimestamp).TotalMilliseconds),
                    Math.Max(
                        0,
                        _timeProvider.GetElapsedTime(
                            nextDeadline,
                            startedTimestamp).TotalMilliseconds),
                    missedTotal,
                    skippedBusyTotal)
                {
                    StartedTimestamp = startedTimestamp,
                    CompletedTimestamp = completedTimestamp,
                };
                result = result with { CollectionRevision = inFlightRevision };
                await _sink.PublishAsync(
                    result,
                    execution,
                    stoppingToken).ConfigureAwait(false);

                nextDeadline = AdvanceDeadline(
                    nextDeadline,
                    basePeriodTicks,
                    consecutiveFailures,
                    completedTimestamp,
                    ref missedTotal);
            }
        }
        finally
        {
            collectionCancellation?.Cancel();
            if (inFlight is not null)
            {
                try
                {
                    await inFlight.WaitAsync(TimeSpan.FromSeconds(1))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or TimeoutException)
                {
                    // Never release native Provider state while an
                    // uncancellable call may still be using it.
                    _outstandingCollections.TryAdd(
                        provider,
                        inFlight);
                }
            }

            collectionCancellation?.Dispose();
            if (slotHeld)
            {
                ReleaseConcurrency(optional);
            }
        }
    }

    private async Task<bool> TryAcquireConcurrencyAsync(
        TimeSpan timeout,
        bool optional,
        CancellationToken stoppingToken)
    {
        using var capacityCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        capacityCancellation.CancelAfter(timeout);
        var optionalHeld = false;
        var totalHeld = false;
        var acquired = false;
        try
        {
            if (optional)
            {
                await _optionalConcurrency!.WaitAsync(capacityCancellation.Token).ConfigureAwait(false);
                optionalHeld = true;
            }
            await _concurrency.WaitAsync(capacityCancellation.Token).ConfigureAwait(false);
            totalHeld = true;
            capacityCancellation.Token.ThrowIfCancellationRequested();
            acquired = true;
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (!acquired)
            {
                if (totalHeld) _concurrency.Release();
                if (optionalHeld) _optionalConcurrency!.Release();
            }
        }
    }

    private void ReleaseConcurrency(bool optional)
    {
        _concurrency.Release();
        if (optional) _optionalConcurrency!.Release();
    }

    private static bool IsSuccessful(ProviderResult result) =>
        result.Availability is
            AvailabilityStates.Available or AvailabilityStates.Partial;

    private long AdvanceDeadline(
        long previousDeadline,
        long basePeriodTicks,
        int consecutiveFailures,
        long now,
        ref long missedTotal)
    {
        var effectivePeriod = checked(
            basePeriodTicks *
            SchedulerMath.BackoffMultiplier(consecutiveFailures));
        var next = SchedulerMath.AdvanceAbsoluteDeadline(
            previousDeadline,
            effectivePeriod,
            now,
            out var missed);
        missedTotal = checked(missedTotal + missed);
        return next;
    }

    private async Task DelayUntilAsync(
        long deadline,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetTimestamp();
        if (deadline <= now)
        {
            return;
        }

        await Task.Delay(
            _timeProvider.GetElapsedTime(now, deadline),
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProviderResult> CollectIsolatedAsync(
        IMetricProvider provider,
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(
                async () => await provider.CollectAsync(
                    context,
                    cancellationToken).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProviderResult.Timeout(
                provider.Descriptor,
                context.TimeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            return ProviderResult.Failure(
                provider.Descriptor,
                context.TimeProvider.GetUtcNow(),
                ExceptionClassifier.Availability(exception),
                ExceptionClassifier.StableCode(exception));
        }
    }

    private static async ValueTask DisposeProviderAsync(
        IMetricProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync()
                .ConfigureAwait(false);
        }
        else if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static async Task DisposeAfterCollectionAsync(
        IMetricProvider provider,
        Task<ProviderResult> collection)
    {
        try
        {
            _ = await collection.ConfigureAwait(false);
        }
        catch
        {
            // CollectIsolatedAsync normally converts failures to a
            // result. Shutdown cleanup still must not race the call.
        }

        try
        {
            await DisposeProviderAsync(provider)
                .ConfigureAwait(false);
        }
        catch
        {
            // Deferred cleanup cannot report through a disposed host.
            // The process boundary remains the final native cleanup.
        }
    }
}
