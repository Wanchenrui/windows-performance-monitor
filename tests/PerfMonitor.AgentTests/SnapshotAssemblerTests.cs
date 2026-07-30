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
