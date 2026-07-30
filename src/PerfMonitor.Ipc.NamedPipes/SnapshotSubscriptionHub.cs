using System.Collections.Concurrent;
using System.Threading.Channels;
using PerfMonitor.Core;

namespace PerfMonitor.Ipc.NamedPipes;

public sealed class SnapshotSubscriptionHub :
    ISnapshotConsumer,
    IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SnapshotSubscription>
        _subscriptions = new(StringComparer.Ordinal);
    private int _disposed;

    public int SubscriberCount => _subscriptions.Count;

    public SnapshotSubscription Subscribe()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        while (true)
        {
            var subscription = new SnapshotSubscription(
                Guid.NewGuid().ToString("N"),
                Remove);
            if (_subscriptions.TryAdd(
                subscription.SubscriptionId,
                subscription))
            {
                return subscription;
            }
        }
    }

    public bool TryPublish(AgentSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.TryPublish(snapshot);
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Complete();
        }

        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    private void Remove(string subscriptionId)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
    }
}

public sealed class SnapshotSubscription : IAsyncDisposable
{
    private readonly Channel<AgentSnapshot> _channel;
    private readonly Action<string> _onDispose;
    private int _disposed;

    internal SnapshotSubscription(
        string subscriptionId,
        Action<string> onDispose)
    {
        SubscriptionId = subscriptionId;
        _onDispose = onDispose;
        _channel = Channel.CreateBounded<AgentSnapshot>(
            new BoundedChannelOptions(1)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }

    public string SubscriptionId { get; }

    public ChannelReader<AgentSnapshot> Reader => _channel.Reader;

    internal void TryPublish(AgentSnapshot snapshot)
    {
        if (_channel.Writer.TryWrite(snapshot))
        {
            return;
        }

        _channel.Reader.TryRead(out _);
        _channel.Writer.TryWrite(snapshot);
    }

    internal void Complete() => _channel.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
            _onDispose(SubscriptionId);
        }

        return ValueTask.CompletedTask;
    }
}
