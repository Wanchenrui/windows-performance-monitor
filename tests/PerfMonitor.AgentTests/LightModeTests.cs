using System.Collections.Concurrent;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class LightModeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedEnableRestoresOriginalSessionSetting(bool originallyPaused)
    {
        var gpu = Descriptor(GroupIds.Gpu);
        var cpu = Descriptor(GroupIds.SystemCpu);
        var mode = new ProviderSamplingMode { OptionalHardwarePaused = originallyPaused };
        var assembler = new SnapshotAssembler([gpu, cpu]) { SamplingMode = mode };
        var controller = new AgentLightModeController(mode, assembler, [gpu, cpu]);
        var now = DateTimeOffset.UtcNow;
        assembler.PublishAsync(DelegateProvider.Available(cpu, now),
            new ProviderExecution(cpu, now, now, now, 1, 0, 0, 0), CancellationToken.None);
        var held = assembler.Read();

        var enabled = controller.Set(true, CancellationToken.None);
        var firstSequence = assembler.Read().Sequence;
        var repeated = controller.Set(true, CancellationToken.None);
        Assert.IsTrue(enabled.Enabled && repeated.Enabled && enabled.CanRestore && enabled.SessionOnly);
        CollectionAssert.AreEqual(new[] { GroupIds.Gpu }, enabled.PausedGroups.ToArray());
        Assert.AreEqual(firstSequence, assembler.Read().Sequence);
        Assert.IsTrue(mode.OptionalHardwarePaused);
        Assert.AreEqual(held.Groups[GroupIds.SystemCpu].ObservationSequence,
            assembler.Read().Groups[GroupIds.SystemCpu].ObservationSequence);

        var paused = assembler.Read().Groups[GroupIds.Gpu];
        AssertPaused(paused);
        Assert.IsNull(held.Groups[GroupIds.Gpu].CollectionState);
        var json = AgentJson.Serialize(assembler.Read());
        var roundTrip = JsonSerializer.Deserialize<AgentSnapshot>(json, AgentJson.Options)!;
        AssertPaused(roundTrip.Groups[GroupIds.Gpu]);
        var legacy = JsonSerializer.Deserialize<SnapshotContract>(json, ContractJson.Options)!;
        Assert.AreEqual(FreshnessStates.WarmingUp, legacy.Groups[GroupIds.Gpu].Freshness);
        Assert.IsNull(legacy.Groups[GroupIds.Gpu].ObservedAtUtc);

        var restored = controller.Set(false, CancellationToken.None);
        Assert.IsFalse(restored.Enabled || restored.CanRestore);
        Assert.AreEqual(originallyPaused, mode.OptionalHardwarePaused);
        var group = assembler.Read().Groups[GroupIds.Gpu];
        Assert.IsNull(group.ObservedAtUtc);
        Assert.IsNull(group.ObservedElapsedSeconds);
        Assert.AreEqual(FreshnessStates.WarmingUp, group.Freshness);
        Assert.AreEqual(originallyPaused ? "paused" : null, group.CollectionState);
        var restoredSequence = assembler.Read().Sequence;
        controller.Set(false, CancellationToken.None);
        Assert.AreEqual(restoredSequence, assembler.Read().Sequence);
    }

    [TestMethod]
    public void UnregisteredHardwareAndCancelledRequestDoNotClaimSavingsOrChangeSettings()
    {
        var cpu = Descriptor(GroupIds.SystemCpu);
        var mode = new ProviderSamplingMode();
        var assembler = new SnapshotAssembler([cpu]) { SamplingMode = mode };
        var controller = new AgentLightModeController(mode, assembler, [cpu]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => controller.Set(true, cancelled.Token));
        Assert.IsFalse(controller.Read().Enabled);
        var status = controller.Set(true, CancellationToken.None);
        Assert.HasCount(0, status.PausedGroups);
        StringAssert.Contains(status.ImpactDescription, "不会改变当前采集");
        Assert.AreEqual(0L, assembler.Read().Sequence);
        var newMode = new ProviderSamplingMode();
        var newAssembler = new SnapshotAssembler([cpu]) { SamplingMode = newMode };
        Assert.IsFalse(new AgentLightModeController(newMode, newAssembler, [cpu]).Read().Enabled);
    }

    [TestMethod]
    [Timeout(8000)]
    public async Task PausedHardwareSkipsRequestsWhileBasicCollectionContinuesAndRestoreSamplesAgain()
    {
        var gpu = Descriptor(GroupIds.Gpu);
        var cpu = Descriptor(GroupIds.SystemCpu);
        var calls = 0;
        var hardware = new DelegateProvider(gpu, (context, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DelegateProvider.Available(gpu, context.UtcNow));
        });
        var basic = new DelegateProvider(cpu, (context, _) =>
            Task.FromResult(DelegateProvider.Available(cpu, context.UtcNow)));
        var mode = new ProviderSamplingMode();
        var assembler = new SnapshotAssembler([gpu, cpu]) { SamplingMode = mode };
        var controller = new AgentLightModeController(mode, assembler, [gpu, cpu]);
        controller.Set(true, CancellationToken.None);
        var sink = new TrackingSink(assembler);
        await using var scheduler = new ProviderScheduler([hardware, basic], sink, 2,
            reservedGroupIds: [cpu.GroupId]) { SamplingMode = mode };
        scheduler.Start();
        await WaitUntilAsync(() => sink.Items.Count(item => item.Result.GroupId == cpu.GroupId) >= 3);
        Assert.AreEqual(0, Volatile.Read(ref calls));
        AssertPaused(assembler.Read().Groups[gpu.GroupId]);
        Assert.AreEqual(FreshnessStates.Fresh, assembler.Read().Groups[cpu.GroupId].Freshness);
        controller.Set(false, CancellationToken.None);
        Assert.IsNull(assembler.Read().Groups[gpu.GroupId].ObservedAtUtc);
        await WaitUntilAsync(() => assembler.Read().Groups[gpu.GroupId].ObservedAtUtc is not null);
        Assert.IsTrue(Volatile.Read(ref calls) > 0);
        Assert.IsNull(assembler.Read().Groups[gpu.GroupId].CollectionState);
        await scheduler.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10000)]
    public async Task PauseAndQuickRestoreRejectOldResultAndKeepItsSlot(bool letOldCallTimeout)
    {
        var gpu = Descriptor(GroupIds.Gpu, letOldCallTimeout ? 80 : 2000);
        var sensors = Descriptor(GroupIds.Sensors, 2000);
        var cpu = Descriptor(GroupIds.SystemCpu);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sensorCalls = 0;
        var hardware = new DelegateProvider(gpu, async (context, _) =>
        {
            var sample = Interlocked.Increment(ref calls);
            if (sample == 1) { firstStarted.TrySetResult(); await firstRelease.Task; }
            else { secondStarted.TrySetResult(); await secondRelease.Task; }
            return DelegateProvider.Available(gpu, context.UtcNow) with
            { Data = new JsonObject { ["sample"] = sample } };
        });
        var otherHardware = new DelegateProvider(sensors, (context, _) =>
        {
            Interlocked.Increment(ref sensorCalls);
            return Task.FromResult(DelegateProvider.Available(sensors, context.UtcNow));
        });
        var basic = new DelegateProvider(cpu, (context, _) =>
            Task.FromResult(DelegateProvider.Available(cpu, context.UtcNow)));
        var mode = new ProviderSamplingMode();
        var assembler = new SnapshotAssembler([gpu, sensors, cpu]) { SamplingMode = mode };
        var controller = new AgentLightModeController(mode, assembler, [gpu, sensors, cpu]);
        var sink = new TrackingSink(assembler);
        await using var scheduler = new ProviderScheduler([hardware, otherHardware, basic], sink, 2,
            reservedGroupIds: [cpu.GroupId]) { SamplingMode = mode };
        try
        {
            scheduler.Start();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            controller.Set(true, CancellationToken.None);
            AssertPaused(assembler.Read().Groups[gpu.GroupId]);
            controller.Set(false, CancellationToken.None);
            if (letOldCallTimeout)
                await WaitUntilAsync(() => sink.Items.Any(item => item.Result.GroupId == gpu.GroupId &&
                    item.Execution.SkippedBusyIntervalsTotal > 0));
            else
            {
                firstRelease.TrySetResult();
                await WaitUntilAsync(() => sink.Items.Any(item => item.Result.GroupId == gpu.GroupId &&
                    item.Result.Availability == AvailabilityStates.Available));
            }
            Assert.IsNull(assembler.Read().Groups[gpu.GroupId].ObservedAtUtc);
            Assert.IsNull(assembler.Read().Groups[gpu.GroupId].Data);
            Assert.AreEqual(FreshnessStates.WarmingUp, assembler.Read().Groups[gpu.GroupId].Freshness);
            if (letOldCallTimeout)
            {
                Assert.AreEqual(1, Volatile.Read(ref calls));
                Assert.AreEqual(0, Volatile.Read(ref sensorCalls));
                Assert.IsTrue(sink.Items.Count(item => item.Result.GroupId == cpu.GroupId) > 1);
            }
            firstRelease.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            secondRelease.TrySetResult();
            await WaitUntilAsync(() => assembler.Read().Groups[gpu.GroupId].ReadOnlyData is { } data &&
                data.GetProperty("sample").GetInt32() >= 2);
            Assert.IsNotNull(assembler.Read().Groups[gpu.GroupId].ObservedAtUtc);
        }
        finally
        {
            firstRelease.TrySetResult(); secondRelease.TrySetResult();
            await scheduler.StopAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10000)]
    public async Task IpcProbesLegacyCapabilityAndAppliesOnlySupportedSessionToggle(bool supported)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var endpoint = new PipeEndpoint($"PerfMonitor.light-mode-test.{Guid.NewGuid():N}", identity.User!);
        var gpu = Descriptor(GroupIds.Gpu);
        var mode = new ProviderSamplingMode();
        var assembler = new SnapshotAssembler([gpu]) { SamplingMode = mode };
        var controller = new AgentLightModeController(mode, assembler, [gpu]);
        var service = new LightService(assembler, controller, supported);
        await using var subscriptions = new SnapshotSubscriptionHub();
        await using var server = new NamedPipeAgentServer(endpoint, service, subscriptions);
        server.Start();
        await using var client = await NamedPipeAgentClient.ConnectAsync(endpoint,
            TimeSpan.FromSeconds(3), CancellationToken.None);
        var status = await client.GetLightModeAsync(CancellationToken.None);
        Assert.AreEqual(supported, status.Supported);
        var enabled = await client.SetLightModeAsync(true, CancellationToken.None);
        Assert.AreEqual(supported, enabled.Enabled);
        Assert.AreEqual(supported, mode.OptionalHardwarePaused);
        if (supported)
        {
            AssertPaused((await client.GetSnapshotAsync(CancellationToken.None)).Groups[gpu.GroupId]);
            var restored = await client.SetLightModeAsync(false, CancellationToken.None);
            Assert.IsFalse(restored.Enabled || restored.CanRestore || mode.OptionalHardwarePaused);
            Assert.IsNull((await client.GetSnapshotAsync(CancellationToken.None)).Groups[gpu.GroupId].ObservedAtUtc);
        }
        else Assert.AreEqual(0, service.ModeRequests);
    }

    private static void AssertPaused(SnapshotGroup group)
    {
        Assert.AreEqual("paused", group.CollectionState);
        Assert.AreEqual(AvailabilityStates.Unavailable, group.Availability);
        Assert.AreEqual(FreshnessStates.WarmingUp, group.Freshness);
        Assert.IsNull(group.ObservedAtUtc); Assert.IsNull(group.ObservedElapsedSeconds);
        Assert.IsNull(group.Data); Assert.HasCount(0, group.Errors);
    }

    private static ProviderDescriptor Descriptor(string groupId, int timeoutMs = 2000) =>
        new(groupId, "test." + groupId, TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(timeoutMs), "user", "low");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class TrackingSink(SnapshotAssembler assembler) : IProviderResultSink
    {
        public ConcurrentQueue<(ProviderResult Result, ProviderExecution Execution)> Items { get; } = new();
        public async ValueTask PublishAsync(ProviderResult result, ProviderExecution execution, CancellationToken ct)
        { await assembler.PublishAsync(result, execution, ct); Items.Enqueue((result, execution)); }
    }

    private sealed class LightService(SnapshotAssembler assembler, AgentLightModeController controller,
        bool supported) : IAgentIpcService
    {
        public int ModeRequests { get; private set; }
        public string InstanceId => assembler.InstanceId;
        public AgentSnapshot ReadLatestSnapshot() => assembler.Read();
        public HealthContract ReadHealth() => throw new NotSupportedException();
        public CapabilitiesContract ReadCapabilities() => new()
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, InstanceId = InstanceId,
            Groups = [], StableErrorCodes = [],
            History = new HistoryCapabilityContract { MetricIds = [], Aggregations = [] },
            Endpoints = supported ? new Dictionary<string, string> { ["lightMode"] = "pipe:test/light-mode" }
                : new Dictionary<string, string>(),
        };
        public LightModeContract ReadLightMode() { ModeRequests++; return controller.Read(); }
        public LightModeContract SetLightMode(bool enabled, CancellationToken ct)
        { ModeRequests++; return controller.Set(enabled, ct); }
        public ValueTask<HistoryContract> QueryHistoryAsync(HistoryQueryContract query, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(DiagnosticQueryContract query, CancellationToken ct) =>
            throw new NotSupportedException();
        public ValueTask<ActionResultContract> ExecuteActionAsync(UserActionRequestContract request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
