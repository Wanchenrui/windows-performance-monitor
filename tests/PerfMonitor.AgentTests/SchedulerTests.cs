using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SchedulerTests
{
    [TestMethod]
    public void AbsoluteDeadlineSkipsExpiredPeriodsWithoutDrift()
    {
        var next = SchedulerMath.AdvanceAbsoluteDeadline(
            previousDeadline: 0,
            periodTicks: 10,
            now: 35,
            out var missed);

        Assert.AreEqual(40L, next);
        Assert.AreEqual(3L, missed);
    }

    [TestMethod]
    public void FailureBackoffIsBounded()
    {
        Assert.AreEqual(1, SchedulerMath.BackoffMultiplier(0));
        Assert.AreEqual(2, SchedulerMath.BackoffMultiplier(1));
        Assert.AreEqual(4, SchedulerMath.BackoffMultiplier(2));
        Assert.AreEqual(8, SchedulerMath.BackoffMultiplier(3));
        Assert.AreEqual(8, SchedulerMath.BackoffMultiplier(100));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task TimedOutProviderNeverOverlapsItself()
    {
        var descriptor = Descriptor("slow", 10, 15);
        var concurrent = 0;
        var maximumConcurrent = 0;
        var provider = new DelegateProvider(
            descriptor,
            async (context, _) =>
            {
                var active = Interlocked.Increment(ref concurrent);
                UpdateMaximum(ref maximumConcurrent, active);
                await Task.Delay(60).ConfigureAwait(false);
                Interlocked.Decrement(ref concurrent);
                return DelegateProvider.Available(
                    descriptor,
                    context.UtcNow);
            });
        var sink = new CaptureSink();
        await using var scheduler = new ProviderScheduler(
            [provider],
            sink,
            maxConcurrency: 1);

        scheduler.Start();
        await Task.Delay(180).ConfigureAwait(false);
        await scheduler.StopAsync().ConfigureAwait(false);

        Assert.AreEqual(1, maximumConcurrent);
        Assert.IsTrue(
            sink.Items.Any(
                item => item.Result.Availability ==
                    AvailabilityStates.Timeout));
        Assert.IsTrue(
            sink.Items.Any(
                item => item.Execution.SkippedBusyIntervalsTotal > 0));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task ProviderFailureDoesNotBlockHealthyProvider()
    {
        var healthyDescriptor = Descriptor("healthy", 15, 100);
        var failingDescriptor = Descriptor("failing", 15, 100);
        var healthy = new DelegateProvider(
            healthyDescriptor,
            (context, _) => Task.FromResult(
                DelegateProvider.Available(
                    healthyDescriptor,
                    context.UtcNow)));
        var failing = new DelegateProvider(
            failingDescriptor,
            (_, _) => Task.FromException<ProviderResult>(
                new InvalidDataException("injected")));
        var sink = new CaptureSink();
        await using var scheduler = new ProviderScheduler(
            [healthy, failing],
            sink,
            maxConcurrency: 2);

        scheduler.Start();
        await Task.Delay(160).ConfigureAwait(false);
        await scheduler.StopAsync().ConfigureAwait(false);

        Assert.IsTrue(
            sink.Items.Any(
                item => item.Result.GroupId == "healthy" &&
                    item.Result.Availability ==
                        AvailabilityStates.Available));
        Assert.IsTrue(
            sink.Items.Any(
                item => item.Result.GroupId == "failing" &&
                    item.Result.Errors.Any(
                        error => error.ErrorCode == "invalid_data")));
    }

    private static ProviderDescriptor Descriptor(
        string group,
        int periodMilliseconds,
        int timeoutMilliseconds) =>
        new(
            group,
            $"test.{group}.v1",
            TimeSpan.FromMilliseconds(periodMilliseconds),
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            "user",
            "low");

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref maximum,
                    candidate,
                    observed) == observed)
            {
                return;
            }
        }
    }
}
