using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopRangeExportTests
{
    private static readonly DateTimeOffset Utc = new(2026, 10, 3, 1, 0, 0, TimeSpan.Zero);
    private static readonly DesktopProcessIdentity Identity = new(42, 134_091_432_123_456_789);

    [TestMethod]
    public void TrendUsesInclusiveMonotonicRangeAndDetachesFirstPointWithoutUtcSorting()
    {
        var buffer = new DesktopTrendBuffer();
        buffer.Observe(State(1, 0, 1));
        buffer.Observe(State(2, 39, 2));
        buffer.Observe(State(3, 40, 3));
        buffer.Observe(State(4, 41, 4, observedUtc: Utc.AddHours(-1)));
        var final = State(5, 100, 5);
        buffer.Observe(final);
        var result = DesktopRangeExport.CreateTrend(buffer, ResourceView.Cpu, 60, final, Utc);
        var trend = result.Trend!;
        Assert.AreEqual(40d, trend.Range.FromElapsedSeconds);
        Assert.AreEqual(100d, trend.Range.ToElapsedSeconds);
        CollectionAssert.AreEqual(new[] { 40d, 41d, 100d }, trend.Series[0].Points.Select(point => point.ElapsedSeconds).ToArray());
        Assert.IsTrue(trend.Series[0].Points[0].BreakBefore);
        Assert.IsFalse(trend.Series[0].Points[1].BreakBefore);
        Assert.IsTrue(trend.Series[0].Points[1].ObservedAtUtc < trend.Series[0].Points[0].ObservedAtUtc);
        Assert.IsFalse(buffer.Points(MetricIds.SystemCpuUtilization)[2].BreakBefore);
    }

    [TestMethod]
    public void NetworkAndDiskExportBothLinesWithNullQualityAndRealZero()
    {
        foreach (var resource in new[] { ResourceView.Network, ResourceView.Disk })
        {
            var buffer = new DesktopTrendBuffer();
            buffer.Observe(State(1, 0, 0));
            buffer.Observe(State(2, 1, null));
            var state = State(3, 2, 55, FreshnessStates.Stale);
            buffer.Observe(state);
            var result = DesktopRangeExport.CreateTrend(buffer, resource, 300, state, Utc).Trend!;
            Assert.AreEqual(2, result.Series.Count);
            foreach (var series in result.Series)
            {
                Assert.AreEqual(Units.BytePerSecond, series.Unit);
                Assert.AreEqual(3, series.PointCount);
                Assert.AreEqual(0d, series.Points[0].Value);
                Assert.IsNull(series.Points[1].Value);
                Assert.IsNull(series.Points[2].Value);
                StringAssert.Contains(series.Points[2].Quality, "陈旧");
                Assert.IsTrue(series.Points[2].BreakBefore);
                Assert.AreEqual(3L, series.Points[2].ObservationSequence);
            }
        }
    }

    [TestMethod]
    public void FrozenTrendDoesNotChangeAfterBufferRestartsAndReportsDisconnect()
    {
        var buffer = new DesktopTrendBuffer();
        var state = State(1, 15, 27);
        buffer.Observe(state);
        var disconnected = state with { Status = DesktopConnectionStatus.Reconnecting, ErrorCode = "agent_disconnected" };
        var result = DesktopRangeExport.CreateTrend(buffer, ResourceView.Memory, 60, disconnected, Utc.AddHours(1)).Trend!;
        buffer.Observe(State(1, 0, 88, instanceId: "new-session"));
        Assert.AreEqual("session", result.AgentInstanceId);
        Assert.AreEqual(27d, result.Series[0].Points.Single().Value);
        Assert.AreEqual(15d, result.Range.ToElapsedSeconds);
        Assert.AreEqual("Reconnecting", result.Connection.Status);
        Assert.AreEqual("agent_disconnected", result.Connection.ErrorCode);
        StringAssert.Contains(result.CoverageNote, "隐藏");
        StringAssert.Contains(result.CoverageNote, "完整原始质量");
    }

    [TestMethod]
    public void MissingGroupsAreEmptySeriesWithoutInventedObservations()
    {
        var buffer = new DesktopTrendBuffer();
        var state = State(1, 10, 20);
        state = state with { LatestSnapshot = state.LatestSnapshot! with { Groups = new Dictionary<string, SnapshotGroup>() } };
        buffer.Observe(state);
        var result = DesktopRangeExport.CreateTrend(buffer, ResourceView.Cpu, 60, state, Utc).Trend!;
        Assert.AreEqual(0, result.Series[0].PointCount);
        Assert.AreEqual(0d, result.Range.FromElapsedSeconds);
        Assert.AreEqual(10d, result.Range.ToElapsedSeconds);
    }

    [TestMethod]
    public void ProcessExportKeepsSelectedInstanceAndPostSelectionScopeAcrossPidReuse()
    {
        var buffer = new SelectedProcessTrendBuffer();
        var initial = ProcessState(1, 0, Identity, 10);
        buffer.Select(initial, SnapshotPresentation.FindProcess(initial, Identity)!);
        buffer.Observe(ProcessState(2, 2, Identity, 0));
        var reused = ProcessState(3, 4, Identity with { CreationTimeTicks = Identity.CreationTimeTicks + 1 }, 80);
        buffer.Observe(reused);
        var result = DesktopRangeExport.CreateProcessTrend(buffer, 60, reused, Utc).Trend!;
        buffer.Clear();
        Assert.AreEqual("session", result.AgentInstanceId);
        Assert.AreEqual(Identity.Pid, result.Process!.Pid);
        Assert.AreEqual(Identity.CreationTimeTicks, result.Process.CreationTimeTicks);
        Assert.AreEqual("demo", result.Process.Name);
        Assert.AreEqual(2, result.Series[0].PointCount);
        Assert.AreEqual(0d, result.Series[0].Points[0].Value);
        Assert.IsNull(result.Series[0].Points[1].Value);
        StringAssert.Contains(result.CoverageNote, "选中后");
    }

    [TestMethod]
    public void DiagnosticsFreezeQueryEventsAndEvidenceAndPreserveBoundaryMetadata()
    {
        var rules = new List<string> { DiagnosticRuleIds.HighCpu };
        var evidence = new List<DiagnosticEvidenceContract> { Event(Utc).Evidence[0] };
        var events = new List<DiagnosticEventContract> { Event(Utc) with { Evidence = evidence }, Event(Utc.AddMinutes(5)) };
        var response = Response(events) with { Truncated = true, Query = Response(events).Query with { RuleIds = rules } };
        var result = DesktopRangeExport.CreateDiagnostics(response, Utc).Diagnostics!;
        rules.Clear(); evidence.Clear(); events.Clear();
        Assert.IsNull(result.StorageAvailable);
        Assert.IsTrue(result.Response.Truncated);
        Assert.AreEqual(200, result.Response.Query.MaxEvents);
        Assert.AreEqual(2, result.Response.Events.Count);
        Assert.AreEqual(1, result.Response.Events[0].Evidence.Count);
        Assert.AreEqual(DiagnosticRuleIds.HighCpu, result.Response.Query.RuleIds.Single());
        Assert.AreEqual(Utc.ToUnixTimeMilliseconds(), result.Response.Query.FromEpochMs);
        Assert.AreEqual(Utc.AddMinutes(5).ToUnixTimeMilliseconds(), result.Response.Query.ToEpochMs);
        StringAssert.Contains(result.RangeSemantics, "Inclusive");
        StringAssert.Contains(result.CoverageNote, "空结果不代表");
        Assert.AreEqual(42L, result.Response.Events[0].ObservationSequence);
        Assert.IsNull(result.Response.Events[0].Evidence[0].Value);
    }

    [TestMethod]
    public void InvalidRangesOutOfScopeEventsAndNonFiniteEvidenceAreRejected()
    {
        var state = State(1, 0, 20);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DesktopRangeExport.CreateTrend(new(), ResourceView.Cpu, 61, state, Utc));
        Assert.ThrowsExactly<InvalidOperationException>(() => DesktopRangeExport.CreateProcessTrend(new(), 60, state, Utc));
        Assert.ThrowsExactly<InvalidDataException>(() => DesktopRangeExport.CreateDiagnostics(Response([Event(Utc.AddMilliseconds(-1))]), Utc));
        Assert.ThrowsExactly<InvalidDataException>(() => DesktopRangeExport.CreateDiagnostics(Response([]) with { EventCount = 1 }, Utc));
        Assert.ThrowsExactly<InvalidDataException>(() => DesktopRangeExport.CreateDiagnostics(Response([Event(Utc), Event(Utc)]) with
            { Query = Response([]).Query with { MaxEvents = 1 } }, Utc));
        var item = Event(Utc);
        var invalid = item with { Evidence = [item.Evidence[0] with { Value = double.NaN }] };
        Assert.ThrowsExactly<InvalidDataException>(() => DesktopRangeExport.CreateDiagnostics(Response([invalid]), Utc));
    }

    [TestMethod]
    public async Task WriterPublishesCompleteJsonAndRetainsNullMissingValues()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "export.json");
            await File.WriteAllTextAsync(path, "previous content");
            var export = DesktopRangeExport.CreateDiagnostics(Response([Event(Utc)]), Utc);
            await DesktopRangeExport.WriteAsync(path, export);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var diagnostics = json.RootElement.GetProperty("diagnostics");
            Assert.AreEqual("1", json.RootElement.GetProperty("schemaVersion").GetString());
            Assert.AreEqual(JsonValueKind.Null, diagnostics.GetProperty("storageAvailable").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, diagnostics.GetProperty("response").GetProperty("events")[0]
                .GetProperty("evidence")[0].GetProperty("value").ValueKind);
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        });
    }

    [TestMethod]
    public async Task CancellationAndSerializationFailureDoNotDamagePreviousFileOrLeaveTemporaryFiles()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "export.json");
            await File.WriteAllTextAsync(path, "keep me");
            var export = DesktopRangeExport.CreateDiagnostics(Response([Event(Utc)]), Utc);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => DesktopRangeExport.WriteAsync(path, export, canceled.Token));
            var badResponse = export.Diagnostics!.Response with
            { Events = [Event(Utc) with { Confidence = double.PositiveInfinity }] };
            var invalid = export with { Diagnostics = export.Diagnostics with { Response = badResponse } };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => DesktopRangeExport.WriteAsync(path, invalid));
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        });
    }

    [TestMethod]
    public async Task RenameFailureCleansTemporaryFileAndLeavesDestinationDirectoryIntact()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "directory.json");
            Directory.CreateDirectory(path);
            var export = DesktopRangeExport.CreateDiagnostics(Response([]), Utc);
            try { await DesktopRangeExport.WriteAsync(path, export); Assert.Fail("Expected destination directory to reject publication."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Assert.IsTrue(Directory.Exists(path));
            Assert.AreEqual(0, Directory.GetFiles(directory).Length);
        });
    }

    private static async Task InDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PerfMonitor-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static DesktopConnectionState State(long observation, double elapsed, double? value,
        string freshness = FreshnessStates.Fresh, DateTimeOffset? observedUtc = null, string instanceId = "session")
    {
        var groups = DesktopTrendBuffer.Metrics.GroupBy(metric => metric.Group).ToDictionary(group => group.Key, group =>
        {
            var metrics = new JsonObject();
            foreach (var metric in group) metrics[metric.Metric] = new JsonObject { ["value"] = value };
            return new SnapshotGroup("test", observedUtc ?? Utc.AddSeconds(elapsed), AvailabilityStates.Available,
                freshness, ProviderCoverage.Complete, [], new JsonObject { ["metrics"] = metrics })
                { ObservedElapsedSeconds = elapsed, ObservationSequence = observation };
        });
        var snapshot = new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent, instanceId, observation,
            null, null, Utc.AddSeconds(elapsed), 0, new("available", "fresh"), new(3600, 3600), groups)
            { ElapsedSeconds = elapsed };
        return new(DesktopConnectionStatus.Connected, instanceId, 0, snapshot, null);
    }

    private static DesktopConnectionState ProcessState(long observation, double elapsed,
        DesktopProcessIdentity identity, double cpu)
    {
        var row = new JsonObject
        {
            ["identity"] = new JsonObject { ["pid"] = identity.Pid, ["creationTimeTicks"] = identity.CreationTimeTicks },
            ["name"] = "demo", ["cpuReady"] = true,
            ["metrics"] = new JsonObject { [MetricIds.ProcessCpuNormalized] = new JsonObject { ["value"] = cpu } },
        };
        var state = State(observation, elapsed, cpu);
        var group = new SnapshotGroup("test", Utc.AddSeconds(elapsed), AvailabilityStates.Available,
            FreshnessStates.Fresh, ProviderCoverage.Complete, [], new JsonArray(row))
            { ObservationSequence = observation, ObservedElapsedSeconds = elapsed };
        return state with { LatestSnapshot = state.LatestSnapshot! with
            { Groups = new Dictionary<string, SnapshotGroup> { [GroupIds.Processes] = group } } };
    }

    private static DiagnosticsContract Response(IReadOnlyList<DiagnosticEventContract> events) => new()
    {
        ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, InstanceId = "session",
        Query = new() { FromEpochMs = Utc.ToUnixTimeMilliseconds(), ToEpochMs = Utc.AddMinutes(5).ToUnixTimeMilliseconds(), MaxEvents = 200 },
        EventCount = events.Count, Truncated = false, Events = events,
    };

    private static DiagnosticEventContract Event(DateTimeOffset time) => new()
    {
        ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, EventId = "event-" + time.Ticks,
        InstanceId = "session", RuleId = DiagnosticRuleIds.HighCpu, RuleVersion = "1", Severity = DiagnosticSeverities.Warning,
        State = DiagnosticStates.Active, SubjectId = "system", FirstSeenUtc = time.AddMinutes(-1), LastSeenUtc = time,
        ObservationSequence = 42, Confidence = .75, CooldownSeconds = 30,
        Hysteresis = new() { ActivateWhen = "CPU >= 90%", RecoverWhen = "CPU <= 70%" },
        Debounce = new() { ActivateSeconds = 15, RecoverSeconds = 5 },
        EvidenceWindow = new() { FromUtc = time.AddSeconds(-15), ToUtc = time, SampleCount = 10 },
        Evidence = [new() { ObservedAtUtc = time, SubjectId = "system", Signal = MetricIds.SystemCpuUtilization,
            Value = null, Unit = Units.Percent, Condition = "缺测" }],
    };
}
