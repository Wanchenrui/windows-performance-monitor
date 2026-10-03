using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SoakSimulationTests
{
    [TestMethod]
    [Timeout(15000)]
    public void SeventyTwoHoursOfDeadlinesRemainAbsoluteAndBounded()
    {
        const int seconds = 72 * 60 * 60;
        const long periodTicks = TimeSpan.TicksPerSecond;
        long deadline = 0;
        long totalMissed = 0;

        for (var second = 0; second < seconds; second++)
        {
            deadline = SchedulerMath.AdvanceAbsoluteDeadline(
                deadline,
                periodTicks,
                deadline,
                out var missed);
            totalMissed += missed;
        }

        Assert.AreEqual(
            seconds * periodTicks,
            deadline);
        Assert.AreEqual(0L, totalMissed);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task SeventyTwoHoursOfSnapshotsDoNotRetainHistory()
    {
        // v0.4.0: 以 259,200 次真实发布检查缓存是否误留历史快照。
        const int cycles = 72 * 60 * 60;
        const int warmupCycles = 4_096;
        const long retainedGrowthLimitBytes = 16 * 1024 * 1024;
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var descriptor = new ProviderDescriptor(
            "soak",
            "test.soak.v1",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(500),
            "user",
            "low");
        var assembler = new SnapshotAssembler(
            [descriptor],
            time,
            "33333333333333333333333333333333");

        for (var cycle = 0; cycle < warmupCycles; cycle++)
        {
            await PublishOnceAsync(
                assembler,
                descriptor,
                time).ConfigureAwait(false);
        }

        var retainedAfterWarmup = GC.GetTotalMemory(
            forceFullCollection: true);
        for (var cycle = warmupCycles; cycle < cycles; cycle++)
        {
            await PublishOnceAsync(
                assembler,
                descriptor,
                time).ConfigureAwait(false);
        }

        var retainedAfterSoak = GC.GetTotalMemory(
            forceFullCollection: true);
        var retainedGrowth = Math.Max(
            0,
            retainedAfterSoak - retainedAfterWarmup);
        Assert.AreEqual((long)cycles, assembler.Read().Sequence);
        Assert.IsTrue(
            retainedGrowth <= retainedGrowthLimitBytes,
            $"Retained heap grew by {retainedGrowth} bytes.");
    }

    private static async ValueTask PublishOnceAsync(
        SnapshotAssembler assembler,
        ProviderDescriptor descriptor,
        ManualTimeProvider time)
    {
        var now = time.GetUtcNow();
        await assembler.PublishAsync(
            DelegateProvider.Available(descriptor, now),
            new ProviderExecution(
                descriptor,
                now,
                now,
                now,
                0,
                0,
                0,
                0),
            CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(1));
    }
}
