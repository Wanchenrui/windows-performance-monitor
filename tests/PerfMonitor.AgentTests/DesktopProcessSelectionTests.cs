using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopProcessSelectionTests
{
    private static readonly DesktopProcessIdentity Identity = new(42, 134_091_432_123_456_789);

    [TestMethod]
    public void SelectedProcessUsesExactIdentityAndRemainsReadableOutsideTopTwelve()
    {
        var rows = new JsonArray(Row(Identity, 0));
        for (var index = 1; index < 20; index++) rows.Add(Row(new(42 + index, Identity.CreationTimeTicks + index), index));
        var state = State(1, 0, rows);
        Assert.IsFalse(SnapshotPresentation.ProcessRows(state).Any(row => row.Identity == Identity));
        var selected = SnapshotPresentation.FindProcess(state, Identity)!;
        Assert.AreEqual(Identity, selected.Identity);
        Assert.AreEqual(16L * 1024 * 1024, selected.PrivateBytes);
        Assert.AreEqual(32L * 1024 * 1024, selected.WorkingSetBytes);
        Assert.IsNull(SnapshotPresentation.FindProcess(state, Identity with { CreationTimeTicks = Identity.CreationTimeTicks + 1 }));
    }

    [TestMethod]
    public void SelectionStartsWithNextObservationAndHeldValuesDoNotAddHistory()
    {
        var initial = State(1, 0, new(Row(Identity, 10)));
        var buffer = Select(initial);
        buffer.Observe(initial);
        Assert.AreEqual(0, buffer.Points.Count);
        buffer.Observe(State(2, 2, new(Row(Identity, 15))));
        buffer.Observe(State(2, 3, new(Row(Identity, 50)), observedElapsed: 2));
        Assert.AreEqual(1, buffer.Points.Count);
        Assert.AreEqual(15d, buffer.Points[0].Value);
    }

    [TestMethod]
    public void ReusedPidDoesNotConnectToSelectedInstanceAndMissingValuesRemainNull()
    {
        var buffer = Select(State(1, 0, new(Row(Identity, 10))));
        buffer.Observe(State(2, 2, new(Row(Identity, 15))));
        buffer.Observe(State(3, 4, new(Row(Identity with { CreationTimeTicks = Identity.CreationTimeTicks + 1 }, 80))));
        Assert.IsNull(buffer.Points[^1].Value);
        buffer.Observe(State(4, 6, new(Row(Identity, null))));
        Assert.IsNull(buffer.Points[^1].Value);
        buffer.Observe(State(5, 8, new(Row(Identity, 20))));
        Assert.IsTrue(buffer.Points[^1].BreakBefore);
        Assert.AreEqual(20d, buffer.Points[^1].Value);
    }

    [TestMethod]
    public void HiddenGapBreaksLineAndAgentRestartRequiresNewSelection()
    {
        var buffer = Select(State(1, 0, new(Row(Identity, 10))));
        buffer.Observe(State(2, 2, new(Row(Identity, 15))));
        buffer.BreakContinuity();
        buffer.Observe(State(3, 4, new(Row(Identity, 20))));
        Assert.IsTrue(buffer.Points[^1].BreakBefore);
        var count = buffer.Points.Count;
        buffer.Observe(State(4, 6, new(Row(Identity, 80)), instance: "new-agent"));
        Assert.AreEqual(count, buffer.Points.Count);
        StringAssert.Contains(buffer.Status, "实例已更换");
    }

    [TestMethod]
    public void StaleCpuDoesNotBecomeANewValidProcessPoint()
    {
        var buffer = Select(State(1, 0, new(Row(Identity, 10))));
        buffer.Observe(State(2, 2, new(Row(Identity, 75)), freshness: FreshnessStates.Stale));
        Assert.AreEqual(1, buffer.Points.Count);
        Assert.IsNull(buffer.Points[0].Value);
        buffer.Observe(State(3, 4, new(Row(Identity, 20))));
        Assert.IsTrue(buffer.Points[^1].BreakBefore);
    }

    [TestMethod]
    public void SelectedWindowIsBoundedAndSwitchingSelectionDropsOldHistory()
    {
        var buffer = Select(State(1, 0, new(Row(Identity, 10))));
        for (var observation = 2; observation < 2500; observation++)
            buffer.Observe(State(observation, observation * .25, new(Row(Identity, 10))));
        Assert.IsTrue(buffer.Points.Count <= SelectedProcessTrendBuffer.MaxPoints);
        Assert.IsTrue(buffer.PayloadBytes <= SelectedProcessTrendBuffer.MaxPayloadBytes);
        Assert.IsTrue(buffer.Points[0].ElapsedSeconds >= buffer.EndElapsedSeconds - SelectedProcessTrendBuffer.RetainedSeconds);
        var other = Identity with { Pid = 88 };
        var state = State(2500, 625, new(Row(other, 40)));
        buffer.Select(state, SnapshotPresentation.FindProcess(state, other)!);
        Assert.AreEqual(0, buffer.Points.Count);
        Assert.AreEqual(0, buffer.PayloadBytes);
    }

    private static SelectedProcessTrendBuffer Select(DesktopConnectionState state)
    {
        var buffer = new SelectedProcessTrendBuffer();
        buffer.Select(state, SnapshotPresentation.FindProcess(state, Identity)!);
        return buffer;
    }

    private static JsonObject Row(DesktopProcessIdentity identity, double? cpu) => new()
    {
        ["identity"] = new JsonObject { ["pid"] = identity.Pid, ["creationTimeTicks"] = identity.CreationTimeTicks },
        ["name"] = "demo", ["cpuReady"] = cpu is not null,
        ["metrics"] = new JsonObject
        {
            [MetricIds.ProcessCpuNormalized] = new JsonObject { ["value"] = cpu },
            [MetricIds.ProcessPrivateBytes] = new JsonObject { ["value"] = 16L * 1024 * 1024 },
            [MetricIds.ProcessWorkingSetBytes] = new JsonObject { ["value"] = 32L * 1024 * 1024 },
        },
    };

    private static DesktopConnectionState State(long observation, double elapsed, JsonArray rows,
        string instance = "first", string freshness = FreshnessStates.Fresh, double? observedElapsed = null)
    {
        var group = new SnapshotGroup("test", DateTimeOffset.UtcNow, AvailabilityStates.Available,
            freshness, ProviderCoverage.Complete, [], rows)
            { ObservationSequence = observation, ObservedElapsedSeconds = observedElapsed ?? elapsed };
        var snapshot = new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent, instance, observation,
            null, null, DateTimeOffset.UtcNow, 0, new("available", "fresh"), new(3600, 3600),
            new Dictionary<string, SnapshotGroup> { [GroupIds.Processes] = group }) { ElapsedSeconds = elapsed };
        return new(DesktopConnectionStatus.Connected, instance, 0, snapshot, null);
    }
}
