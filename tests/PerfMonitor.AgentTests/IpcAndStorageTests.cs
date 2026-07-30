using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;
using PerfMonitor.Ipc.NamedPipes;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class IpcAndStorageTests
{
    [TestMethod]
    public async Task FramingRejectsOversizeBeforeReadingPayload()
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked(
                (uint)IpcProtocol.AbsoluteMaxMessageSize + 1U));
        await using var stream = new MemoryStream(header);

        var exception = await Assert.ThrowsExactlyAsync<
            IpcProtocolException>(
            async () => _ = await LengthPrefixedJson.ReadAsync(
                stream,
                IpcProtocol.AbsoluteMaxMessageSize,
                CancellationToken.None));

        Assert.AreEqual(
            IpcErrorCodes.MessageTooLarge,
            exception.ErrorCode);
        Assert.AreEqual((long)sizeof(uint), stream.Position);
    }

    [TestMethod]
    public void PipeAclAllowsOnlyCurrentUserAndLocalSystem()
    {
        var endpoint = PipeEndpoint.ForCurrentUser();
        var security = endpoint.CreateSecurity();
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        var network = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);
        var allowed = rules
            .Where(rule =>
                rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();

        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.IsTrue(allowed.Contains(endpoint.UserSid));
        Assert.IsTrue(allowed.Contains(localSystem));
        Assert.AreEqual(
            endpoint.UserSid.Equals(localSystem) ? 1 : 2,
            allowed.Count);
        Assert.IsTrue(
            rules.Any(rule =>
                rule.AccessControlType == AccessControlType.Deny &&
                rule.IdentityReference.Equals(network)));
    }

    [TestMethod]
    [Timeout(15000)]
    public async Task ClientDisconnectDoesNotStopAgentAndRestartChangesInstance()
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        var sid = identity.User ?? throw new AssertFailedException(
            "Current test identity has no SID.");
        var endpoint = new PipeEndpoint(
            $"PerfMonitor.test.{Guid.NewGuid():N}",
            sid);
        var firstService = new FakeIpcService(
            "11111111111111111111111111111111",
            sequence: 1);
        await using (var subscriptions =
            new SnapshotSubscriptionHub())
        await using (var server = new NamedPipeAgentServer(
            endpoint,
            firstService,
            subscriptions))
        {
            server.Start();
            await using (var firstClient =
                await NamedPipeAgentClient.ConnectAsync(
                    endpoint,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None))
            {
                var snapshot = await firstClient.GetSnapshotAsync(
                    CancellationToken.None);
                Assert.AreEqual(firstService.InstanceId, snapshot.InstanceId);
                var health = await firstClient.GetHealthAsync(
                    CancellationToken.None);
                Assert.AreEqual(ServiceIds.PerfMonitor, health.Service);
                Assert.AreEqual(firstService.InstanceId, health.InstanceId);
                Assert.AreEqual(1L, health.Sequence);
                var now = DateTimeOffset.UtcNow;
                var diagnostics =
                    await firstClient.QueryDiagnosticsAsync(
                        new DiagnosticQueryContract
                        {
                            FromEpochMs = now.AddMinutes(-1)
                                .ToUnixTimeMilliseconds(),
                            ToEpochMs = now.AddMinutes(1)
                                .ToUnixTimeMilliseconds(),
                            MaxEvents = 10,
                        },
                        CancellationToken.None);
                Assert.AreEqual(0, diagnostics.EventCount);
            }

            await using var secondClient =
                await NamedPipeAgentClient.ConnectAsync(
                    endpoint,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);
            var secondSnapshot = await secondClient.GetSnapshotAsync(
                CancellationToken.None);
            Assert.AreEqual(1L, secondSnapshot.Sequence);
        }

        var secondService = new FakeIpcService(
            "22222222222222222222222222222222",
            sequence: 2);
        await using var secondSubscriptions =
            new SnapshotSubscriptionHub();
        await using var secondServer = new NamedPipeAgentServer(
            endpoint,
            secondService,
            secondSubscriptions);
        secondServer.Start();
        await using var restartedClient =
            await NamedPipeAgentClient.ConnectAsync(
                endpoint,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

        Assert.AreNotEqual(
            firstService.InstanceId,
            restartedClient.InstanceId);
        Assert.AreEqual(
            secondService.InstanceId,
            restartedClient.InstanceId);
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task DesktopRetainsSnapshotAndDetectsAgentRestart()
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        var sid = identity.User ?? throw new AssertFailedException(
            "Current test identity has no SID.");
        var endpoint = new PipeEndpoint(
            $"PerfMonitor.desktop-test.{Guid.NewGuid():N}",
            sid);
        var firstService = new FakeIpcService(
            "77777777777777777777777777777777",
            sequence: 7);
        var firstSubscriptions = new SnapshotSubscriptionHub();
        var firstServer = new NamedPipeAgentServer(
            endpoint,
            firstService,
            firstSubscriptions);
        var firstServerDisposed = false;
        var firstSubscriptionsDisposed = false;
        var secondSubscriptions = new SnapshotSubscriptionHub();
        NamedPipeAgentServer? secondServer = null;
        using var stopping = new CancellationTokenSource();
        var session = new DesktopAgentSession(
            endpoint,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(25));
        var sessionTask = session.RunAsync(stopping.Token);

        try
        {
            firstServer.Start();
            await WaitUntilAsync(
                () =>
                    session.Current.Status ==
                        DesktopConnectionStatus.Connected &&
                    session.Current.InstanceId ==
                        firstService.InstanceId &&
                    session.Current.LatestSnapshot?.Sequence == 7,
                TimeSpan.FromSeconds(5));

            await firstServer.DisposeAsync();
            firstServerDisposed = true;
            await firstSubscriptions.DisposeAsync();
            firstSubscriptionsDisposed = true;
            await WaitUntilAsync(
                () => session.Current.Status ==
                    DesktopConnectionStatus.Reconnecting,
                TimeSpan.FromSeconds(5));

            Assert.AreEqual(
                firstService.InstanceId,
                session.Current.InstanceId);
            Assert.AreEqual(
                7L,
                session.Current.LatestSnapshot?.Sequence);

            var secondService = new FakeIpcService(
                "88888888888888888888888888888888",
                sequence: 8);
            secondServer = new NamedPipeAgentServer(
                endpoint,
                secondService,
                secondSubscriptions);
            secondServer.Start();
            await WaitUntilAsync(
                () =>
                    session.Current.Status ==
                        DesktopConnectionStatus.Connected &&
                    session.Current.InstanceId ==
                        secondService.InstanceId &&
                    session.Current.RestartCount == 1 &&
                    session.Current.LatestSnapshot?.Sequence == 8,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            stopping.Cancel();
            await sessionTask;
            if (!firstServerDisposed)
            {
                await firstServer.DisposeAsync();
            }

            if (!firstSubscriptionsDisposed)
            {
                await firstSubscriptions.DisposeAsync();
            }

            if (secondServer is not null)
            {
                await secondServer.DisposeAsync();
            }

            await secondSubscriptions.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SubscriptionQueueKeepsOnlyLatestSnapshot()
    {
        await using var hub = new SnapshotSubscriptionHub();
        await using var subscription = hub.Subscribe();
        Assert.IsTrue(hub.TryPublish(
            CreateSnapshot(
                "33333333333333333333333333333333",
                1,
                DateTimeOffset.UtcNow,
                10)));
        Assert.IsTrue(hub.TryPublish(
            CreateSnapshot(
                "33333333333333333333333333333333",
                2,
                DateTimeOffset.UtcNow,
                20)));

        var snapshot = await subscription.Reader.ReadAsync();
        Assert.AreEqual(2L, snapshot.Sequence);
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task ThirtyHourHistoryIsServerBoundedAndPreservesPeaks()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"perf-monitor-storage-test-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "history.db");
        try
        {
            await using var store = new SqliteHistoryStore(
                new SqliteHistoryOptions
                {
                    DatabasePath = databasePath,
                });
            await store.StartAsync(CancellationToken.None);
            Assert.AreEqual(
                SqliteHistoryState.Healthy,
                store.Health.State);

            var start = DateTimeOffset.UtcNow.AddHours(-30);
            const int sourcePoints = 121;
            for (var index = 0; index < sourcePoints; index++)
            {
                var value = index == 61 ? 99.0 : index % 40;
                Assert.IsTrue(store.TryPublish(
                    CreateSnapshot(
                        "44444444444444444444444444444444",
                        index + 1,
                        start.AddMinutes(index * 15),
                        value)));
            }

            await store.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            var history = await store.QueryAsync(
                new HistoryQueryContract
                {
                    MetricIds = [MetricIds.SystemCpuUtilization],
                    FromEpochMs = start.ToUnixTimeMilliseconds(),
                    ToEpochMs = start.AddHours(30)
                        .ToUnixTimeMilliseconds(),
                    MaxPoints = 10,
                },
                "55555555555555555555555555555555",
                CancellationToken.None);

            Assert.IsTrue(history.Downsampled);
            Assert.IsTrue(history.PointCount is > 0 and <= 10);
            Assert.AreEqual(history.PointCount, history.Points.Count);
            Assert.IsTrue(
                history.Points.Any(point =>
                    point.Metrics[
                        MetricIds.SystemCpuUtilization].Max == 99.0));
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
    public async Task StorageInitializationFailureLeavesRealtimeAvailable()
    {
        var parent = Path.Combine(
            Path.GetTempPath(),
            $"perf-monitor-storage-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var blockingFile = Path.Combine(parent, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        try
        {
            await using var store = new SqliteHistoryStore(
                new SqliteHistoryOptions
                {
                    DatabasePath = Path.Combine(
                        blockingFile,
                        "history.db"),
                });
            await store.StartAsync(CancellationToken.None);
            var snapshot = CreateSnapshot(
                "66666666666666666666666666666666",
                1,
                DateTimeOffset.UtcNow,
                42);

            Assert.AreEqual(
                SqliteHistoryState.DegradedReadOnly,
                store.Health.State);
            Assert.IsFalse(store.TryPublish(snapshot));
            Assert.AreEqual(42.0, ReadCpu(snapshot));
            Assert.AreEqual(
                1L,
                store.Health.DroppedPersistenceSamples);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static AgentSnapshot CreateSnapshot(
        string instanceId,
        long sequence,
        DateTimeOffset completedAtUtc,
        double cpuPercent)
    {
        var data = new JsonObject
        {
            ["metrics"] = new JsonObject
            {
                [MetricIds.SystemCpuUtilization] = MetricJson.Value(
                    cpuPercent,
                    Units.Percent,
                    SourceIds.SystemCpu),
            },
        };
        return new AgentSnapshot(
            ContractVersions.V1,
            ProductVersions.Agent,
            instanceId,
            sequence,
            completedAtUtc,
            completedAtUtc,
            completedAtUtc,
            0,
            new SnapshotSummary(
                AvailabilityStates.Available,
                FreshnessStates.Fresh),
            new SnapshotRetention(3600, 86_400),
            new Dictionary<string, SnapshotGroup>(
                StringComparer.Ordinal)
            {
                [GroupIds.SystemCpu] = new SnapshotGroup(
                    ProviderIds.SystemCpu,
                    completedAtUtc,
                    AvailabilityStates.Available,
                    FreshnessStates.Fresh,
                    ProviderCoverage.Complete,
                    [],
                    data),
            });
    }

    private static double ReadCpu(AgentSnapshot snapshot) =>
        snapshot.Groups[GroupIds.SystemCpu].Data!["metrics"]![
            MetricIds.SystemCpuUtilization]!["value"]!
            .GetValue<double>();

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        using var stopping = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                stopping.Token);
        }
    }

    private sealed class FakeIpcService : IAgentIpcService
    {
        private readonly AgentSnapshot _snapshot;
        private readonly CapabilitiesContract _capabilities;

        public FakeIpcService(string instanceId, long sequence)
        {
            InstanceId = instanceId;
            _snapshot = CreateSnapshot(
                instanceId,
                sequence,
                DateTimeOffset.UtcNow,
                12.5);
            _capabilities = new CapabilitiesContract
            {
                ContractVersion = ContractVersions.V1,
                ProductVersion = ProductVersions.Agent,
                InstanceId = instanceId,
                Groups =
                [
                    new ProviderCapabilityContract
                    {
                        GroupId = GroupIds.SystemCpu,
                        ProviderId = ProviderIds.SystemCpu,
                        DefaultPeriodMs = 1_000,
                        RequiredPrivilege = "user",
                        CostClass = "low",
                    },
                ],
                History = new HistoryCapabilityContract
                {
                    MetricIds =
                        HistoryPolicy.SupportedMetricIds,
                    DefaultMaxPoints =
                        HistoryPolicy.DefaultMaxPoints,
                    MaxPoints = HistoryPolicy.MaxPoints,
                    RamPointLimit = HistoryPolicy.RamPointLimit,
                    Aggregations = HistoryPolicy.Aggregations,
                },
                Diagnostics = new DiagnosticsCapabilityContract
                {
                    DefaultMaxEvents =
                        DiagnosticPolicyLimits.DefaultMaxEvents,
                    MaxEvents = DiagnosticPolicyLimits.MaxEvents,
                    ActionsSupported = false,
                    Rules = [],
                },
                Endpoints = new Dictionary<string, string>
                {
                    ["snapshot"] = "pipe:test/snapshot",
                    ["history"] = "pipe:test/history",
                    ["diagnostics"] = "pipe:test/diagnostics",
                    ["capabilities"] = "pipe:test/capabilities",
                    ["health"] = "pipe:test/health",
                },
                StableErrorCodes =
                    [IpcErrorCodes.InvalidRequest],
            };
        }

        public string InstanceId { get; }

        public AgentSnapshot ReadLatestSnapshot() => _snapshot;

        public HealthContract ReadHealth() => new()
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            Service = ServiceIds.PerfMonitor,
            InstanceId = InstanceId,
            Sequence = _snapshot.Sequence,
            CompletedAtUtc = _snapshot.CompletedAtUtc,
            Summary = new SummaryContract
            {
                Availability = _snapshot.Summary.Availability,
                Freshness = _snapshot.Summary.Freshness,
            },
        };

        public CapabilitiesContract ReadCapabilities() =>
            _capabilities;

        public ValueTask<HistoryContract> QueryHistoryAsync(
            HistoryQueryContract query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new HistoryContract
            {
                ContractVersion = ContractVersions.V1,
                ProductVersion = ProductVersions.Agent,
                InstanceId = InstanceId,
                Query = query,
                SourcePointCount = 0,
                PointCount = 0,
                Downsampled = false,
                Points = [],
            });

        public ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(
            DiagnosticQueryContract query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DiagnosticsContract
            {
                ContractVersion = ContractVersions.V1,
                ProductVersion = ProductVersions.Agent,
                InstanceId = InstanceId,
                Query = query,
                EventCount = 0,
                Truncated = false,
                Events = [],
            });
    }
}
