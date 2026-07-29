namespace PerfMonitor.Core;

public sealed class ProviderScheduler : IAsyncDisposable
{
    private readonly IReadOnlyList<IMetricProvider> _providers;
    private readonly IProviderResultSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly int _logicalProcessorCount;
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _runners = [];
    private int _started;

    public ProviderScheduler(
        IEnumerable<IMetricProvider> providers,
        IProviderResultSink sink,
        int maxConcurrency,
        TimeProvider? timeProvider = null,
        int? logicalProcessorCount = null)
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
    }

    public IReadOnlyList<ProviderDescriptor> Descriptors =>
        _providers.Select(provider => provider.Descriptor).ToArray();

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
        CancellationTokenSource? collectionCancellation = null;
        var slotHeld = false;

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

                if (inFlight is not null)
                {
                    if (!inFlight.IsCompleted)
                    {
                        skippedBusyTotal++;
                        var timeoutResult = ProviderResult.Timeout(
                            descriptor,
                            wakeUtc);
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
                        _concurrency.Release();
                        slotHeld = false;
                    }
                }

                await _concurrency.WaitAsync(stoppingToken)
                    .ConfigureAwait(false);
                slotHeld = true;
                var startedTimestamp = _timeProvider.GetTimestamp();
                var startedAtUtc = _timeProvider.GetUtcNow();
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
                    _concurrency.Release();
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
                    skippedBusyTotal);
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
                }
            }

            collectionCancellation?.Dispose();
            if (slotHeld)
            {
                _concurrency.Release();
            }
        }
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
}
