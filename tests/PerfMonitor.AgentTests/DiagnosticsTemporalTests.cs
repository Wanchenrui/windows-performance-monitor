using System.Text.Json;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.AgentTests;

public sealed partial class DiagnosticsTests
{
    private static readonly DateTimeOffset TemporalOrigin =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void UtcJumpsDoNotChangeDebounceCooldownOrEventIdentity()
    {
        var policy = OnlyHighCpu(2, 2, 10);
        var trace = new[] { 95.0, 95, 95, 70, 70, 70, 95, 95, 95, 70, 70, 70, 95, 95, 95 };
        var ordinary = trace.Select((cpu, second) => TemporalCpu(
            second + 1, second, cpu, TemporalOrigin.AddSeconds(second))).ToArray();
        var jumps = trace.Select((cpu, second) => TemporalCpu(
            second + 1, second, cpu, TemporalOrigin.AddHours(second % 2 == 0 ? 24 : -24)))
            .ToArray();

        var expected = DiagnosticReplay.Replay(ordinary, policy);
        var actual = DiagnosticReplay.Replay(jumps, policy);
        CollectionAssert.AreEqual(expected.Select(static item => item.State).ToArray(),
            actual.Select(static item => item.State).ToArray());
        CollectionAssert.AreEqual(expected.Select(static item => item.EventId).ToArray(),
            actual.Select(static item => item.EventId).ToArray());
        CollectionAssert.AreEqual(expected.Select(static item => item.Evidence.Count).ToArray(),
            actual.Select(static item => item.Evidence.Count).ToArray());
        Assert.AreEqual(3, actual.Count);
    }

    [TestMethod]
    public void MissingMetricBreaksActivationAndRecoveryEvidence()
    {
        var activation = new DiagnosticEvaluator(OnlyHighCpu(2, 2, 0));
        Assert.AreEqual(0, activation.Evaluate(TemporalCpu(1, 0, 95)).Count);
        Assert.AreEqual(0, activation.Evaluate(UnknownCpu(2, 1)).Count);
        Assert.AreEqual(0, activation.Evaluate(TemporalCpu(3, 2, 95)).Count);
        Assert.AreEqual(0, activation.Evaluate(TemporalCpu(4, 3, 95)).Count);
        var active = activation.Evaluate(TemporalCpu(5, 4, 95)).Single();
        Assert.AreEqual(TemporalOrigin.AddSeconds(2), active.FirstSeenUtc);
        Assert.AreEqual(3, active.Evidence.Count);

        var recovery = new DiagnosticEvaluator(OnlyHighCpu(0, 2, 0));
        Assert.AreEqual(DiagnosticStates.Active,
            recovery.Evaluate(TemporalCpu(1, 0, 95)).Single().State);
        Assert.AreEqual(0, recovery.Evaluate(TemporalCpu(2, 1, 70)).Count);
        Assert.AreEqual(0, recovery.Evaluate(UnknownCpu(3, 2)).Count);
        Assert.AreEqual(0, recovery.Evaluate(TemporalCpu(4, 3, 70)).Count);
        Assert.AreEqual(0, recovery.Evaluate(TemporalCpu(5, 4, 70)).Count);
        var resolved = recovery.Evaluate(TemporalCpu(6, 5, 70)).Single();
        Assert.AreEqual(DiagnosticStates.Resolved, resolved.State);
        Assert.AreEqual(3, resolved.Evidence.Count);
        Assert.IsTrue(resolved.Evidence.All(static item => item.Value == 70));
    }

    [TestMethod]
    public void StaleAndWarmupMetricsCannotActivateOrResolve()
    {
        var evaluator = new DiagnosticEvaluator(OnlyHighCpu(0, 0, 0));
        Assert.AreEqual(DiagnosticStates.Active,
            evaluator.Evaluate(TemporalCpu(1, 0, 95)).Single().State);
        foreach (var quality in new[] { FreshnessStates.Stale, FreshnessStates.WarmingUp })
        {
            var snapshot = TemporalCpu(quality == FreshnessStates.Stale ? 2 : 3,
                quality == FreshnessStates.Stale ? 1 : 2, 70);
            snapshot = ReplaceGroup(snapshot, GroupIds.SystemCpu,
                snapshot.Groups[GroupIds.SystemCpu] with { Freshness = quality });
            Assert.AreEqual(0, evaluator.Evaluate(snapshot).Count);
            var inactive = new DiagnosticEvaluator(OnlyHighCpu(0, 0, 0));
            var high = ReplaceGroup(snapshot, GroupIds.SystemCpu,
                snapshot.Groups[GroupIds.SystemCpu] with
                {
                    Data = ScalarGroup(TemporalOrigin, MetricIds.SystemCpuUtilization,
                        95, Units.Percent).Data,
                });
            Assert.AreEqual(0, inactive.Evaluate(high).Count);
        }

        Assert.AreEqual(DiagnosticStates.Resolved,
            evaluator.Evaluate(TemporalCpu(4, 3, 70)).Single().State);
    }

    [TestMethod]
    public void SequenceLossAndLongPauseRestartPendingDebounce()
    {
        var evaluator = new DiagnosticEvaluator(OnlyHighCpu(2, 0, 0));
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(1, 0, 95)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(2, 1, 95)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(4, 2, 95)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(5, 3, 95)).Count);
        Assert.AreEqual(TemporalOrigin.AddSeconds(2),
            evaluator.Evaluate(TemporalCpu(6, 4, 95)).Single().FirstSeenUtc);

        evaluator = new DiagnosticEvaluator(OnlyHighCpu(2, 0, 0));
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(1, 0, 95)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(2, 1, 95)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(3, 300, 95)).Count);
        var resumed = evaluator.Evaluate(TemporalCpu(4, 302, 95)).Single();
        Assert.AreEqual(TemporalOrigin.AddSeconds(300), resumed.FirstSeenUtc);
        Assert.AreEqual(2, resumed.Evidence.Count);
    }

    [TestMethod]
    public async Task ActualFanoutCadenceAllowsDefaultDebounceAcrossProviderUpdates()
    {
        var time = new ManualTimeProvider(TemporalOrigin);
        var cpu = new ProviderDescriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), "user", "low");
        var memory = cpu with { GroupId = GroupIds.Memory, ProviderId = ProviderIds.Memory };
        var uptime = cpu with { GroupId = GroupIds.Uptime, ProviderId = ProviderIds.Uptime };
        var assembler = new SnapshotAssembler([cpu, memory, uptime], time, "cadence-test");
        var policy = OnlyHighCpu(30, 30, 300);
        await using var engine = new DiagnosticEngine(policy);
        var capture = new SnapshotCapture();
        var fanout = new SnapshotFanout([engine, capture]);
        engine.Start();
        for (var second = 0; second <= 32; second++)
        {
            if (second > 0)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }

            foreach (var descriptor in new[] { cpu, memory, uptime })
            {
                var result = descriptor == cpu
                    ? new ProviderResult(cpu.GroupId, cpu.ProviderId, time.GetUtcNow(),
                        AvailabilityStates.Available, ProviderCoverage.Complete, [],
                        ScalarGroup(time.GetUtcNow(), MetricIds.SystemCpuUtilization,
                            95, Units.Percent).Data)
                    : DelegateProvider.Available(descriptor, time.GetUtcNow());
                await assembler.PublishAsync(result, new ProviderExecution(descriptor,
                    time.GetUtcNow(), time.GetUtcNow(), time.GetUtcNow(), 0, 0, 0, 0)
                {
                    StartedTimestamp = time.GetTimestamp(),
                    CompletedTimestamp = time.GetTimestamp(),
                }, CancellationToken.None);
            }

            _ = fanout.Publish(assembler.Read());
        }

        await engine.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        var query = new DiagnosticQueryContract
        {
            FromEpochMs = TemporalOrigin.AddSeconds(-1).ToUnixTimeMilliseconds(),
            ToEpochMs = TemporalOrigin.AddMinutes(1).ToUnixTimeMilliseconds(),
            MaxEvents = 10,
        };
        var live = await engine.QueryDiagnosticsAsync(query, "response", CancellationToken.None);
        var active = live.Events.Single();
        Assert.AreEqual(DiagnosticStates.Active, active.State);
        Assert.AreEqual(TemporalOrigin, active.FirstSeenUtc);
        Assert.AreEqual(TemporalOrigin.AddSeconds(30), active.LastSeenUtc);
        Assert.AreEqual(3L, capture.Snapshots[0].Sequence);
        Assert.AreEqual(6L, capture.Snapshots[1].Sequence);
        Assert.AreEqual(1L, capture.Snapshots[0].DeliverySequence);
        Assert.AreEqual(2L, capture.Snapshots[1].DeliverySequence);
        Assert.AreEqual(0L, engine.Health.DroppedSnapshots);
        var replay = await DiagnosticReplay.ReplayAsync(AsAsync(capture.Snapshots), policy);
        Assert.AreEqual(JsonSerializer.Serialize(live.Events, ContractJson.Options),
            JsonSerializer.Serialize(replay, ContractJson.Options));
    }

    [TestMethod]
    public async Task ActualDiagnosticQueueDropBreaksContinuousHighEvidence()
    {
        var policy = OnlyHighCpu(2, 0, 0) with
        {
            ProviderUnavailable = DiagnosticPolicy.Default.ProviderUnavailable with
            {
                ActivateDebounceSeconds = 0,
                RecoverDebounceSeconds = 0,
            },
        };
        using var sink = new BlockingDiagnosticSink();
        await using var engine = new DiagnosticEngine(policy, [sink], new DiagnosticEngineOptions
        {
            QueueCapacity = 1,
        });
        engine.Start();
        var first = TemporalCpu(1, 0, 95);
        first = ReplaceGroup(first, GroupIds.Memory,
            first.Groups[GroupIds.SystemCpu] with
            {
                Availability = AvailabilityStates.Error,
                Data = null,
            });
        try
        {
            Assert.IsTrue(engine.TryPublish(first));
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(engine.TryPublish(TemporalCpu(2, 1, 95)));
            Assert.IsFalse(engine.TryPublish(TemporalCpu(3, 2, 95)));
        }
        finally
        {
            sink.Release.Set();
        }

        await engine.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        for (var second = 3; second <= 5; second++)
        {
            Assert.IsTrue(engine.TryPublish(TemporalCpu(second + 1, second, 95)));
            await engine.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        }

        var result = await engine.QueryDiagnosticsAsync(new DiagnosticQueryContract
        {
            RuleIds = [DiagnosticRuleIds.HighCpu],
            FromEpochMs = TemporalOrigin.AddSeconds(-1).ToUnixTimeMilliseconds(),
            ToEpochMs = TemporalOrigin.AddSeconds(10).ToUnixTimeMilliseconds(),
            MaxEvents = 10,
        }, "response", CancellationToken.None);
        var active = result.Events.Single();
        Assert.AreEqual(1L, engine.Health.DroppedSnapshots);
        Assert.AreEqual(TemporalOrigin.AddSeconds(3), active.FirstSeenUtc);
        Assert.AreEqual(TemporalOrigin.AddSeconds(5), active.LastSeenUtc);
        Assert.AreEqual(3, active.Evidence.Count);
    }

    [TestMethod]
    public void WarmSamplerIsUnknownAndCannotSupplyASamplingGap()
    {
        var policy = OnlyHighCpu(0, 0, 0) with
        {
            HighCpu = DiagnosticPolicy.Default.HighCpu with { Enabled = false },
            SamplingGap = DiagnosticPolicy.Default.SamplingGap with
            {
                ActivateDebounceSeconds = 0,
            },
        };
        var evaluator = new DiagnosticEvaluator(policy);
        var warm = new SnapshotGroup(ProviderIds.Sampler, null,
            AvailabilityStates.Unavailable, FreshnessStates.WarmingUp,
            ProviderCoverage.Limited, [], null);
        Assert.AreEqual(0, evaluator.Evaluate(ReplaceGroup(TemporalCpu(1, 0, 95),
            GroupIds.Sampler, warm)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(ReplaceGroup(TemporalCpu(2, 10, 95),
            GroupIds.Sampler, warm)).Count);
    }

    [TestMethod]
    public void HeldValuesDoNotBecomeIndependentEvidence()
    {
        var evaluator = new DiagnosticEvaluator(OnlyHighCpu(2, 0, 0));
        var first = TemporalCpu(1, 0, 95);
        Assert.AreEqual(0, evaluator.Evaluate(first).Count);
        for (var sequence = 2; sequence <= 4; sequence++)
        {
            var held = first with
            {
                Sequence = sequence,
                ElapsedSeconds = sequence - 1,
                DeliverySequence = sequence,
            };
            Assert.AreEqual(0, evaluator.Evaluate(held).Count);
        }

        var active = evaluator.Evaluate(TemporalCpu(5, 4, 95)).Single();
        Assert.AreEqual(2, active.Evidence.Count);
    }

    [TestMethod]
    public void ProcessWarmupMissingAndLimitedCoverageNeverInventRecovery()
    {
        var evaluator = new DiagnosticEvaluator(OnlyWatchedProcess());
        var first = evaluator.Evaluate(TemporalProcess(1, 0, 95)).Single();
        Assert.AreEqual("process:hotproc", first.SubjectId);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalProcess(2, 1, null)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalProcess(3, 2, 20,
            coverage: ProviderCoverage.Limited)).Count);
        var resolved = evaluator.Evaluate(TemporalProcess(4, 3, 20)).Single();
        Assert.AreEqual(DiagnosticStates.Resolved, resolved.State);
        Assert.IsFalse(resolved.Evidence.Any(static item => item.Value == 0));

        Assert.AreEqual(0, new DiagnosticEvaluator(OnlyWatchedProcess())
            .Evaluate(TemporalProcess(1, 0, null)).Count);
    }

    [TestMethod]
    public void ProcessExitAndPidReuseDoNotResolvePreviousInstance()
    {
        var evaluator = new DiagnosticEvaluator(OnlyWatchedProcess());
        Assert.AreEqual(DiagnosticStates.Active,
            evaluator.Evaluate(TemporalProcess(1, 0, 95)).Single().State);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalProcess(2, 1, 20,
            present: false)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalProcess(3, 2, 20,
            created: 5678)).Count);
        Assert.AreEqual(DiagnosticStates.Active,
            evaluator.Evaluate(TemporalProcess(4, 3, 95, created: 5678)).Single().State);

        evaluator = new DiagnosticEvaluator(OnlyWatchedProcess());
        _ = evaluator.Evaluate(TemporalProcess(1, 0, 95));
        Assert.AreEqual(0, evaluator.Evaluate(TemporalProcess(2, 1, 20,
            created: 5678)).Count);
    }

    [TestMethod]
    public void AgentEpochSwitchDiscardsPreviousStateAndCooldown()
    {
        var evaluator = new DiagnosticEvaluator(OnlyHighCpu(0, 0, 300));
        _ = evaluator.Evaluate(TemporalCpu(1, 0, 95));
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(1, 0, 70) with
        {
            InstanceId = "second-instance",
        }).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(2, 1, 70)).Count);
        Assert.AreEqual(DiagnosticStates.Active,
            evaluator.Evaluate(TemporalCpu(3, 2, 95)).Single().State);
    }

    [TestMethod]
    public void InvalidMonotonicEpochCannotResolveAnEarlierEpisode()
    {
        var evaluator = new DiagnosticEvaluator(OnlyHighCpu(0, 0, 0));
        _ = evaluator.Evaluate(TemporalCpu(1, 100, 95));
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(2, 101, 70) with
        {
            ElapsedSeconds = null,
        }).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(3, 0, 70)).Count);
        Assert.AreEqual(0, evaluator.Evaluate(TemporalCpu(4, 1, 70)).Count);
    }

    [TestMethod]
    public void NameMaximumAndFingerprintAreStableWhenRowsChangeOrder()
    {
        var snapshot = TemporalProcess(1, 0, 20);
        var rows = (JsonArray)snapshot.Groups[GroupIds.Processes].Data!.DeepClone();
        var additional = (JsonObject)rows[0]!.DeepClone();
        additional["identity"]!["pid"] = 43;
        additional["metrics"]![MetricIds.ProcessCpuNormalized]!["value"] = 95.0;
        rows.Add(additional);
        var group = snapshot.Groups[GroupIds.Processes] with { Data = rows };
        snapshot = ReplaceGroup(snapshot, GroupIds.Processes, group);
        var projected = DiagnosticProcessProjection.ProjectGroup(group, ["hotproc"]);
        var reversed = new JsonArray(rows.Reverse().Select(static row => row!.DeepClone()).ToArray());
        var reordered = DiagnosticProcessProjection.ProjectGroup(group with { Data = reversed }, ["hotproc"]);
        Assert.AreEqual(projected.Data!.ToJsonString(), reordered.Data!.ToJsonString());
        var raw = DiagnosticReplay.Replay([snapshot], OnlyWatchedProcess()).Single();
        var minimal = DiagnosticReplay.Replay([ReplaceGroup(snapshot, GroupIds.Processes, projected)],
            OnlyWatchedProcess()).Single();
        Assert.AreEqual(95.0, raw.Evidence.Single().Value);
        Assert.AreEqual(JsonSerializer.Serialize(raw, ContractJson.Options),
            JsonSerializer.Serialize(minimal, ContractJson.Options));
    }

    [TestMethod]
    public void MinimalProcessProjectionPreservesEventsAndInstanceContinuity()
    {
        var policy = OnlyWatchedProcess() with
        {
            ProcessCpuSpike = DiagnosticPolicy.Default.ProcessCpuSpike with
            {
                ActivateDebounceSeconds = 2,
                RecoverDebounceSeconds = 2,
                CooldownSeconds = 0,
            },
        };
        var snapshots = Enumerable.Range(0, 9).Select(second =>
            TemporalProcess(second + 1, second,
                second <= 2 || second >= 6 ? 95 : 20,
                created: second >= 6 ? 5678 : 1234)).ToArray();
        var projected = snapshots.Select(snapshot => ReplaceGroup(snapshot,
            GroupIds.Processes, DiagnosticProcessProjection.ProjectGroup(
                snapshot.Groups[GroupIds.Processes], policy.WatchedProcessNames))).ToArray();
        var rawEvents = DiagnosticReplay.Replay(snapshots, policy);
        var projectedEvents = DiagnosticReplay.Replay(projected, policy);

        Assert.AreEqual(3, rawEvents.Count);
        Assert.AreEqual(JsonSerializer.Serialize(rawEvents, ContractJson.Options),
            JsonSerializer.Serialize(projectedEvents, ContractJson.Options));
        var json = projected[0].Groups[GroupIds.Processes].Data!.ToJsonString();
        Assert.IsFalse(json.Contains("creationTimeTicks", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"pid\"", StringComparison.Ordinal));
        Assert.AreEqual(1, ((JsonArray)projected[0].Groups[GroupIds.Processes].Data!).Count);
    }

    [TestMethod]
    public void OldRawReplayFailsExplicitlyWithoutChangingItsPayload()
    {
        var modern = TemporalCpu(1, 0, 95);
        var legacy = modern with
        {
            ElapsedSeconds = null,
            Groups = modern.Groups.ToDictionary(static pair => pair.Key,
                static pair => pair.Value with
                {
                    ObservationSequence = null,
                    ObservedElapsedSeconds = null,
                }),
        };
        var before = JsonSerializer.Serialize(legacy, ContractJson.Options);
        var restored = JsonSerializer.Deserialize<AgentSnapshot>(before, ContractJson.Options)!;
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            DiagnosticReplay.Replay([restored], OnlyHighCpu(0, 0, 0)));
        Assert.AreEqual("diagnostic_replay_time_unavailable", exception.Message);
        Assert.AreEqual(before, JsonSerializer.Serialize(restored, ContractJson.Options));
        Assert.AreEqual(0, new DiagnosticEvaluator(OnlyHighCpu(0, 0, 0))
            .Evaluate(restored).Count);
    }

    [TestMethod]
    public async Task StreamingReplayUsesSequenceAndRejectsReopenedEpochs()
    {
        var snapshots = Enumerable.Range(0, 3).Select(second => TemporalCpu(
            second + 1, second, 95, TemporalOrigin.AddHours(-second))).ToArray();
        var events = await DiagnosticReplay.ReplayAsync(AsAsync(snapshots), OnlyHighCpu(2, 0, 0));
        Assert.AreEqual(DiagnosticStates.Active, events.Single().State);
        var order = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await DiagnosticReplay.ReplayAsync(AsAsync(snapshots.Reverse()), OnlyHighCpu(2, 0, 0)));
        Assert.AreEqual("diagnostic_replay_order_invalid", order.Message);
        var reopened = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await DiagnosticReplay.ReplayAsync(AsAsync([
                snapshots[0], snapshots[0] with { InstanceId = "second-instance" }, snapshots[1],
            ]), OnlyHighCpu(0, 0, 0)));
        Assert.AreEqual("diagnostic_replay_order_invalid", reopened.Message);
        var synchronous = Assert.ThrowsExactly<InvalidDataException>(() =>
            DiagnosticReplay.Replay([
                snapshots[0], snapshots[0] with { InstanceId = "second-instance" }, snapshots[1],
            ], OnlyHighCpu(0, 0, 0)));
        Assert.AreEqual("diagnostic_replay_order_invalid", synchronous.Message);
    }

    [TestMethod]
    public void ProjectionPolicyMismatchIsAnExplicitReplayError()
    {
        var policy = OnlyHighCpu(0, 0, 0);
        var snapshot = TemporalCpu(1, 0, 95);
        var sampler = new SnapshotGroup(ProviderIds.Sampler, snapshot.CompletedAtUtc,
            AvailabilityStates.Available, FreshnessStates.Fresh, ProviderCoverage.Complete,
            [], new JsonObject
            {
                ["storageReplay"] = new JsonObject
                {
                    ["projectionVersion"] = DiagnosticReplay.ProjectionVersion,
                    ["policyFingerprint"] = DiagnosticReplay.GetPolicyFingerprint(policy),
                },
            })
        {
            ObservationSequence = snapshot.Sequence,
            ObservedElapsedSeconds = snapshot.ElapsedSeconds,
        };
        snapshot = ReplaceGroup(snapshot, GroupIds.Sampler, sampler);
        Assert.AreEqual(1, DiagnosticReplay.Replay([snapshot], policy).Count);
        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            DiagnosticReplay.Replay([snapshot], OnlyHighCpu(2, 0, 0)));
        Assert.AreEqual("diagnostic_replay_projection_incompatible", exception.Message);
    }

    [TestMethod]
    public async Task EventObservationOrderSurvivesUtcRollbackInRealtimeQuery()
    {
        var policy = OnlyHighCpu(0, 0, 0);
        await using var engine = new DiagnosticEngine(policy);
        engine.Start();
        Assert.IsTrue(engine.TryPublish(TemporalCpu(1, 0, 95, TemporalOrigin.AddDays(1))));
        Assert.IsTrue(engine.TryPublish(TemporalCpu(2, 1, 70, TemporalOrigin.AddDays(-1))));
        await engine.WaitForIdleAsync(TimeSpan.FromSeconds(5));
        var query = new DiagnosticQueryContract
        {
            FromEpochMs = TemporalOrigin.AddDays(-2).ToUnixTimeMilliseconds(),
            ToEpochMs = TemporalOrigin.AddDays(2).ToUnixTimeMilliseconds(),
            MaxEvents = 10,
        };
        var result = await engine.QueryDiagnosticsAsync(query, "response", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { DiagnosticStates.Active, DiagnosticStates.Resolved },
            result.Events.Select(static item => item.State).ToArray());
        CollectionAssert.AreEqual(new long?[] { 1, 2 },
            result.Events.Select(static item => item.ObservationSequence).ToArray());
        Assert.IsTrue(result.Events[0].LastSeenUtc > result.Events[1].LastSeenUtc);

        var latest = await engine.QueryDiagnosticsAsync(query with { MaxEvents = 1 },
            "response", CancellationToken.None);
        Assert.AreEqual(DiagnosticStates.Resolved, latest.Events.Single().State);
    }

    private static DiagnosticPolicy OnlyWatchedProcess()
    {
        var policy = OnlyHighCpu(0, 0, 0);
        return policy with
        {
            HighCpu = policy.HighCpu with { Enabled = false },
            ProcessCpuSpike = DiagnosticPolicy.Default.ProcessCpuSpike with
            {
                ActivateDebounceSeconds = 0,
                RecoverDebounceSeconds = 0,
                CooldownSeconds = 0,
            },
            WatchedProcessNames = ["hotproc"],
        };
    }

    private static AgentSnapshot TemporalCpu(long sequence, double elapsed, double cpu,
        DateTimeOffset? utc = null) => WithLogicalTime(
            CpuSnapshot(sequence, utc ?? TemporalOrigin.AddSeconds(elapsed), cpu), elapsed);

    private static AgentSnapshot UnknownCpu(long sequence, double elapsed)
    {
        var snapshot = TemporalCpu(sequence, elapsed, 95);
        var group = snapshot.Groups[GroupIds.SystemCpu];
        var data = group.Data!.DeepClone();
        data["metrics"]![MetricIds.SystemCpuUtilization]!["value"] = null;
        return ReplaceGroup(snapshot, GroupIds.SystemCpu, group with { Data = data });
    }

    private static AgentSnapshot TemporalProcess(long sequence, double elapsed, double? cpu,
        long created = 1234, bool present = true, ProviderCoverage? coverage = null)
    {
        var rows = new JsonArray();
        if (present)
        {
            rows.Add(new JsonObject
            {
                ["name"] = "hotproc",
                ["identity"] = new JsonObject { ["pid"] = 42, ["creationTimeTicks"] = created },
                ["cpuReady"] = cpu is not null,
                ["metrics"] = new JsonObject
                {
                    [MetricIds.ProcessCpuNormalized] = MetricJson.Value(cpu, Units.Percent, "test"),
                },
            });
        }

        var utc = TemporalOrigin.AddSeconds(elapsed);
        return WithLogicalTime(Snapshot(sequence, utc,
            new Dictionary<string, SnapshotGroup>
            {
                [GroupIds.Processes] = Group(utc, rows) with
                {
                    Coverage = coverage ?? ProviderCoverage.Complete,
                },
            }), elapsed);
    }

    private static AgentSnapshot WithLogicalTime(AgentSnapshot snapshot, double elapsed) =>
        snapshot with
        {
            ElapsedSeconds = elapsed,
            DeliverySequence = snapshot.Sequence,
            Groups = snapshot.Groups.ToDictionary(static pair => pair.Key, pair => pair.Value with
            {
                ObservationSequence = snapshot.Sequence,
                ObservedElapsedSeconds = elapsed,
            }),
        };

    private static AgentSnapshot ReplaceGroup(AgentSnapshot snapshot, string groupId, SnapshotGroup group)
    {
        var groups = snapshot.Groups.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        groups[groupId] = group;
        return snapshot with { Groups = groups };
    }

    private static async IAsyncEnumerable<AgentSnapshot> AsAsync(IEnumerable<AgentSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            await Task.Yield();
            yield return snapshot;
        }
    }

    private sealed class SnapshotCapture : ISnapshotConsumer
    {
        public List<AgentSnapshot> Snapshots { get; } = [];
        public bool TryPublish(AgentSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
            return true;
        }
    }

    private sealed class BlockingDiagnosticSink : IDiagnosticEventSink, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool TryPublishDiagnostic(DiagnosticEventContract diagnosticEvent)
        {
            Entered.TrySetResult();
            return Release.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Release.Dispose();
    }
}
