using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopWatchedProcessTests
{
    private static readonly DesktopProcessIdentity First = new(42, 134_091_432_123_456_789);

    [TestMethod]
    public void PinsDeduplicateExactInstanceAndAllowExplicitRemoval()
    {
        var state = State(new(Row(First, 10)));
        var list = new WatchedProcessList();
        var process = SnapshotPresentation.FindProcess(state, First)!;
        Assert.IsTrue(list.TryPin(state, process));
        Assert.IsTrue(list.TryPin(state, process));
        Assert.AreEqual(1, list.Entries.Count);
        Assert.IsTrue(list.Contains("first", First));
        Assert.IsTrue(list.Remove("first", First));
        Assert.AreEqual(0, list.Entries.Count);
    }

    [TestMethod]
    public void ListHasTwelveSlotsWithoutSilentlyEvictingPins()
    {
        var rows = new JsonArray();
        for (var index = 0; index <= WatchedProcessList.MaxEntries; index++) rows.Add(Row(First with { Pid = 42 + index }, index));
        var state = State(rows); var list = new WatchedProcessList();
        for (var index = 0; index < WatchedProcessList.MaxEntries; index++)
            Assert.IsTrue(list.TryPin(state, SnapshotPresentation.FindProcess(state, First with { Pid = 42 + index })!));
        Assert.IsFalse(list.TryPin(state, SnapshotPresentation.FindProcess(state, First with { Pid = 54 })!));
        Assert.AreEqual(12, list.Entries.Count);
        Assert.AreEqual(First, list.Entries[0].Identity);
        list.Remove("first", First);
        Assert.IsTrue(list.TryPin(state, SnapshotPresentation.FindProcess(state, First with { Pid = 54 })!));
        Assert.AreEqual(12, list.Entries.Count);
    }

    [TestMethod]
    public void PinOutsideRankingStillUsesFullSnapshot()
    {
        var rows = new JsonArray(Row(First, 0));
        for (var index = 1; index < 20; index++) rows.Add(Row(First with { Pid = 42 + index }, index));
        var state = State(rows); var list = new WatchedProcessList();
        Assert.IsTrue(list.TryPin(state, SnapshotPresentation.FindProcess(state, First)!));
        Assert.IsFalse(SnapshotPresentation.ProcessRows(state).Any(process => process.Identity == First));
        var view = list.Present(state).Single();
        Assert.IsTrue(view.CanSelect);
        Assert.AreEqual(0d, view.Process!.Cpu);
    }

    [TestMethod]
    public void SameNameAndReusedPidNeverReplacePinnedIdentity()
    {
        var state = State(new(Row(First, 10))); var list = new WatchedProcessList();
        list.TryPin(state, SnapshotPresentation.FindProcess(state, First)!);
        var reused = First with { CreationTimeTicks = First.CreationTimeTicks + 1 };
        var next = State(new(Row(reused, 90)));
        Assert.IsFalse(list.Present(next).Single().CanSelect);
        Assert.IsNull(list.Present(next).Single().Process);
        Assert.IsTrue(list.TryPin(next, SnapshotPresentation.FindProcess(next, reused)!));
        Assert.AreEqual(2, list.Entries.Count);
        Assert.AreEqual(First, list.Entries[0].Identity);
        Assert.AreEqual(reused, list.Entries[1].Identity);
    }

    [TestMethod]
    public void NewAgentRequiresNewPinAndOldSessionCanAlwaysBeRemoved()
    {
        var first = State(new(Row(First, 10))); var list = new WatchedProcessList();
        list.TryPin(first, SnapshotPresentation.FindProcess(first, First)!);
        var next = State(new(Row(First, 70)), instance: "second");
        Assert.IsFalse(list.Present(next).Single().CanSelect);
        Assert.IsTrue(list.TryPin(next, SnapshotPresentation.FindProcess(next, First)!));
        var views = list.Present(next);
        Assert.IsFalse(views[0].CanSelect);
        Assert.IsTrue(views[1].CanSelect);
        Assert.IsTrue(list.Remove("first", First));
        Assert.AreEqual("second", list.Entries.Single().AgentInstanceId);
    }

    [TestMethod]
    public void MissingStaleAndDisconnectedObservationsNeverShowLiveZero()
    {
        var state = State(new(Row(First, 10))); var list = new WatchedProcessList();
        list.TryPin(state, SnapshotPresentation.FindProcess(state, First)!);
        foreach (var unavailable in new[]
        {
            State(new JsonArray()),
            State(new(Row(First, 80)), freshness: FreshnessStates.Stale),
            state with { Status = DesktopConnectionStatus.Reconnecting },
            state with { InstanceId = "second" },
        })
        {
            var view = list.Present(unavailable).Single();
            Assert.IsNull(view.Process);
            Assert.IsFalse(view.CanSelect);
        }
        var missingCpu = list.Present(State(new(Row(First, null)))).Single();
        Assert.IsTrue(missingCpu.CanSelect);
        Assert.IsNull(missingCpu.Process!.Cpu);
    }

    [TestMethod]
    public void PinningRequiresCurrentObservedIdentityAndListsAreDesktopLocal()
    {
        var state = State(new(Row(First, 10))); var process = SnapshotPresentation.FindProcess(state, First)!;
        var list = new WatchedProcessList();
        Assert.IsFalse(list.TryPin(state with { Status = DesktopConnectionStatus.Reconnecting }, process));
        Assert.IsFalse(list.TryPin(State(new(Row(First, 10)), freshness: FreshnessStates.Stale), process));
        Assert.IsFalse(list.TryPin(state, process with { Identity = First with { Pid = 99 } }));
        Assert.IsTrue(list.TryPin(state, process));
        Assert.AreEqual(0, new WatchedProcessList().Entries.Count);
    }

    private static JsonObject Row(DesktopProcessIdentity identity, double? cpu) => new()
    {
        ["identity"] = new JsonObject { ["pid"] = identity.Pid, ["creationTimeTicks"] = identity.CreationTimeTicks },
        ["name"] = "same-name", ["cpuReady"] = cpu is not null,
        ["metrics"] = new JsonObject { [MetricIds.ProcessCpuNormalized] = new JsonObject { ["value"] = cpu } },
    };

    private static DesktopConnectionState State(JsonArray rows, string instance = "first", string freshness = FreshnessStates.Fresh)
    {
        var group = new SnapshotGroup("test", DateTimeOffset.UtcNow, AvailabilityStates.Available, freshness,
            ProviderCoverage.Complete, [], rows) { ObservationSequence = 1, ObservedElapsedSeconds = 0 };
        var snapshot = new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent, instance, 1, null, null,
            DateTimeOffset.UtcNow, 0, new("available", "fresh"), new(3600, 3600),
            new Dictionary<string, SnapshotGroup> { [GroupIds.Processes] = group }) { ElapsedSeconds = 0 };
        return new(DesktopConnectionStatus.Connected, instance, 0, snapshot, null);
    }
}
