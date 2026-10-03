using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SchedulerIsolationTests
{
    [TestMethod]
    [Timeout(10000)]
    public async Task MultipleNonCooperativeProvidersLeaveCapacityForBasicGroups()
    {
        var tracker = new ConcurrencyTracker();
        var blockers = Enumerable.Range(0, 3).Select(index => new BlockingProvider(
            Descriptor($"blocked.{index}", 20, 25), tracker)).ToArray();
        var cpu = Healthy(Descriptor(GroupIds.SystemCpu, 30, 200), tracker);
        var memory = Healthy(Descriptor(GroupIds.Memory, 30, 200), tracker);
        var sink = new CaptureSink();
        var scheduler = new ProviderScheduler([.. blockers, cpu, memory], sink, 3,
            reservedGroupIds: [GroupIds.SystemCpu, GroupIds.Memory]);
        try
        {
            scheduler.Start();
            await Task.WhenAll(blockers.Take(2).Select(provider => provider.Entered.Task))
                .WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(400);
            foreach (var group in new[] { GroupIds.SystemCpu, GroupIds.Memory })
            {
                var samples = sink.Items.Where(item => item.Result.GroupId == group &&
                    item.Result.Availability == AvailabilityStates.Available).ToArray();
                Assert.IsTrue(samples.Length >= 5, $"Basic group stopped: {group}");
                Assert.IsTrue(samples.Zip(samples.Skip(1), (left, right) =>
                    right.Execution.StartedAtUtc - left.Execution.StartedAtUtc)
                    .All(gap => gap <= TimeSpan.FromMilliseconds(200)));
            }
            Assert.IsTrue(sink.Items.Any(item => item.Result.GroupId == blockers[2].Descriptor.GroupId &&
                IsCapacityTimeout(item.Result, item.Execution)));
            Assert.AreEqual(0, blockers[2].Calls);
            Assert.IsTrue(tracker.Maximum <= 3);
            Assert.IsTrue(blockers.All(provider => provider.MaximumActive <= 1));
            var stop = Stopwatch.StartNew();
            await scheduler.StopAsync();
            Assert.IsTrue(stop.Elapsed < TimeSpan.FromMilliseconds(1500));
            Assert.AreEqual(2, tracker.Active);
            Assert.IsTrue(blockers.Take(2).All(provider => !provider.Disposed));
        }
        finally
        {
            foreach (var provider in blockers) provider.Release.TrySetResult();
            await scheduler.DisposeAsync();
            await Task.WhenAll(blockers.Select(provider => provider.DisposedSignal.Task))
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.AreEqual(0, tracker.Active);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task CapacityTimeoutHasNoObservationAndCannotResolveAnActiveCpuDiagnosis()
    {
        var tracker = new ConcurrencyTracker();
        var blocker = new BlockingProvider(Descriptor("blocked", 20, 25), tracker);
        var cpuDescriptor = Descriptor(GroupIds.SystemCpu, 30, 40);
        var cpuCalls = 0;
        var cpu = new DelegateProvider(cpuDescriptor, (context, _) =>
        {
            Interlocked.Increment(ref cpuCalls);
            return Task.FromResult(CpuResult(cpuDescriptor, context.UtcNow, 0));
        });
        var memory = Healthy(Descriptor(GroupIds.Memory, 30, 200), tracker);
        var assembler = new SnapshotAssembler([blocker.Descriptor, cpuDescriptor, memory.Descriptor]);
        var now = TimeProvider.System.GetUtcNow();
        var timestamp = TimeProvider.System.GetTimestamp();
        await assembler.PublishAsync(CpuResult(cpuDescriptor, now, 95), new ProviderExecution(
            cpuDescriptor, now, now, now, 0, 0, 0, 0)
        {
            StartedTimestamp = timestamp,
            CompletedTimestamp = timestamp,
        }, CancellationToken.None);
        var initial = assembler.Read();
        var policy = DiagnosticPolicy.Default with
        {
            HighCpu = DiagnosticPolicy.Default.HighCpu with
            {
                ActivateDebounceSeconds = 0,
                RecoverDebounceSeconds = 0,
                CooldownSeconds = 0,
            },
        };
        var evaluator = new DiagnosticEvaluator(policy);
        Assert.IsTrue(evaluator.Evaluate(initial).Any(item =>
            item.RuleId == DiagnosticRuleIds.HighCpu && item.State == DiagnosticStates.Active));
        var sink = new ForwardingSink(assembler);
        var scheduler = new ProviderScheduler([blocker, cpu, memory], sink, 2,
            reservedGroupIds: [GroupIds.Memory]);
        try
        {
            scheduler.Start();
            await sink.CpuCapacityTimeout.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var item = sink.Items.First(item => item.Result.GroupId == GroupIds.SystemCpu);
            Assert.AreEqual(AvailabilityStates.Timeout, item.Result.Availability);
            Assert.AreEqual(StableErrorCodes.Timeout, item.Result.Errors.Single().ErrorCode);
            Assert.IsNull(item.Result.ObservedAtUtc);
            Assert.IsNull(item.Result.Data);
            Assert.AreEqual(0d, item.Execution.DurationMilliseconds);
            Assert.IsNull(item.Execution.StartedTimestamp);
            Assert.IsNotNull(item.Execution.CompletedTimestamp);
            Assert.IsTrue(item.Execution.SkippedBusyIntervalsTotal > 0);
            var snapshot = assembler.Read();
            var group = snapshot.Groups[GroupIds.SystemCpu];
            Assert.IsTrue(snapshot.Sequence > initial.Sequence);
            Assert.IsTrue(group.ObservationSequence > initial.Groups[GroupIds.SystemCpu].ObservationSequence);
            Assert.AreEqual(FreshnessStates.WarmingUp, group.Freshness);
            Assert.IsNull(group.ObservedAtUtc);
            Assert.IsNull(group.ObservedElapsedSeconds);
            Assert.IsNull(group.Data);
            Assert.IsFalse(evaluator.Evaluate(snapshot).Any(diagnostic =>
                diagnostic.RuleId == DiagnosticRuleIds.HighCpu && diagnostic.State == DiagnosticStates.Resolved));
            Assert.AreEqual(0, cpuCalls);
        }
        finally
        {
            blocker.Release.TrySetResult();
            await scheduler.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task SingleSlotKeepsSharedCompatibilityAndRecoversAfterBlockedCallEnds()
    {
        var tracker = new ConcurrencyTracker();
        var blocker = new BlockingProvider(Descriptor("blocked", 20, 25), tracker);
        var basic = Healthy(Descriptor(GroupIds.Memory, 30, 40), tracker);
        var sink = new CaptureSink();
        var scheduler = new ProviderScheduler([blocker, basic], sink, 1,
            reservedGroupIds: [GroupIds.Memory]);
        try
        {
            scheduler.Start();
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => sink.Items.Any(item => item.Result.GroupId == GroupIds.Memory &&
                IsCapacityTimeout(item.Result, item.Execution)));
            Assert.IsFalse(sink.Items.Any(item => item.Result.GroupId == GroupIds.Memory &&
                item.Result.Availability == AvailabilityStates.Available));
            Assert.AreEqual(1, blocker.Calls);
            blocker.Release.TrySetResult();
            await WaitForAsync(() => sink.Items.Any(item => item.Result.GroupId == GroupIds.Memory &&
                item.Result.Availability == AvailabilityStates.Available));
            Assert.AreEqual(1, tracker.Maximum);
            Assert.AreEqual(1, blocker.MaximumActive);
        }
        finally
        {
            blocker.Release.TrySetResult();
            await scheduler.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task StopCancelsQueuesAtBothGatesWithoutStartingWaitingCalls()
    {
        var tracker = new ConcurrencyTracker();
        var reserved = new[]
        {
            new BlockingProvider(Descriptor("reserved.0", 20, 5000), tracker),
            new BlockingProvider(Descriptor("reserved.1", 20, 5000), tracker),
        };
        var optional = new[]
        {
            new BlockingProvider(Descriptor("optional.0", 20, 5000), tracker),
            new BlockingProvider(Descriptor("optional.1", 20, 5000), tracker),
        };
        var waitingReserved = new BlockingProvider(Descriptor("reserved.waiting", 20, 5000), tracker);
        var all = reserved.Concat(optional).Append(waitingReserved).ToArray();
        var scheduler = new ProviderScheduler(all, new CaptureSink(), 2,
            reservedGroupIds: reserved.Append(waitingReserved).Select(provider => provider.Descriptor.GroupId));
        try
        {
            scheduler.Start();
            await Task.WhenAll(reserved.Select(provider => provider.Entered.Task))
                .WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(25);
            await scheduler.StopAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(1500));
            Assert.IsTrue(optional.Append(waitingReserved).All(provider => provider.Calls == 0));
            Assert.AreEqual(2, tracker.Maximum);
            Assert.IsTrue(reserved.All(provider => !provider.Disposed));
        }
        finally
        {
            foreach (var provider in all) provider.Release.TrySetResult();
            await scheduler.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task OptionalFailureReturnsItsCapacityAndBackoffAllowsOtherCollectors()
    {
        var tracker = new ConcurrencyTracker();
        var failingDescriptor = Descriptor("optional.failing", 20, 100);
        var failing = new DelegateProvider(failingDescriptor, (_, _) =>
            Task.FromException<ProviderResult>(new InvalidDataException("injected")));
        var optional = Healthy(Descriptor("optional.healthy", 20, 100), tracker);
        var basic = Healthy(Descriptor(GroupIds.Memory, 20, 100), tracker);
        var sink = new CaptureSink();
        await using var scheduler = new ProviderScheduler([failing, optional, basic], sink, 2,
            reservedGroupIds: [GroupIds.Memory]);
        scheduler.Start();
        await WaitForAsync(() => sink.Items.Count(item => item.Result.GroupId == "optional.healthy") >= 5);
        await scheduler.StopAsync();
        Assert.IsTrue(sink.Items.Any(item => item.Result.GroupId == failingDescriptor.GroupId &&
            item.Result.Errors.Any(error => error.ErrorCode == StableErrorCodes.InvalidData)));
        Assert.IsTrue(sink.Items.Any(item => item.Result.GroupId == GroupIds.Memory));
        Assert.IsTrue(tracker.Maximum <= 2);
    }

    [TestMethod]
    public void ReservationRejectsUnknownGroups()
    {
        var provider = Healthy(Descriptor(GroupIds.Memory, 20, 100), new ConcurrencyTracker());
        Assert.ThrowsExactly<ArgumentException>(() => new ProviderScheduler([provider], new CaptureSink(), 2,
            reservedGroupIds: [GroupIds.SystemCpu]));
    }

    private static ProviderDescriptor Descriptor(string group, int period, int timeout) => new(
        group, $"test.{group}.v1", TimeSpan.FromMilliseconds(period), TimeSpan.FromMilliseconds(timeout), "user", "low");

    private static bool IsCapacityTimeout(ProviderResult result, ProviderExecution execution) =>
        result.Availability == AvailabilityStates.Timeout && result.ObservedAtUtc is null &&
        result.Errors.Any(error => error.ErrorCode == StableErrorCodes.Timeout) &&
        execution.StartedTimestamp is null && execution.DurationMilliseconds == 0;

    private static DelegateProvider Healthy(ProviderDescriptor descriptor, ConcurrencyTracker tracker) => new(
        descriptor, (context, _) =>
        {
            tracker.Enter();
            try { return Task.FromResult(DelegateProvider.Available(descriptor, context.UtcNow)); }
            finally { tracker.Exit(); }
        });

    private static ProviderResult CpuResult(ProviderDescriptor descriptor, DateTimeOffset utc, double cpu) => new(
        descriptor.GroupId, descriptor.ProviderId, utc, AvailabilityStates.Available, ProviderCoverage.Complete, [],
        new JsonObject { ["metrics"] = new JsonObject { [MetricIds.SystemCpuUtilization] = MetricJson.Value(cpu, Units.Percent, "test.cpu") } });

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(2)) Assert.Fail("Scheduler condition did not complete within budget.");
            await Task.Delay(10);
        }
    }

    private sealed class ConcurrencyTracker
    {
        public int Active;
        public int Maximum;
        public void Enter() => UpdateMaximum(ref Maximum, Interlocked.Increment(ref Active));
        public void Exit() => Interlocked.Decrement(ref Active);
        public static void UpdateMaximum(ref int maximum, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
                if (observed >= value) return;
            }
            while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
        }
    }

    private sealed class BlockingProvider(ProviderDescriptor descriptor, ConcurrencyTracker tracker) : IMetricProvider, IDisposable
    {
        private int _active;
        public int Calls;
        public int MaximumActive;
        public bool Disposed;
        public ProviderDescriptor Descriptor { get; } = descriptor;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ProviderResult> CollectAsync(ProviderContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            ConcurrencyTracker.UpdateMaximum(ref MaximumActive, Interlocked.Increment(ref _active));
            tracker.Enter();
            Entered.TrySetResult();
            try
            {
                await Release.Task; // Intentional non-cooperation for isolation/lifetime assertions.
                return DelegateProvider.Available(Descriptor, context.UtcNow);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                tracker.Exit();
            }
        }
        public void Dispose()
        {
            Disposed = true;
            DisposedSignal.TrySetResult();
        }
    }

    private sealed class ForwardingSink(IProviderResultSink target) : IProviderResultSink
    {
        public ConcurrentQueue<(ProviderResult Result, ProviderExecution Execution)> Items { get; } = new();
        public TaskCompletionSource CpuCapacityTimeout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask PublishAsync(ProviderResult result, ProviderExecution execution, CancellationToken cancellationToken)
        {
            await target.PublishAsync(result, execution, cancellationToken);
            Items.Enqueue((result, execution));
            if (result.GroupId == GroupIds.SystemCpu && IsCapacityTimeout(result, execution))
                CpuCapacityTimeout.TrySetResult();
        }
    }
}
