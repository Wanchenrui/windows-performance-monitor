using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

/// <summary>
/// Receives an immutable snapshot without applying backpressure to provider
/// scheduling. Implementations must return immediately and perform any I/O on
/// their own bounded worker queue.
/// </summary>
public interface ISnapshotConsumer
{
    bool TryPublish(AgentSnapshot snapshot);
}

public interface IHistoryReader
{
    ValueTask<HistoryContract> QueryAsync(
        HistoryQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken);
}

public interface IDiagnosticEventSink
{
    bool TryPublishDiagnostic(DiagnosticEventContract diagnosticEvent);
}

public interface IDiagnosticEventReader
{
    ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken);
}

public sealed class SnapshotFanout
{
    private readonly IReadOnlyList<ISnapshotConsumer> _consumers;
    private long _consumerFailures;

    public SnapshotFanout(IEnumerable<ISnapshotConsumer> consumers)
    {
        _consumers = consumers.ToArray();
    }

    public long ConsumerFailures => Interlocked.Read(ref _consumerFailures);

    public void Publish(AgentSnapshot snapshot)
    {
        foreach (var consumer in _consumers)
        {
            try
            {
                _ = consumer.TryPublish(snapshot);
            }
            catch
            {
                // v0.5.0: downstream faults are isolated from the sampling
                // deadline. Consumers surface their own health separately.
                Interlocked.Increment(ref _consumerFailures);
            }
        }
    }
}
