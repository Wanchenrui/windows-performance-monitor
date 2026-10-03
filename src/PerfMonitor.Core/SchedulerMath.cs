namespace PerfMonitor.Core;

public static class SchedulerMath
{
    public static long ToTimestampTicks(
        TimeProvider timeProvider,
        TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period),
                "Provider period must be positive.");
        }

        var ticks = checked(
            (long)Math.Ceiling(
                period.TotalSeconds * timeProvider.TimestampFrequency));
        return Math.Max(1, ticks);
    }

    public static long AdvanceAbsoluteDeadline(
        long previousDeadline,
        long periodTicks,
        long now,
        out long missedIntervals)
    {
        if (periodTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(periodTicks));
        }

        var next = checked(previousDeadline + periodTicks);
        if (next > now)
        {
            missedIntervals = 0;
            return next;
        }

        missedIntervals = checked((now - next) / periodTicks + 1);
        return checked(next + missedIntervals * periodTicks);
    }

    public static int BackoffMultiplier(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return 1;
        }

        return 1 << Math.Min(consecutiveFailures, 3);
    }
}
