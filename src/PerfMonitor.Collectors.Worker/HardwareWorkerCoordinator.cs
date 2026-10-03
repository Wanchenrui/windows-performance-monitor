using PerfMonitor.Core;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal sealed class HardwareWorkerCoordinator
{
    private static readonly TimeSpan ReuseWindow =
        TimeSpan.FromMilliseconds(250);
    private readonly IHardwareWorkerClient _client;
    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private int _remainingLeases;
    private WorkerResponse? _cached;
    private long _cachedTimestamp;
    private bool _disposed;

    public HardwareWorkerCoordinator(
        IHardwareWorkerClient client,
        int leases)
    {
        if (leases <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leases));
        }

        _client = client;
        _remainingLeases = leases;
    }

    public async ValueTask<WorkerResponse> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        await _collectionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CanReuse(context))
            {
                return _cached!;
            }

            var response = await _client.CollectAsync(
                cancellationToken).ConfigureAwait(false);
            _cached = response;
            _cachedTimestamp = context.Timestamp;
            return response;
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    public async ValueTask ReleaseAsync()
    {
        var remaining = Interlocked.Decrement(
            ref _remainingLeases);
        if (remaining < 0)
        {
            throw new InvalidOperationException(
                "Hardware Worker lease released more than once.");
        }
        if (remaining != 0)
        {
            return;
        }

        await _collectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cached = null;
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    private bool CanReuse(ProviderContext context)
    {
        if (_cached is null ||
            context.Timestamp < _cachedTimestamp)
        {
            return false;
        }

        return context.TimeProvider.GetElapsedTime(
            _cachedTimestamp,
            context.Timestamp) <= ReuseWindow;
    }
}
