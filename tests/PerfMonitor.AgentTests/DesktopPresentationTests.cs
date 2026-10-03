using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopPresentationTests
{
    [TestMethod]
    public void BlockedUiRetainsOneCallbackAndRendersOnlyLatestState()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var rendered = new List<string>();
        var renderer = new LatestStateRenderer<string>(callbacks.Enqueue, rendered.Add);
        renderer.SetEnabled(true);
        Parallel.For(0, 10000, index => renderer.Publish(index.ToString()));
        renderer.Publish("latest");
        Assert.AreEqual(1, callbacks.Count);
        Assert.IsTrue(callbacks.TryDequeue(out var callback));
        callback!();
        CollectionAssert.AreEqual(new[] { "latest" }, rendered);
        Assert.AreEqual(0, callbacks.Count);
    }

    [TestMethod]
    public void UpdatesDuringRenderScheduleOneFollowUpWithLatestState()
    {
        var callbacks = new Queue<Action>();
        var rendered = new List<string>();
        LatestStateRenderer<string>? renderer = null;
        renderer = new(callbacks.Enqueue, state =>
        {
            rendered.Add(state);
            if (state != "first") return;
            renderer!.Publish("intermediate");
            renderer.Publish("latest");
            Assert.AreEqual(0, callbacks.Count);
        });
        renderer.SetEnabled(true);
        renderer.Publish("first");
        callbacks.Dequeue()();
        Assert.AreEqual(1, callbacks.Count);
        callbacks.Dequeue()();
        CollectionAssert.AreEqual(new[] { "first", "latest" }, rendered);
        Assert.AreEqual(0, callbacks.Count);
    }

    [TestMethod]
    public void HiddenWindowSkipsPendingRenderAndRestoresLatestSnapshot()
    {
        var callbacks = new Queue<Action>();
        var rendered = new List<string>();
        var renderer = new LatestStateRenderer<string>(callbacks.Enqueue, rendered.Add);
        renderer.SetEnabled(true);
        renderer.Publish("before hide");
        renderer.SetEnabled(false);
        callbacks.Dequeue()();
        for (var index = 0; index < 1000; index++) renderer.Publish(index.ToString());
        Assert.AreEqual(0, callbacks.Count);
        Assert.AreEqual(0, rendered.Count);
        renderer.SetEnabled(true);
        Assert.AreEqual(1, callbacks.Count);
        callbacks.Dequeue()();
        CollectionAssert.AreEqual(new[] { "999" }, rendered);
        renderer.SetEnabled(false);
        renderer.SetEnabled(true);
        callbacks.Dequeue()();
        CollectionAssert.AreEqual(new[] { "999", "999" }, rendered);
    }

    [TestMethod]
    public void ClosingDiscardsQueuedAndFutureUpdates()
    {
        var callbacks = new Queue<Action>();
        var rendered = new List<string>();
        var renderer = new LatestStateRenderer<string>(callbacks.Enqueue, rendered.Add);
        renderer.SetEnabled(true);
        renderer.Publish("queued");
        renderer.Close();
        renderer.Publish("late");
        callbacks.Dequeue()();
        renderer.SetEnabled(true);
        Assert.AreEqual(0, rendered.Count);
        Assert.AreEqual(0, callbacks.Count);
    }

    [TestMethod]
    public void UnavailableAndMetricFailureNeverExposeStoredNumericValue()
    {
        foreach (var availability in new[]
        {
            AvailabilityStates.Unavailable, AvailabilityStates.NotSupported,
            AvailabilityStates.PermissionDenied, AvailabilityStates.Timeout, AvailabilityStates.Error,
            "future_state",
        })
        {
            var state = State(Group(availability: availability));
            Assert.IsNull(Read(state));
            Assert.IsFalse(SnapshotPresentation.Group(state, GroupIds.SystemCpu).IsHealthy);
        }
        var failedMetric = State(Group(availability: AvailabilityStates.Partial,
            errors: [new(StableErrorCodes.InvalidData, MetricIds.SystemCpuUtilization)]));
        Assert.IsNull(Read(failedMetric));
        StringAssert.Contains(SnapshotPresentation.CardQuality(failedMetric, GroupIds.SystemCpu,
            MetricIds.SystemCpuUtilization), "无效采样");
    }

    [TestMethod]
    public void ZeroIsValidButMissingNonFiniteAndWarmingMetricsAreNot()
    {
        Assert.AreEqual(0d, Read(State(Group(value: 0))));
        Assert.IsNull(Read(State(Group(value: null))));
        Assert.IsNull(Read(State(Group(value: double.NaN))));
        Assert.IsNull(Read(State(Group(freshness: FreshnessStates.WarmingUp))));
        var firstRate = State(Group(sampleReady: false));
        Assert.IsNull(Read(firstRate));
        StringAssert.Contains(SnapshotPresentation.Group(firstRate, GroupIds.SystemCpu).Status, "预热中");
        StringAssert.Contains(SnapshotPresentation.Group(firstRate, GroupIds.SystemCpu).Detail, "相邻有效采样");
    }

    [TestMethod]
    public void StaleAndDisconnectedNumbersRemainExplicitlyHistorical()
    {
        var stale = State(Group(freshness: FreshnessStates.Stale));
        Assert.AreEqual(23.5d, Read(stale));
        var quality = SnapshotPresentation.Group(stale, GroupIds.SystemCpu);
        StringAssert.Contains(quality.Status, "陈旧");
        Assert.IsFalse(quality.IsHealthy);
        var disconnected = State(Group()) with
        {
            Status = DesktopConnectionStatus.Reconnecting,
            ErrorCode = "agent_disconnected",
        };
        quality = SnapshotPresentation.Group(disconnected, GroupIds.SystemCpu);
        Assert.IsTrue(quality.Status.StartsWith("历史快照 · 连接中断", StringComparison.Ordinal));
        StringAssert.Contains(quality.Detail, "Agent 已断开");
        StringAssert.Contains(quality.Detail, "观测 ");
        StringAssert.Contains(quality.Detail, "等待新快照");
        Assert.IsFalse(quality.IsHealthy);
        var newInstanceWithoutSnapshot = State(Group()) with { InstanceId = "new-instance" };
        Assert.IsTrue(SnapshotPresentation.Group(newInstanceWithoutSnapshot, GroupIds.SystemCpu).Status
            .StartsWith("历史快照 · 实例已更换", StringComparison.Ordinal));
        var missing = SnapshotPresentation.Group(disconnected, GroupIds.Gpu);
        Assert.IsTrue(missing.Status.StartsWith("历史快照 · 连接中断", StringComparison.Ordinal));
        StringAssert.Contains(missing.Status, "缺测");
        StringAssert.Contains(missing.Detail, "等待新快照");
        var unavailable = State(Group(availability: AvailabilityStates.NotSupported)) with
            { Status = DesktopConnectionStatus.Reconnecting };
        var unavailableQuality = SnapshotPresentation.CardQuality(unavailable, GroupIds.SystemCpu,
            MetricIds.SystemCpuUtilization);
        Assert.IsTrue(unavailableQuality.StartsWith("历史快照 · 连接中断", StringComparison.Ordinal));
        StringAssert.Contains(unavailableQuality, "不支持");
        StringAssert.Contains(unavailableQuality, "指标缺测");
        Assert.IsNull(Read(unavailable));
    }

    [TestMethod]
    public void ProcessDrawerDoesNotExposeUnavailableOrUnreadyCpuAsNormalAndBoundsRows()
    {
        var rows = new JsonArray();
        for (var index = 0; index < 20; index++) rows.Add(new JsonObject { ["name"] = "process" + index,
            ["cpuReady"] = true, ["metrics"] = new JsonObject { [MetricIds.ProcessCpuNormalized] = new JsonObject { ["value"] = index } } });
        var group = Group() with { Data = rows };
        var state = State(group);
        state = state with { LatestSnapshot = state.LatestSnapshot! with { Groups = new Dictionary<string, SnapshotGroup> { [GroupIds.Processes] = group } } };
        var presentation = SnapshotPresentation.ProcessRows(state);
        Assert.AreEqual(12, presentation.Count); Assert.AreEqual(19d, presentation[0].Cpu);
        var denied = state with { LatestSnapshot = state.LatestSnapshot with { Groups = new Dictionary<string, SnapshotGroup>
            { [GroupIds.Processes] = group with { Availability = AvailabilityStates.PermissionDenied } } } };
        Assert.IsTrue(SnapshotPresentation.ProcessRows(denied).All(row => row.Cpu is null));
        var warming = state with { LatestSnapshot = state.LatestSnapshot with { Groups = new Dictionary<string, SnapshotGroup>
            { [GroupIds.Processes] = group with { Freshness = FreshnessStates.WarmingUp } } } };
        Assert.IsTrue(SnapshotPresentation.ProcessRows(warming).All(row => row.Cpu is null));
    }

    [TestMethod]
    public void PartialCoverageAndAbsentGroupsAreVisibleWithReasons()
    {
        var group = Group(availability: AvailabilityStates.Partial) with
        {
            Coverage = new("limited", 4, 3, 1,
                new Dictionary<string, int> { [StableErrorCodes.AccessDenied] = 1 }),
        };
        var state = State(group);
        var quality = SnapshotPresentation.Group(state, GroupIds.SystemCpu);
        Assert.AreEqual(23.5d, Read(state));
        StringAssert.Contains(quality.Status, "部分可用");
        StringAssert.Contains(quality.Status, "覆盖有限");
        StringAssert.Contains(quality.Detail, "可读 3/4");
        StringAssert.Contains(quality.Detail, "访问被拒绝");
        StringAssert.Contains(SnapshotPresentation.Group(state, GroupIds.Gpu).Status, "缺测");
        Assert.AreEqual(12, SnapshotPresentation.Groups(state).Count);
        Assert.IsFalse(SnapshotPresentation.Group(state, GroupIds.Gpu).IsHealthy);
    }

    private static double? Read(DesktopConnectionState state) =>
        SnapshotPresentation.ReadMetric(state.LatestSnapshot!, GroupIds.SystemCpu, MetricIds.SystemCpuUtilization);

    private static SnapshotGroup Group(string availability = AvailabilityStates.Available,
        string freshness = FreshnessStates.Fresh, double? value = 23.5,
        bool sampleReady = true, IReadOnlyList<ProviderError>? errors = null) =>
        new(ProviderIds.SystemCpu, DateTimeOffset.UtcNow, availability, freshness,
            ProviderCoverage.Complete, errors ?? [], new JsonObject
            {
                ["sampleReady"] = sampleReady,
                ["metrics"] = new JsonObject
                {
                    [MetricIds.SystemCpuUtilization] = new JsonObject
                    {
                        ["value"] = value is null ? null : JsonValue.Create(value.Value),
                    },
                },
            });

    private static DesktopConnectionState State(SnapshotGroup group) =>
        new(DesktopConnectionStatus.Connected, "instance", 0,
            new(ContractVersions.V1, ProductVersions.Agent, "instance", 1,
                null, null, DateTimeOffset.UtcNow, 0, new("available", "fresh"), new(3600, 3600),
                new Dictionary<string, SnapshotGroup> { [GroupIds.SystemCpu] = group }), null);
}
