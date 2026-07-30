using System.Text.Json;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public void ReplayIsDeterministicAcrossHysteresisTransitions()
    {
        var policy = OnlyHighCpu(
            activateDebounceSeconds: 2,
            recoverDebounceSeconds: 2,
            cooldownSeconds: 10);
        var origin = DateTimeOffset.Parse(
            "2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var snapshots = new List<AgentSnapshot>();
        for (var second = 0; second <= 2; second++)
        {
            snapshots.Add(CpuSnapshot(
                second + 1,
                origin.AddSeconds(second),
                95));
        }

        for (var second = 3; second <= 5; second++)
        {
            snapshots.Add(CpuSnapshot(
                second + 1,
                origin.AddSeconds(second),
                70));
        }

        var first = DiagnosticReplay.Replay(snapshots, policy);
        var secondReplay = DiagnosticReplay.Replay(
            snapshots.AsEnumerable().Reverse(),
            policy);

        Assert.AreEqual(2, first.Count);
        Assert.AreEqual(DiagnosticStates.Active, first[0].State);
        Assert.AreEqual(DiagnosticStates.Resolved, first[1].State);
        Assert.AreEqual(origin, first[0].FirstSeenUtc);
        Assert.AreEqual(origin.AddSeconds(2), first[0].LastSeenUtc);
        Assert.AreEqual(origin, first[1].FirstSeenUtc);
        Assert.AreEqual(origin.AddSeconds(5), first[1].LastSeenUtc);
        Assert.AreEqual(1.0, first[0].Confidence);
        Assert.IsTrue(first[0].EvidenceWindow.SampleCount >= 3);
        Assert.AreEqual(
            JsonSerializer.Serialize(first, ContractJson.Options),
            JsonSerializer.Serialize(
                secondReplay,
                ContractJson.Options));
    }

    [TestMethod]
    public void EveryRoadmapRuleProducesDeterministicActivation()
    {
        var policy = ImmediatePolicy();
        var origin = DateTimeOffset.Parse(
            "2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var evaluator = new DiagnosticEvaluator(policy);

        var first = evaluator.Evaluate(
            FullSnapshot(1, origin, breached: true));
        var second = evaluator.Evaluate(
            FullSnapshot(2, origin.AddSeconds(6), breached: true));
        var ruleIds = first
            .Concat(second)
            .Select(item => item.RuleId)
            .ToHashSet(StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(
            DiagnosticRuleIds.All.ToArray(),
            ruleIds.ToArray());
        Assert.IsTrue(
            first.Concat(second).All(
                item =>
                    item.RuleVersion == "1.0.0" &&
                    item.State == DiagnosticStates.Active &&
                    item.Confidence == 1 &&
                    item.Evidence.Count > 0 &&
                    item.Debounce.ActivateSeconds == 0));
    }

    [TestMethod]
    public async Task EngineQueueAndQueryRemainBounded()
    {
        var policy = OnlyHighCpu(
            activateDebounceSeconds: 0,
            recoverDebounceSeconds: 0,
            cooldownSeconds: 0);
        await using var engine = new DiagnosticEngine(
            policy,
            options: new DiagnosticEngineOptions
            {
                QueueCapacity = 8,
                RecentEventLimit = 4,
            });
        engine.Start();
        var origin = DateTimeOffset.Parse(
            "2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsTrue(engine.TryPublish(
            CpuSnapshot(1, origin, 95)));
        Assert.IsTrue(engine.TryPublish(
            CpuSnapshot(2, origin.AddSeconds(1), 70)));
        await engine.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        var result = await engine.QueryDiagnosticsAsync(
            new DiagnosticQueryContract
            {
                FromEpochMs = origin.AddMinutes(-1)
                    .ToUnixTimeMilliseconds(),
                ToEpochMs = origin.AddMinutes(1)
                    .ToUnixTimeMilliseconds(),
                MaxEvents = 2,
            },
            "response-instance",
            CancellationToken.None);

        Assert.AreEqual(2, result.EventCount);
        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(
            "response-instance",
            result.InstanceId);
        Assert.AreEqual(2, engine.Health.EvaluatedSnapshots);
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task DiagnosticEventsUseExistingSingleSqliteWriter()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"perf-monitor-diagnostic-store-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "history.db");
        try
        {
            await using var store = new SqliteHistoryStore(
                new SqliteHistoryOptions
                {
                    DatabasePath = databasePath,
                });
            await store.StartAsync(CancellationToken.None);
            var origin = DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
            var diagnosticEvent = DiagnosticReplay.Replay(
                [CpuSnapshot(1, origin, 95)],
                OnlyHighCpu(
                    activateDebounceSeconds: 0,
                    recoverDebounceSeconds: 0,
                    cooldownSeconds: 0)).Single();

            Assert.IsTrue(
                store.TryPublishDiagnostic(diagnosticEvent));
            Assert.IsTrue(
                store.TryPublishDiagnostic(diagnosticEvent));
            await store.WaitForDiagnosticsIdleAsync(
                TimeSpan.FromSeconds(10));
            var result = await store.QueryDiagnosticsAsync(
                new DiagnosticQueryContract
                {
                    RuleIds = [DiagnosticRuleIds.HighCpu],
                    States = [DiagnosticStates.Active],
                    FromEpochMs = origin.AddMinutes(-1)
                        .ToUnixTimeMilliseconds(),
                    ToEpochMs = origin.AddMinutes(1)
                        .ToUnixTimeMilliseconds(),
                    MaxEvents = 10,
                },
                "response-instance",
                CancellationToken.None);

            Assert.AreEqual(1, result.EventCount);
            Assert.AreEqual(
                diagnosticEvent.EventId,
                result.Events.Single().EventId);
            Assert.AreEqual(
                2L,
                store.Health.PersistedDiagnosticEvents);
            Assert.AreEqual(
                SqliteHistoryState.Healthy,
                store.Health.State);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task SqliteSnapshotHistoryReplaysIdenticalEvents()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"perf-monitor-diagnostic-replay-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "history.db");
        try
        {
            var policy = OnlyHighCpu(
                activateDebounceSeconds: 2,
                recoverDebounceSeconds: 2,
                cooldownSeconds: 10);
            await using var store = new SqliteHistoryStore(
                new SqliteHistoryOptions
                {
                    DatabasePath = databasePath,
                });
            await store.StartAsync(CancellationToken.None);
            await using var engine = new DiagnosticEngine(
                policy,
                [store]);
            engine.Start();
            var origin = DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture);
            var snapshots = Enumerable.Range(0, 6)
                .Select(second => CpuSnapshot(
                    second + 1,
                    origin.AddSeconds(second),
                    second <= 2 ? 95 : 70))
                .ToArray();
            foreach (var snapshot in snapshots)
            {
                Assert.IsTrue(store.TryPublish(snapshot));
                Assert.IsTrue(engine.TryPublish(snapshot));
            }

            await engine.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            await store.WaitForDiagnosticsIdleAsync(
                TimeSpan.FromSeconds(10));
            var query = new DiagnosticQueryContract
            {
                FromEpochMs = origin.AddMinutes(-1)
                    .ToUnixTimeMilliseconds(),
                ToEpochMs = origin.AddMinutes(1)
                    .ToUnixTimeMilliseconds(),
                MaxEvents = 10,
            };
            var live = await engine.QueryDiagnosticsAsync(
                query,
                "response-instance",
                CancellationToken.None);
            var replayed = await DiagnosticReplay.ReplayAsync(
                store.ReadSnapshotsForReplayAsync(
                    query.FromEpochMs!.Value,
                    query.ToEpochMs!.Value,
                    maxSnapshots: 100,
                    cancellationToken: CancellationToken.None),
                policy);

            Assert.AreEqual(
                JsonSerializer.Serialize(
                    live.Events,
                    ContractJson.Options),
                JsonSerializer.Serialize(
                    replayed,
                    ContractJson.Options));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DiagnosticPolicy OnlyHighCpu(
        double activateDebounceSeconds,
        double recoverDebounceSeconds,
        double cooldownSeconds)
    {
        var policy = DiagnosticPolicy.Default;
        return policy with
        {
            HighCpu = policy.HighCpu with
            {
                ActivateDebounceSeconds =
                    activateDebounceSeconds,
                RecoverDebounceSeconds =
                    recoverDebounceSeconds,
                CooldownSeconds = cooldownSeconds,
            },
            MemoryPressure = policy.MemoryPressure with
            {
                Enabled = false,
            },
            SystemDiskLow = policy.SystemDiskLow with
            {
                Enabled = false,
            },
            ProcessCpuSpike = policy.ProcessCpuSpike with
            {
                Enabled = false,
            },
            SamplingGap = policy.SamplingGap with
            {
                Enabled = false,
            },
            ProviderUnavailable = policy.ProviderUnavailable with
            {
                Enabled = false,
            },
            AgentResourceAnomaly =
                policy.AgentResourceAnomaly with
                {
                    Enabled = false,
                },
        };
    }

    private static DiagnosticPolicy ImmediatePolicy()
    {
        var policy = DiagnosticPolicy.Default;
        return (policy with
        {
            HighCpu = Immediate(policy.HighCpu),
            MemoryPressure = Immediate(policy.MemoryPressure),
            SystemDiskLow = Immediate(policy.SystemDiskLow) with
            {
                MaxObservationGapSeconds = 120,
            },
            ProcessCpuSpike = Immediate(policy.ProcessCpuSpike),
            SamplingGap = Immediate(policy.SamplingGap),
            ProviderUnavailable = policy.ProviderUnavailable with
            {
                ActivateDebounceSeconds = 0,
                RecoverDebounceSeconds = 0,
            },
            AgentResourceAnomaly =
                policy.AgentResourceAnomaly with
                {
                    ActivateDebounceSeconds = 0,
                    RecoverDebounceSeconds = 0,
                },
            WatchedProcessNames = ["hotproc"],
        }).Validate();
    }

    private static ThresholdRulePolicy Immediate(
        ThresholdRulePolicy policy) =>
        policy with
        {
            ActivateDebounceSeconds = 0,
            RecoverDebounceSeconds = 0,
            CooldownSeconds = 0,
        };

    private static AgentSnapshot CpuSnapshot(
        long sequence,
        DateTimeOffset time,
        double cpu) =>
        Snapshot(
            sequence,
            time,
            new Dictionary<string, SnapshotGroup>(
                StringComparer.Ordinal)
            {
                [GroupIds.SystemCpu] = Group(
                    time,
                    new JsonObject
                    {
                        ["metrics"] = new JsonObject
                        {
                            [MetricIds.SystemCpuUtilization] =
                                MetricJson.Value(
                                    cpu,
                                    Units.Percent,
                                    "test"),
                        },
                    }),
            });

    private static AgentSnapshot FullSnapshot(
        long sequence,
        DateTimeOffset time,
        bool breached)
    {
        var cpu = breached ? 95 : 70;
        var memory = breached ? 95 : 70;
        var disk = breached ? 95 : 70;
        var processCpu = breached ? 95 : 20;
        var agentCpu = breached ? 30 : 5;
        var agentPrivate = breached
            ? 300L * 1024 * 1024
            : 100L * 1024 * 1024;
        return Snapshot(
            sequence,
            time,
            new Dictionary<string, SnapshotGroup>(
                StringComparer.Ordinal)
            {
                [GroupIds.SystemCpu] = ScalarGroup(
                    time,
                    MetricIds.SystemCpuUtilization,
                    cpu,
                    Units.Percent),
                [GroupIds.Memory] = ScalarGroup(
                    time,
                    MetricIds.MemoryUtilization,
                    memory,
                    Units.Percent),
                [GroupIds.Volumes] = Group(
                    time,
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["isSystem"] = true,
                            ["metrics"] = new JsonObject
                            {
                                [MetricIds.VolumeUtilization] =
                                    MetricJson.Value(
                                        disk,
                                        Units.Percent,
                                        "test"),
                            },
                        },
                    }),
                [GroupIds.Processes] = Group(
                    time,
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "hotproc",
                            ["metrics"] = new JsonObject
                            {
                                [MetricIds.ProcessCpuNormalized] =
                                    MetricJson.Value(
                                        processCpu,
                                        Units.Percent,
                                        "test"),
                            },
                        },
                    }),
                [GroupIds.Uptime] = new SnapshotGroup(
                    "test",
                    time,
                    breached
                        ? AvailabilityStates.Error
                        : AvailabilityStates.Available,
                    FreshnessStates.Fresh,
                    ProviderCoverage.Limited,
                    [],
                    null),
                [GroupIds.Self] = Group(
                    time,
                    new JsonObject
                    {
                        ["metrics"] = new JsonObject
                        {
                            [MetricIds.AgentCpuCoreEquivalent] =
                                MetricJson.Value(
                                    agentCpu,
                                    Units.Percent,
                                    "test"),
                            [MetricIds.AgentPrivateBytes] =
                                MetricJson.Value(
                                    agentPrivate,
                                    Units.Byte,
                                    "test"),
                        },
                    }),
            });
    }

    private static SnapshotGroup ScalarGroup(
        DateTimeOffset time,
        string metricId,
        double value,
        string unit) =>
        Group(
            time,
            new JsonObject
            {
                ["metrics"] = new JsonObject
                {
                    [metricId] = MetricJson.Value(
                        value,
                        unit,
                        "test"),
                },
            });

    private static SnapshotGroup Group(
        DateTimeOffset time,
        JsonNode data) =>
        new(
            "test",
            time,
            AvailabilityStates.Available,
            FreshnessStates.Fresh,
            ProviderCoverage.Complete,
            [],
            data);

    private static AgentSnapshot Snapshot(
        long sequence,
        DateTimeOffset time,
        IReadOnlyDictionary<string, SnapshotGroup> groups) =>
        new(
            ContractVersions.V1,
            ProductVersions.Agent,
            "11111111111111111111111111111111",
            sequence,
            time,
            time,
            time,
            0,
            new SnapshotSummary(
                AvailabilityStates.Available,
                FreshnessStates.Fresh),
            new SnapshotRetention(3600, 86_400),
            groups);
}
