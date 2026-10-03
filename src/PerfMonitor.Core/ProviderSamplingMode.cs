using PerfMonitor.Contracts;

namespace PerfMonitor.Core;

/// <summary>Session settings affecting only GPU and temperature requests.</summary>
public sealed class ProviderSamplingMode
{
    private long _state;
    private int _hardwarePeriodMultiplier = 1;

    // Independent from the manual pause epoch: changing a period does not
    // invalidate an otherwise valid in-flight observation.
    public int HardwarePeriodMultiplier
    {
        get => Volatile.Read(ref _hardwarePeriodMultiplier);
        set
        {
            ValidateMultiplier(value);
            Volatile.Write(ref _hardwarePeriodMultiplier, value);
        }
    }

    public bool TryChangeHardwarePeriodMultiplier(int expected, int desired)
    {
        ValidateMultiplier(expected);
        ValidateMultiplier(desired);
        return Interlocked.CompareExchange(ref _hardwarePeriodMultiplier, desired, expected) == expected;
    }

    private static void ValidateMultiplier(int value)
    {
        if (value is not (1 or 3 or 6 or 12))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    public TimeSpan GetEffectivePeriod(ProviderDescriptor descriptor) =>
        descriptor.GroupId is GroupIds.Gpu or GroupIds.Sensors
            ? TimeSpan.FromTicks(checked(descriptor.DefaultPeriod.Ticks * HardwarePeriodMultiplier))
            : descriptor.DefaultPeriod;

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
