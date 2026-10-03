using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class AdaptiveSchedulingSchedulerTests
{
    [TestMethod]
    [Timeout(10000)]
    public async Task LongWaitReturnsToFastWithinOneBasePeriodWithoutChangingBasicCollection()
    {
        var time = new AdaptiveTimeProvider();
        var hardware = Descriptor(GroupIds.Gpu);
        var cpu = Descriptor(GroupIds.SystemCpu);
        var mode = new ProviderSamplingMode { HardwarePeriodMultiplier = 12 };
        var hardwareTimes = new ConcurrentQueue<long>();
        var cpuTimes = new ConcurrentQueue<long>();
        var sink = new CaptureSink();
        await using var scheduler = new ProviderScheduler(
            [Provider(hardware, hardwareTimes), Provider(cpu, cpuTimes)], sink, 2, time,
            reservedGroupIds: [GroupIds.SystemCpu]) { SamplingMode = mode };
        scheduler.Start();
        await WaitUntilAsync(() => sink.Items.Count == 2 && time.HasTimerAt(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => sink.Items.Count(item => item.Result.GroupId == GroupIds.SystemCpu) == 2);
        // Finish the fake-time wake continuations before moving the shared clock
        // again; real collection work is already complete at this boundary.
        await Task.Delay(20);
        Assert.AreEqual(1, hardwareTimes.Count);
        var restoredAt = time.GetTimestamp();
        mode.HardwarePeriodMultiplier = 1;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => cpuTimes.Count == 3 && hardwareTimes.Count == 2);
        Assert.IsTrue(hardwareTimes.Last() - restoredAt <= TimeSpan.TicksPerSecond);
        CollectionAssert.AreEqual(new[] { 0L, TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond }, cpuTimes.ToArray());
        await WaitUntilAsync(() => sink.Items.Count(item => item.Result.GroupId == GroupIds.Gpu) == 2);
        CollectionAssert.AreEqual(new[] { 12.0, 1.0 }, sink.Items.Where(item => item.Result.GroupId == GroupIds.Gpu)
            .Select(item => item.Execution.ExpectedPeriodSeconds!.Value).ToArray());
        await scheduler.StopAsync();
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task SlowerPeriodDefersExistingDeadlineWithoutBurstingOrChangingPauseOwner()
    {
        var time = new AdaptiveTimeProvider();
        var hardware = Descriptor(GroupIds.Sensors);
        var mode = new ProviderSamplingMode();
        var calls = new ConcurrentQueue<long>();
        var sink = new CaptureSink();
        await using var scheduler = new ProviderScheduler([Provider(hardware, calls)], sink, 1, time)
        { SamplingMode = mode };
        scheduler.Start();
        await WaitUntilAsync(() => calls.Count == 1 && time.HasTimerAt(1));
        mode.HardwarePeriodMultiplier = 3;
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => time.HasTimerAt(2));
        Assert.AreEqual(1, calls.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => time.HasTimerAt(3));
        Assert.AreEqual(1, calls.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => calls.Count == 2);
        CollectionAssert.AreEqual(new[] { 0L, 3 * TimeSpan.TicksPerSecond }, calls.ToArray());
        Assert.IsFalse(mode.OptionalHardwarePaused);
        await scheduler.StopAsync();
    }

    private static ProviderDescriptor Descriptor(string group) =>
        new(group, "fake." + group, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "user", "low");

    private static DelegateProvider Provider(ProviderDescriptor descriptor, ConcurrentQueue<long> calls) =>
        new(descriptor, (context, _) =>
        {
            calls.Enqueue(context.Timestamp);
            return Task.FromResult(DelegateProvider.Available(descriptor, context.UtcNow));
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
