using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

/// <summary>One session setting: pause only GPU and temperature requests.</summary>
public sealed class ProviderSamplingMode
{
    private long _state;

    public bool OptionalHardwarePaused
    {
        get => (Volatile.Read(ref _state) & 1) != 0;
        set
        {
            long current;
            long next;
            do
            {
                current = Volatile.Read(ref _state);
                if (((current & 1) != 0) == value) return;
                next = ((current & ~1L) + 2) | (value ? 1L : 0L);
            }
            while (Interlocked.CompareExchange(ref _state, next, current) != current);
        }
    }

    internal long Revision => Volatile.Read(ref _state) >> 1;

    public bool IsPaused(string groupId) => OptionalHardwarePaused &&
        (groupId is GroupIds.Gpu or GroupIds.Sensors);

    internal bool Rejects(string groupId, long? collectionRevision)
    {
        var state = Volatile.Read(ref _state);
        return (groupId is GroupIds.Gpu or GroupIds.Sensors) &&
            ((state & 1) != 0 || collectionRevision is { } revision && revision != (state >> 1));
    }
}
