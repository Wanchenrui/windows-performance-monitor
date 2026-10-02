using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SnapshotAssemblerTests
{
    [TestMethod]
    public async Task AvailabilityAndFreshnessRemainOrthogonal()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero));
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var memory = Descriptor(GroupIds.Memory, ProviderIds.Memory);
        var assembler = new SnapshotAssembler(
            [cpu, memory],
            time,
            "11111111111111111111111111111111");

        await assembler.PublishAsync(
            DelegateProvider.Available(cpu, time.GetUtcNow()),
            Execution(cpu, time.GetUtcNow()),
            CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        var snapshot = assembler.Read();

        Assert.AreEqual(
            AvailabilityStates.Partial,
            snapshot.Summary.Availability);
        Assert.AreEqual(
            FreshnessStates.Stale,
            snapshot.Summary.Freshness);
        Assert.AreEqual(
            FreshnessStates.Stale,
            snapshot.Groups[GroupIds.SystemCpu].Freshness);
        Assert.AreEqual(
            FreshnessStates.WarmingUp,
            snapshot.Groups[GroupIds.Memory].Freshness);
    }

    [TestMethod]
    public async Task SnapshotJsonDeserializesThroughFrozenContractDto()
    {
        var descriptor = Descriptor(
            GroupIds.SystemCpu,
            ProviderIds.SystemCpu);
        var now = DateTimeOffset.UtcNow;
        var assembler = new SnapshotAssembler(
            [descriptor],
            instanceId: "22222222222222222222222222222222");
        await assembler.PublishAsync(
            DelegateProvider.Available(descriptor, now),
            Execution(descriptor, now),
            CancellationToken.None);

        var json = AgentJson.Serialize(assembler.Read());
        var contract = JsonSerializer.Deserialize<SnapshotContract>(
            json,
            ContractJson.Options);

        Assert.IsNotNull(contract);
        Assert.AreEqual(ContractVersions.V1, contract.ContractVersion);
        Assert.AreEqual(ProductVersions.Agent, contract.ProductVersion);
        Assert.IsTrue(contract.Groups.ContainsKey(GroupIds.SystemCpu));
        Assert.IsTrue(contract.Groups.ContainsKey(GroupIds.Sampler));
    }

    [TestMethod]
    public async Task FreshnessAndAgeUseMonotonicTimeAcrossUtcJumps()
    {
        var origin = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(origin);
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var assembler = new SnapshotAssembler([cpu], time, "clock-test");
        await assembler.PublishAsync(DelegateProvider.Available(cpu, origin),
            Execution(cpu, origin), CancellationToken.None);

        time.SetUtcNow(origin.AddDays(1));
        var forward = assembler.Read();
        Assert.AreEqual(FreshnessStates.Fresh, forward.Groups[GroupIds.SystemCpu].Freshness);
        Assert.AreEqual(0.0, forward.DataAgeSeconds);
        Assert.AreEqual(0.0, forward.ElapsedSeconds);

        time.Advance(TimeSpan.FromSeconds(4));
        time.SetUtcNow(origin.AddDays(-1));
        var backward = assembler.Read();
        Assert.AreEqual(FreshnessStates.Stale, backward.Groups[GroupIds.SystemCpu].Freshness);
        Assert.AreEqual(4.0, backward.DataAgeSeconds);
        Assert.AreEqual(4.0, backward.ElapsedSeconds);
        Assert.AreEqual(origin, backward.CompletedAtUtc);
    }

    [TestMethod]
    public async Task HeldGroupsKeepObservationIdentityAndTime()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(
            2026, 7, 29, 8, 0, 0, TimeSpan.Zero));
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var memory = Descriptor(GroupIds.Memory, ProviderIds.Memory);
        var assembler = new SnapshotAssembler([cpu, memory], time, "held-test");
        await assembler.PublishAsync(DelegateProvider.Available(cpu, time.GetUtcNow()),
            Execution(cpu, time.GetUtcNow()), CancellationToken.None);
        var first = assembler.Read().Groups[GroupIds.SystemCpu];

        time.Advance(TimeSpan.FromSeconds(1));
        await assembler.PublishAsync(DelegateProvider.Available(memory, time.GetUtcNow()),
            Execution(memory, time.GetUtcNow()), CancellationToken.None);
        var second = assembler.Read();
        Assert.AreEqual(2L, second.Sequence);
        Assert.AreEqual(first.ObservationSequence,
            second.Groups[GroupIds.SystemCpu].ObservationSequence);
        Assert.AreEqual(first.ObservedElapsedSeconds,
            second.Groups[GroupIds.SystemCpu].ObservedElapsedSeconds);
        Assert.AreEqual(2L, second.Groups[GroupIds.Memory].ObservationSequence);
        Assert.IsTrue(second.Groups[GroupIds.Memory].ObservedElapsedSeconds >
            first.ObservedElapsedSeconds);
    }

    [TestMethod]
    public async Task LongCollectionIsAlreadyStaleWhenPublished()
    {
        var origin = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(origin);
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var assembler = new SnapshotAssembler([cpu], time, "slow-test");
        time.Advance(TimeSpan.FromSeconds(4));
        await assembler.PublishAsync(DelegateProvider.Available(cpu, origin),
            Execution(cpu, time.GetUtcNow()) with { DurationMilliseconds = 4000 },
            CancellationToken.None);

        Assert.AreEqual(0.0, assembler.Read().Groups[GroupIds.SystemCpu].ObservedElapsedSeconds);
        Assert.AreEqual(FreshnessStates.Stale,
            assembler.Read().Groups[GroupIds.SystemCpu].Freshness);
    }

    [TestMethod]
    public async Task PublicationDelayDoesNotRenewObservationTime()
    {
        var origin = new DateTimeOffset(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(origin);
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var assembler = new SnapshotAssembler([cpu], time, "delayed-test");
        var started = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(100));
        var completed = time.GetTimestamp();
        var execution = Execution(cpu, time.GetUtcNow()) with
        {
            DurationMilliseconds = 100,
            StartedTimestamp = started,
            CompletedTimestamp = completed,
        };
        time.Advance(TimeSpan.FromSeconds(4));
        time.SetUtcNow(origin.AddDays(-1));
        await assembler.PublishAsync(DelegateProvider.Available(cpu, origin), execution,
            CancellationToken.None);

        var snapshot = assembler.Read();
        Assert.AreEqual(0.0, snapshot.Groups[GroupIds.SystemCpu].ObservedElapsedSeconds);
        Assert.AreEqual(0.1, snapshot.Groups[GroupIds.Sampler].ObservedElapsedSeconds);
        Assert.AreEqual(4.0, snapshot.DataAgeSeconds!.Value, 0.000001);
        Assert.AreEqual(FreshnessStates.Stale, snapshot.Groups[GroupIds.SystemCpu].Freshness);
    }

    private static ProviderDescriptor Descriptor(
        string groupId,
        string providerId) =>
        new(
            groupId,
            providerId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(500),
            "user",
            "low");

    private static ProviderExecution Execution(
        ProviderDescriptor descriptor,
        DateTimeOffset now) =>
        new(
            descriptor,
            now,
            now,
            now,
            1,
            0,
            0,
            0);
}
