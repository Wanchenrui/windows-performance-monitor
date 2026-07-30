using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

internal sealed class DelegateProvider : IMetricProvider
{
    private readonly Func<ProviderContext, CancellationToken, Task<ProviderResult>>
        _collect;

    public DelegateProvider(
        ProviderDescriptor descriptor,
        Func<ProviderContext, CancellationToken, Task<ProviderResult>> collect)
    {
        Descriptor = descriptor;
        _collect = collect;
    }

    public ProviderDescriptor Descriptor { get; }

    public async ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken) =>
        await _collect(context, cancellationToken).ConfigureAwait(false);

    public static ProviderResult Available(
        ProviderDescriptor descriptor,
        DateTimeOffset observedAtUtc) =>
        new(
            descriptor.GroupId,
            descriptor.ProviderId,
            observedAtUtc,
            AvailabilityStates.Available,
            ProviderCoverage.Complete,
            [],
            new JsonObject
            {
                ["metrics"] = new JsonObject
                {
                    ["test.metric"] = MetricJson.Value(
                        1.0,
                        "count",
                        "test.source"),
                },
            });
}

internal sealed class CaptureSink : IProviderResultSink
{
    public ConcurrentQueue<(ProviderResult Result, ProviderExecution Execution)>
        Items { get; } = new();

    public ValueTask PublishAsync(
        ProviderResult result,
        ProviderExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Items.Enqueue((result, execution));
        return ValueTask.CompletedTask;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan duration)
    {
        _utcNow += duration;
        _timestamp = checked(_timestamp + duration.Ticks);
    }
}
