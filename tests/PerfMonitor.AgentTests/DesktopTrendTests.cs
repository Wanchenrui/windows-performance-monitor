using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopTrendTests
{
    [TestMethod]
    public void HeldValuesAndHistoricalFramesNeverBecomeNewSamples()
    {
        var buffer = new DesktopTrendBuffer();
        var first = State(1, 0, 23);
        buffer.Observe(first);
        buffer.Observe(first with { LatestSnapshot = first.LatestSnapshot! with { ElapsedSeconds = 1 } });
        buffer.Observe(first with { Status = DesktopConnectionStatus.Reconnecting });
        Assert.AreEqual(1, buffer.Points(MetricIds.SystemCpuUtilization).Count);
        buffer.Observe(State(100, 2, 30));
        Assert.IsTrue(buffer.Points(MetricIds.SystemCpuUtilization)[1].BreakBefore);
    }

    [TestMethod]
    public void StaleHeldObservationAndHiddenResumeBreakContinuity()
    {
        var buffer = new DesktopTrendBuffer(); var first = State(1, 0, 20);
        buffer.Observe(first);
        var stale = first.LatestSnapshot!.Groups.ToDictionary(item => item.Key, item => item.Value with { Freshness = FreshnessStates.Stale });
        buffer.Observe(first with { LatestSnapshot = first.LatestSnapshot with { Groups = stale, ElapsedSeconds = 1 } });
        buffer.Observe(State(20, 2, 21));
        buffer.BreakContinuity(); buffer.Observe(State(30, 3, 22));
        Assert.AreEqual(3, buffer.Points(MetricIds.SystemCpuUtilization).Count);
        Assert.IsTrue(buffer.Points(MetricIds.SystemCpuUtilization).Skip(1).All(point => point.BreakBefore));
    }

    [TestMethod]
    public void ZeroRemainsRealWhileUnavailableMissingAndLongGapsAreNotConnected()
    {
        var buffer = new DesktopTrendBuffer();
        buffer.Observe(State(1, 0, 0));
        buffer.Observe(State(2, 1, 99, availability: AvailabilityStates.Unavailable));
        buffer.Observe(State(3, 2, 10)); buffer.Observe(State(4, 10, 11));
        var points = buffer.Points(MetricIds.SystemCpuUtilization);
        Assert.AreEqual(0d, points[0].Value); Assert.IsNull(points[1].Value);
        var segments = TrendChart.Segments(points, 0, 20);
        Assert.AreEqual(3, segments.Count);
        Assert.IsTrue(segments.All(segment => segment.Count == 1));
    }

    [TestMethod]
    public void UtcRollbackAndLegitimateProviderSequenceJumpsKeepMonotonicObservations()
    {
        var buffer = new DesktopTrendBuffer(); var first = State(1, 0, 20);
        buffer.Observe(first);
        var second = State(500, 1, 21);
        var earlierUtc = first.LatestSnapshot!.Groups[GroupIds.SystemCpu].ObservedAtUtc!.Value.AddHours(-1);
        second = second with { LatestSnapshot = second.LatestSnapshot! with { Groups = second.LatestSnapshot!.Groups.ToDictionary(
            item => item.Key, item => item.Value with { ObservedAtUtc = earlierUtc }) } };
        buffer.Observe(second);
        var points = buffer.Points(MetricIds.SystemCpuUtilization);
        Assert.AreEqual(2, points.Count); Assert.IsFalse(points[1].BreakBefore);
        Assert.IsTrue(points[1].ObservedAtUtc < points[0].ObservedAtUtc);
        Assert.AreEqual(2, TrendChart.Segments(points, 0, 2).Single().Count);
    }

    [TestMethod]
    public void DeliveryLossBreaksAndNewInstanceClearsPriorCurves()
    {
        var buffer = new DesktopTrendBuffer(); var first = State(1, 0, 20);
        buffer.Observe(first with { LatestSnapshot = first.LatestSnapshot! with { DeliverySequence = 1 } });
        var next = State(2, 1, 30);
        buffer.Observe(next with { LatestSnapshot = next.LatestSnapshot! with { DeliverySequence = 3 } });
        Assert.IsTrue(buffer.Points(MetricIds.SystemCpuUtilization)[1].BreakBefore);
        var restarted = State(1, 0, 0, instance: "second"); buffer.Observe(restarted);
        Assert.AreEqual("second", buffer.InstanceId); Assert.AreEqual(1, buffer.Points(MetricIds.SystemCpuUtilization).Count);
        Assert.AreEqual(0d, buffer.Points(MetricIds.SystemCpuUtilization)[0].Value);
    }

    [TestMethod]
    public void MissingObservationMetadataDoesNotInventTimeOrSamples()
    {
        var buffer = new DesktopTrendBuffer(); var state = State(1, 0, 50);
        state = state with { LatestSnapshot = state.LatestSnapshot! with { Groups = state.LatestSnapshot!.Groups.ToDictionary(
            item => item.Key, item => item.Value with { ObservationSequence = null, ObservedElapsedSeconds = null }) } };
        buffer.Observe(state);
        Assert.AreEqual(0, buffer.Points(MetricIds.SystemCpuUtilization).Count);
    }

    [TestMethod]
    public void HoverReportsActualObservationQualityAndNeverInventsAGapValue()
    {
        var utc = DateTimeOffset.UtcNow;
        TrendPoint[] primary = [new(0, utc, 0, "部分可用", true, 1), new(10, utc.AddSeconds(10), null, "不可用", true, 2)];
        TrendPoint[] secondary = [new(0, utc, 1024, "部分可用", true, 1)];
        var first = TrendChart.HoverAt(primary, secondary, 0.1, "下载", "上传", false);
        Assert.AreEqual(primary[0], first.Point);
        StringAssert.Contains(first.Text, "部分可用"); StringAssert.Contains(first.Text, "上传：1 KiB/s");
        var gap = TrendChart.HoverAt(primary, secondary, 5, "下载", "上传", false);
        Assert.IsNull(gap.Point); StringAssert.Contains(gap.Text, "没有实际观测");
        var unavailable = TrendChart.HoverAt(primary, secondary, 10, "下载", "上传", false);
        StringAssert.Contains(unavailable.Text, "缺测"); StringAssert.Contains(unavailable.Text, "不可用");
    }

    [TestMethod]
    public void PointCountRetentionAndPayloadBudgetsStayBoundedAcrossAllSeries()
    {
        var buffer = new DesktopTrendBuffer();
        for (var index = 1; index <= 1200; index++) buffer.Observe(State(index, index / 100d, 10,
            freshness: new string('旧', 120)));
        Assert.IsTrue(buffer.PayloadBytes <= DesktopTrendBuffer.MaxPayloadBytes);
        Assert.IsTrue(DesktopTrendBuffer.Metrics.All(metric => buffer.Points(metric.Metric).Count <= DesktopTrendBuffer.MaxPointsPerSeries));
        buffer.Observe(State(2000, 1000, 0));
        Assert.IsTrue(DesktopTrendBuffer.Metrics.All(metric => buffer.Points(metric.Metric).Count == 1));
    }

    private static DesktopConnectionState State(long observation, double elapsed, double? value,
        string availability = AvailabilityStates.Available, string freshness = FreshnessStates.Fresh, string instance = "first")
    {
        var groups = DesktopTrendBuffer.Metrics.GroupBy(metric => metric.Group).ToDictionary(group => group.Key, group =>
        {
            var metrics = new JsonObject();
            foreach (var metric in group) metrics[metric.Metric] = new JsonObject { ["value"] = value };
            return new SnapshotGroup("test", DateTimeOffset.UtcNow, availability, freshness, ProviderCoverage.Complete, [],
                new JsonObject { ["metrics"] = metrics }) { ObservedElapsedSeconds = elapsed, ObservationSequence = observation };
        });
        return new(DesktopConnectionStatus.Connected, instance, 0, new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent,
            instance, observation, null, null, DateTimeOffset.UtcNow, 0, new("available", "fresh"), new(3600, 3600), groups)
            { ElapsedSeconds = elapsed }, null);
    }
}
