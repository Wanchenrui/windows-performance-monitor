using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;
using PerfMonitor.Diagnostics;
using PerfMonitor.Ipc.NamedPipes;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DiagnosticStateOrderingTests
{
    [TestMethod]
    public async Task DatabaseAndAgentPreferResolvedAcrossUtcRollback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"perf-monitor-event-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new SqliteHistoryStore(new() { DatabasePath = Path.Combine(directory, "history.db") });
            await store.StartAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var active = Event("current", 1, now, DiagnosticStates.Active);
            var resolved = Event("current", 2, now.AddMinutes(-5), DiagnosticStates.Resolved);
            Assert.IsTrue(store.TryPublishDiagnostic(active));
            Assert.IsTrue(store.TryPublishDiagnostic(resolved));
            await store.WaitForDiagnosticsIdleAsync(TimeSpan.FromSeconds(10));
            var query = new DiagnosticQueryContract
            {
                FromEpochMs = now.AddMinutes(-10).ToUnixTimeMilliseconds(), ToEpochMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
                MaxEvents = 1,
            };
            var persisted = await store.QueryDiagnosticsAsync(query, "current", CancellationToken.None);
            Assert.AreEqual(DiagnosticStates.Resolved, persisted.Events.Single().State);
            Assert.IsTrue(persisted.Truncated);
            ProviderDescriptor[] descriptors = [new(GroupIds.SystemCpu, "test", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "user", "low")];
            var service = new AgentQueryService(new SnapshotAssembler(descriptors, instanceId: "current"),
                store, store, new FixedReader(Response("current", [resolved])), null!,
                DiagnosticPolicy.Default.ToCapabilities(), descriptors, PipeEndpoint.ForCurrentUser(), TimeSpan.FromSeconds(1));
            var merged = await service.QueryDiagnosticsAsync(query, CancellationToken.None);
            Assert.AreEqual(DiagnosticStates.Resolved, merged.Events.Single().State);
            Assert.AreEqual(2L, merged.Events.Single().ObservationSequence);
            Assert.IsTrue(merged.Truncated);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void DesktopUsesCurrentEpochAndObservationOrderForLatestStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var response = Response("current",
        [
            Event("current", 2, now.AddMinutes(-5), DiagnosticStates.Resolved),
            Event("old", 500, now.AddDays(1), DiagnosticStates.Active),
            Event("current", 1, now, DiagnosticStates.Active),
        ]);
        Assert.AreEqual("诊断：查询区间未返回当前活动告警", SnapshotPresentation.DiagnosticsSummary(response));
        response = response with { Events = [Event("current", 3, now.AddMinutes(-7), DiagnosticStates.Active)] };
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(response), "1 个当前活动告警");
    }

    [TestMethod]
    public void CachedDrawerQueryBecomesHistoricalWhenDisconnectedOrAgentInstanceChanges()
    {
        var response = Response("old", [Event("old", 1, DateTimeOffset.UtcNow, DiagnosticStates.Active)]);
        var connected = new DesktopConnectionState(DesktopConnectionStatus.Connected, "old", 0, null, null);
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(response, connected), "最近查询结果");
        foreach (var state in new[] { connected with { Status = DesktopConnectionStatus.Reconnecting },
            connected with { InstanceId = "new" } })
        {
            var summary = SnapshotPresentation.DiagnosticsSummary(response, state);
            StringAssert.Contains(summary, "历史诊断");
            Assert.IsFalse(summary.Contains("当前活动", StringComparison.Ordinal));
        }
        Assert.AreEqual("本次查询未返回活动告警", SnapshotPresentation.DiagnosticsSummary(Response("old", []), connected));
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(Response("old", []) with { Truncated = true }, connected), "不完整");
    }

    [TestMethod]
    public void DesktopDoesNotTreatLegacyHistoryOrTruncationAsCurrentNormalState()
    {
        var legacy = Event("current", 1, DateTimeOffset.UtcNow, DiagnosticStates.Active) with { ObservationSequence = null };
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(Response("current", [legacy])), "状态未知");
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(Response("new", [legacy])), "仅有历史事件");
        StringAssert.Contains(SnapshotPresentation.DiagnosticsSummary(Response("current", []) with { Truncated = true }), "状态不完整");
    }

    private static DiagnosticsContract Response(string instance, IReadOnlyList<DiagnosticEventContract> events) =>
        new() { ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, InstanceId = instance,
            Query = new(), EventCount = events.Count, Events = events };

    private static DiagnosticEventContract Event(string instance, long sequence, DateTimeOffset time, string state) =>
        new()
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
            EventId = $"{instance}-{sequence}-{state}", InstanceId = instance,
            RuleId = DiagnosticRuleIds.HighCpu, RuleVersion = "1.0.0", Severity = DiagnosticSeverities.Warning,
            State = state, SubjectId = "system", ObservationSequence = sequence,
            Hysteresis = new() { ActivateWhen = "high", RecoverWhen = "low" }, Debounce = new(),
            EvidenceWindow = new() { FromUtc = time, ToUtc = time, SampleCount = 1 },
            FirstSeenUtc = time, LastSeenUtc = time, Confidence = 1,
            Evidence = [new() { ObservedAtUtc = time, SubjectId = "system", Signal = "cpu", Condition = "breach" }],
        };

    private sealed class FixedReader(DiagnosticsContract response) : IDiagnosticEventReader
    {
        public ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(DiagnosticQueryContract query,
            string responseInstanceId, CancellationToken cancellationToken) => ValueTask.FromResult(response);
    }
}
