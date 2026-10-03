using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SnapshotOwnershipTests
{
    [TestMethod]
    public async Task ProviderAndConsumerMutationsCannotChangePublishedSnapshots()
    {
        var time = new ManualTimeProvider(Origin);
        var descriptor = Descriptor(GroupIds.Processes, ProviderIds.Processes);
        var data = new JsonArray(new JsonObject
        {
            ["name"] = "original",
            ["identity"] = new JsonObject { ["pid"] = 1, ["creationTimeTicks"] = 639_000_000_000_000_001L },
            ["metrics"] = new JsonObject { ["value"] = 20 },
        });
        var reasons = new Dictionary<string, int> { ["original"] = 1 };
        var errors = new List<ProviderError> { new("original", null) };
        var result = new ProviderResult(descriptor.GroupId, descriptor.ProviderId, Origin,
            AvailabilityStates.Partial, new("limited", 2, 1, 1, reasons), errors, data);
        var assembler = new SnapshotAssembler([descriptor], time, "ownership");
        await assembler.PublishAsync(result, Execution(descriptor, time), CancellationToken.None);
        var published = assembler.Read();
        var before = AgentJson.Serialize(published);

        data[0]!["name"] = "provider-mutated";
        data.Add(new JsonObject { ["name"] = "provider-added" });
        reasons["original"] = 99;
        reasons["added"] = 1;
        errors[0] = new("changed", null);
        errors.Add(new("added", null));

        var consumerView = (JsonArray)published.Groups[descriptor.GroupId].Data!;
        consumerView[0]!["name"] = "consumer-mutated";
        consumerView[0]!["identity"]!["creationTimeTicks"] = 1;
        consumerView.Clear();

        Assert.AreEqual(before, AgentJson.Serialize(published));
        Assert.AreEqual(before, AgentJson.Serialize(assembler.Read()));
        Assert.AreEqual("original", published.Groups[descriptor.GroupId].Data![0]!["name"]!.GetValue<string>());
        Assert.AreEqual(639_000_000_000_000_001L,
            published.Groups[descriptor.GroupId].Data![0]!["identity"]!["creationTimeTicks"]!.GetValue<long>());
        Assert.AreEqual(1, published.Groups[descriptor.GroupId].Errors.Count);
        Assert.AreEqual(1, published.Groups[descriptor.GroupId].Coverage.SkippedByReason!["original"]);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, SnapshotGroup>)published.Groups).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ProviderError>)published.Groups[descriptor.GroupId].Errors).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, int>)published.Groups[descriptor.GroupId].Coverage.SkippedByReason!).Clear());
    }

    [TestMethod]
    public void SnapshotConstructionAndWithAssignmentsOwnTheirInputs()
    {
        var data = new JsonObject { ["nested"] = new JsonArray(new JsonObject { ["value"] = 1 }) };
        var group = new SnapshotGroup("provider", Origin, AvailabilityStates.Available,
            FreshnessStates.Fresh, ProviderCoverage.Complete, [], data);
        var groups = new Dictionary<string, SnapshotGroup> { ["group"] = group };
        var snapshot = new AgentSnapshot(ContractVersions.V1, ProductVersions.Agent, "instance", 1,
            Origin, Origin, Origin, 0, new("available", "fresh"), new(3600, 3600), groups);
        data["nested"]![0]!["value"] = 2;
        groups.Clear();
        Assert.AreEqual(1, snapshot.Groups["group"].Data!["nested"]![0]!["value"]!.GetValue<int>());

        var view = group.Data!;
        view["nested"]![0]!["value"] = 3;
        var replacement = group with { Data = view };
        view["nested"]![0]!["value"] = 4;
        Assert.AreEqual(1, group.Data!["nested"]![0]!["value"]!.GetValue<int>());
        Assert.AreEqual(3, replacement.Data!["nested"]![0]!["value"]!.GetValue<int>());
        var newGroups = new Dictionary<string, SnapshotGroup> { ["new"] = replacement };
        var copied = snapshot with { Groups = newGroups, DeliverySequence = 2 };
        newGroups.Clear();
        Assert.AreEqual(1, copied.Groups.Count);
        Assert.AreEqual(1, snapshot.Groups.Count);
        Assert.AreEqual(2L, copied.DeliverySequence);
    }

    [TestMethod]
    public void DataViewsPreserveRootShapesAndJsonNodeOptions()
    {
        var objectData = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
        {
            ["Known"] = new JsonArray(1, "two", null, new JsonObject { ["flag"] = true }),
        };
        var group = Group(objectData);
        var view = group.Data!;
        Assert.AreEqual(1, view["known"]![0]!.GetValue<int>());
        view["known"]![3]!["flag"] = false;
        Assert.IsTrue(group.Data!["KNOWN"]![3]!["flag"]!.GetValue<bool>());
        Assert.IsInstanceOfType<JsonArray>(Group(new JsonArray(1)).Data);
        Assert.AreEqual("text", Group(JsonValue.Create("text")).Data!.GetValue<string>());
        Assert.AreEqual(639_000_000_000_000_001L,
            Group(JsonValue.Create(639_000_000_000_000_001L)).Data!.GetValue<long>());
        Assert.IsNull(Group(null).Data);
    }

    [TestMethod]
    public void ReadOnlyDataRetainsItsOwnLifetimeAcrossCopiesAndMutableViews()
    {
        SnapshotGroup group;
        using (var document = JsonDocument.Parse("{\"value\":639000000000000001}"))
            group = Group(JsonObject.Create(document.RootElement));
        var immutable = group.ReadOnlyData!.Value;
        var view = group.Data!;
        view["value"] = 3;
        var changed = group with { Data = view };
        Assert.AreEqual(639_000_000_000_000_001L, immutable.GetProperty("value").GetInt64());
        Assert.AreEqual(639_000_000_000_000_001L, group.ReadOnlyData!.Value.GetProperty("value").GetInt64());
        Assert.AreEqual(3, changed.ReadOnlyData!.Value.GetProperty("value").GetInt32());
        Assert.IsFalse(JsonSerializer.Serialize(group, AgentJson.Options).Contains("readOnlyData", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NonFiniteValuesRemainIsolatedMissingDataAndCannotBeSerialized()
    {
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var data = new JsonObject
            {
                ["values"] = new JsonArray(new JsonObject { ["value"] = invalid }, 4),
            };
            var group = Group(data);
            data["values"]![0]!["value"] = 1;
            var view = group.Data!;
            Assert.IsFalse(double.IsFinite(view["values"]![0]!["value"]!.GetValue<double>()));
            view["values"]![0]!["value"] = 2;
            Assert.IsFalse(double.IsFinite(group.Data!["values"]![0]!["value"]!.GetValue<double>()));
            Assert.AreEqual(JsonValueKind.Null,
                group.ReadOnlyData!.Value.GetProperty("values")[0].GetProperty("value").ValueKind);
            Assert.AreEqual(4, group.ReadOnlyData.Value.GetProperty("values")[1].GetInt32());
            Assert.ThrowsExactly<ArgumentException>(() => JsonSerializer.Serialize(group, AgentJson.Options));
            var repaired = group with { Data = view };
            Assert.IsNotNull(JsonSerializer.Serialize(repaired, AgentJson.Options));
        }
    }

    [TestMethod]
    public void FreezeDoesNotSuppressUnrelatedSerializationErrors()
    {
        var value = JsonValue.Create(new UnsupportedValue());
        var error = Assert.ThrowsExactly<ArgumentException>(() => Group(value));
        StringAssert.Contains(error.Message, "unrelated serialization failure");
        var mixed = new JsonObject
        {
            ["invalid"] = double.NaN,
            ["unrelated"] = JsonValue.Create(new UnsupportedValue()),
        };
        var mixedError = Assert.ThrowsExactly<ArgumentException>(() => Group(mixed));
        StringAssert.Contains(mixedError.Message, "unrelated serialization failure");
        var reversed = new JsonObject
        {
            ["unrelated"] = JsonValue.Create(new UnsupportedValue()),
            ["invalid"] = double.NaN,
        };
        var reversedError = Assert.ThrowsExactly<ArgumentException>(() => Group(reversed));
        StringAssert.Contains(reversedError.Message, "unrelated serialization failure");
    }

    [TestMethod]
    public async Task FrozenPayloadRoundTripsThroughContractAndRuntimeDtos()
    {
        var time = new ManualTimeProvider(Origin);
        var descriptor = Descriptor(GroupIds.Processes, ProviderIds.Processes);
        var data = new JsonArray(new JsonObject
        {
            ["identity"] = new JsonObject { ["pid"] = 123, ["creationTimeTicks"] = 639_000_000_000_000_001L },
            ["name"] = "roundtrip", ["cpuReady"] = true,
            ["metrics"] = new JsonObject
            {
                [MetricIds.ProcessCpuNormalized] = MetricJson.Value(25.5, Units.Percent, SourceIds.ProcessCpu),
            },
        });
        var assembler = new SnapshotAssembler([descriptor], time, "roundtrip");
        await assembler.PublishAsync(new(descriptor.GroupId, descriptor.ProviderId, Origin,
            AvailabilityStates.Available, ProviderCoverage.Complete, [], data),
            Execution(descriptor, time), CancellationToken.None);
        var json = AgentJson.Serialize(assembler.Read() with { DeliverySequence = 5 });
        var contract = JsonSerializer.Deserialize<SnapshotContract>(json, ContractJson.Options)!;
        var restored = JsonSerializer.Deserialize<AgentSnapshot>(json, ContractJson.Options)!;
        Assert.AreEqual(ContractVersions.V1, contract.ContractVersion);
        Assert.AreEqual(json, AgentJson.Serialize(restored));
        Assert.AreEqual(5L, restored.DeliverySequence);
        Assert.AreEqual(1L, restored.Groups[descriptor.GroupId].ObservationSequence);
        var view = restored.Groups[descriptor.GroupId].Data!;
        view[0]!["name"] = "changed";
        Assert.AreEqual(json, AgentJson.Serialize(restored));
        Assert.AreEqual(639_000_000_000_000_001L,
            restored.Groups[descriptor.GroupId].Data![0]!["identity"]!["creationTimeTicks"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task HeldGroupsAndStaleViewsRetainTheirOriginalPayload()
    {
        var time = new ManualTimeProvider(Origin);
        var cpu = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var memory = Descriptor(GroupIds.Memory, ProviderIds.Memory);
        var assembler = new SnapshotAssembler([cpu, memory], time, "held");
        await assembler.PublishAsync(Result(cpu, 1), Execution(cpu, time), CancellationToken.None);
        var first = assembler.Read();
        time.Advance(TimeSpan.FromSeconds(1));
        await assembler.PublishAsync(Result(memory, 2), Execution(memory, time), CancellationToken.None);
        var second = assembler.Read();
        Assert.AreSame(first.Groups[cpu.GroupId], second.Groups[cpu.GroupId]);
        Assert.AreEqual(2L, second.Sequence);
        time.Advance(TimeSpan.FromSeconds(3));
        var stale = assembler.Read();
        Assert.AreEqual(FreshnessStates.Stale, stale.Groups[cpu.GroupId].Freshness);
        Assert.AreEqual(FreshnessStates.Fresh, first.Groups[cpu.GroupId].Freshness);
        Assert.AreEqual(first.Groups[cpu.GroupId].ObservationSequence, stale.Groups[cpu.GroupId].ObservationSequence);
        var view = stale.Groups[cpu.GroupId].Data!;
        view["publication"] = 999;
        Assert.AreEqual(1, first.Groups[cpu.GroupId].Data!["publication"]!.GetValue<int>());
        Assert.AreEqual(1, assembler.Read().Groups[cpu.GroupId].Data!["publication"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ConcurrentReadersObserveOnlyCompletePublicationVersions()
    {
        var descriptor = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var time = new ManualTimeProvider(Origin);
        var assembler = new SnapshotAssembler([descriptor], time, "atomic");
        await assembler.PublishAsync(Result(descriptor, 1), Execution(descriptor, time), CancellationToken.None);
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(async () =>
        {
            start.Wait();
            for (var version = 2; version <= 200; version++)
                await assembler.PublishAsync(Result(descriptor, version), Execution(descriptor, time), CancellationToken.None);
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            long previous = 0;
            for (var index = 0; index < 500; index++)
            {
                var snapshot = assembler.Read();
                var group = snapshot.Groups[descriptor.GroupId];
                Assert.AreEqual(snapshot.Sequence, (long)group.Data!["publication"]!.GetValue<int>());
                Assert.AreEqual(snapshot.Sequence, group.ObservationSequence);
                Assert.AreEqual(snapshot.Sequence, snapshot.Groups[GroupIds.Sampler].ObservationSequence);
                Assert.AreEqual(Origin, snapshot.CompletedAtUtc);
                Assert.AreEqual("atomic", snapshot.InstanceId);
                Assert.IsTrue(snapshot.Sequence >= previous);
                previous = snapshot.Sequence;
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(readers.Append(writer));
        Assert.AreEqual(200L, assembler.Read().Sequence);
    }

    [TestMethod]
    public async Task CapacityFailureDoesNotBecomeAProviderObservation()
    {
        var time = new ManualTimeProvider(Origin);
        var descriptor = Descriptor(GroupIds.SystemCpu, ProviderIds.SystemCpu);
        var assembler = new SnapshotAssembler([descriptor], time, "capacity");
        await assembler.PublishAsync(new(descriptor.GroupId, descriptor.ProviderId, null,
            AvailabilityStates.Timeout, ProviderCoverage.Limited, [new(StableErrorCodes.Timeout, null)], null),
            Execution(descriptor, time), CancellationToken.None);
        var snapshot = assembler.Read();
        Assert.AreEqual(AvailabilityStates.Timeout, snapshot.Groups[descriptor.GroupId].Availability);
        Assert.AreEqual(FreshnessStates.WarmingUp, snapshot.Groups[descriptor.GroupId].Freshness);
        Assert.IsNull(snapshot.Groups[descriptor.GroupId].ObservedElapsedSeconds);
        Assert.IsNull(snapshot.Groups[descriptor.GroupId].ObservedAtUtc);
        Assert.IsNull(snapshot.Groups[descriptor.GroupId].Data);
        Assert.AreEqual(FreshnessStates.WarmingUp, snapshot.Summary.Freshness);
    }

    private static readonly DateTimeOffset Origin = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private static ProviderDescriptor Descriptor(string group, string provider) =>
        new(group, provider, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), "user", "low");
    private static ProviderExecution Execution(ProviderDescriptor descriptor, TimeProvider time) =>
        new(descriptor, time.GetUtcNow(), time.GetUtcNow(), time.GetUtcNow(), 0, 0, 0, 0)
        { StartedTimestamp = time.GetTimestamp(), CompletedTimestamp = time.GetTimestamp() };
    private static ProviderResult Result(ProviderDescriptor descriptor, int publication) =>
        new(descriptor.GroupId, descriptor.ProviderId, Origin, AvailabilityStates.Available,
            ProviderCoverage.Complete, [], new JsonObject { ["publication"] = publication });
    private static SnapshotGroup Group(JsonNode? data) =>
        new("provider", Origin, AvailabilityStates.Available, FreshnessStates.Fresh, ProviderCoverage.Complete, [], data);

    private sealed class UnsupportedValue
    {
        public int Value => throw new ArgumentException("unrelated serialization failure");
    }
}
