using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Collectors.Windows;
using PerfMonitor.Collectors.Worker;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class HardwareRecoveryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
    private static readonly ProviderDescriptor Gpu = new(GroupIds.Gpu, ProviderIds.Gpu,
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "user", "high");
    private static PowerStatusRead UnknownPower => new(255, 255, 255, 255, uint.MaxValue, uint.MaxValue);

    [TestMethod]
    public async Task UnknownPowerUsesBatteryStateWithoutInventingPercentOrWatts()
    {
        var source = new PowerSource(() => UnknownPower);
        var fallback = new BatterySource(() => new(false, true, false, true, 420));
        var provider = new PowerStatusProvider(source, fallback);
        var result = await provider.CollectAsync(Context(new(Start)), CancellationToken.None);
        Assert.AreEqual("battery", result.Data!["powerSource"]!.GetValue<string>());
        Assert.IsTrue(result.Data["batteryPresent"]!.GetValue<bool>());
        Assert.IsFalse(result.Data["charging"]!.GetValue<bool>());
        Assert.AreEqual("not_charging", result.Data["chargeStatus"]!.GetValue<string>());
        Assert.IsNull(Metric(result, MetricIds.BatteryChargePercent));
        Assert.IsNull(Metric(result, MetricIds.BatteryFullLifeSeconds));
        Assert.AreEqual(420d, Metric(result, MetricIds.BatteryLifeRemainingSeconds));
        Assert.AreEqual(SourceIds.PowerBatteryState,
            result.Data["metrics"]![MetricIds.BatteryLifeRemainingSeconds]!["sourceId"]!.GetValue<string>());
        Assert.AreEqual(AvailabilityStates.Partial, result.Availability);
        Assert.AreEqual(1, fallback.Calls);
        Assert.IsFalse(result.Data["metrics"]!.AsObject().Any(item => item.Key.Contains("watt")));
    }

    [TestMethod]
    [DataRow((byte)128, (byte)255)]
    [DataRow((byte)0, (byte)0)]
    public async Task KnownNoBatteryAndRealZeroChargeDoNotUseFallback(byte batteryFlag, byte percentage)
    {
        var fallback = new BatterySource(() => throw new AssertFailedException("Unexpected fallback."));
        var provider = new PowerStatusProvider(new PowerSource(() =>
            new(1, batteryFlag, percentage, 0, uint.MaxValue, uint.MaxValue)), fallback);
        var result = await provider.CollectAsync(Context(new(Start)), CancellationToken.None);
        Assert.AreEqual(0, fallback.Calls);
        Assert.AreEqual(AvailabilityStates.Available, result.Availability);
        if (batteryFlag == 128)
        {
            Assert.IsFalse(result.Data!["batteryPresent"]!.GetValue<bool>());
            Assert.IsNull(Metric(result, MetricIds.BatteryChargePercent));
        }
        else Assert.AreEqual(0d, Metric(result, MetricIds.BatteryChargePercent));
        Assert.IsNull(result.Data!["readout"]!["secondary"]);
    }

    [TestMethod]
    public async Task BothPowerPathsFailThenFallbackCooldownExpiresAndRecovers()
    {
        var time = new ManualTimeProvider(Start);
        var canRecover = false;
        var source = new PowerSource(() => throw new Win32Exception(5));
        var fallback = new BatterySource(() => canRecover ? new(true, false, false, false, 0)
            : throw new NotSupportedException());
        var provider = new PowerStatusProvider(source, fallback);
        var failed = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(AvailabilityStates.PermissionDenied, failed.Availability);
        Assert.IsNull(Metric(failed, MetricIds.BatteryChargePercent));
        time.Advance(TimeSpan.FromSeconds(5));
        await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(1, fallback.Calls);
        time.Advance(TimeSpan.FromSeconds(25)); canRecover = true;
        var recovered = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(2, fallback.Calls);
        Assert.AreEqual("ac", recovered.Data!["powerSource"]!.GetValue<string>());
        Assert.IsFalse(recovered.Data["batteryPresent"]!.GetValue<bool>());
        Assert.AreEqual("not_present", recovered.Data["chargeStatus"]!.GetValue<string>());
        Assert.IsNull(Metric(recovered, MetricIds.BatteryChargePercent));
    }

    [TestMethod]
    public async Task CancellationAfterPowerPrimaryPreventsFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var fallback = new BatterySource(() => new(true, true, false, false, 0));
        var provider = new PowerStatusProvider(new PowerSource(() =>
        { cancellation.Cancel(); return UnknownPower; }), fallback);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CollectAsync(Context(new(Start)), cancellation.Token));
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task RealGpuZeroIsReadableAndDoesNotInvokeFallback()
    {
        var fallback = new EngineSource(() => throw new AssertFailedException("Unexpected fallback."));
        await using var provider = new GpuLoadFallbackProvider(new DelegateProvider(Gpu,
            (context, _) => Task.FromResult(GpuReading(context.UtcNow, 0, 55))), fallback);
        var result = await provider.CollectAsync(Context(new(Start)), CancellationToken.None);
        Assert.AreEqual(0d, Metric(result, MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual(0, fallback.Calls);
        Assert.AreEqual(AvailabilityStates.Available, result.Availability);
    }

    [TestMethod]
    public async Task GpuFallbackFirstDifferenceIsUnknownThenZeroIsValidAndPrimaryCanRecover()
    {
        var time = new ManualTimeProvider(Start);
        var primaryCalls = 0; var canRecover = false;
        var primary = new DelegateProvider(Gpu, (context, _) =>
        {
            primaryCalls++;
            return Task.FromResult(canRecover ? GpuReading(context.UtcNow, 25, 60)
                : ProviderResult.Failure(Gpu, context.UtcNow, AvailabilityStates.NotSupported,
                    StableErrorCodes.NotSupported) with { CollectionState = "worker_missing" });
        });
        var samples = new Queue<GpuEngineSample>([new(false, null, 0), new(true, 0, 2)]);
        var fallback = new EngineSource(samples.Dequeue);
        await using var provider = new GpuLoadFallbackProvider(primary, fallback);
        var baseline = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.IsNull(baseline.ObservedAtUtc);
        Assert.IsNull(Metric(baseline, MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual("warming_up", baseline.Data!["readout"]!["secondaryStatus"]!.GetValue<string>());
        time.Advance(TimeSpan.FromSeconds(5));
        var ready = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(1, primaryCalls);
        Assert.AreEqual(0d, Metric(ready, MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual(time.GetUtcNow(), ready.ObservedAtUtc);
        Assert.AreEqual(AvailabilityStates.Partial, ready.Availability);
        Assert.IsNull(Metric(ready, MetricIds.GpuTemperatureMaxCelsius));
        Assert.IsNull(Metric(ready, MetricIds.GpuDeviceCount));
        Assert.AreEqual(0, ready.Data!["devices"]!.AsArray().Count);
        Assert.AreEqual("busiest_engine", ready.Data["loadAggregation"]!.GetValue<string>());
        Assert.AreEqual(SourceIds.WindowsGpuEngine,
            ready.Data["metrics"]![MetricIds.GpuLoadMaxPercent]!["sourceId"]!.GetValue<string>());
        // Existing snapshot wire and ownership retain the additive readout.
        var assembler = new SnapshotAssembler([Gpu], timeProvider: time);
        await assembler.PublishAsync(ready, new(Gpu, time.GetUtcNow(), time.GetUtcNow(),
            time.GetUtcNow(), 0, 0, 0, 0), CancellationToken.None);
        var serialized = AgentJson.Serialize(assembler.Read());
        var roundTrip = JsonSerializer.Deserialize<AgentSnapshot>(serialized, AgentJson.Options)!;
        Assert.AreEqual("busiest_engine", roundTrip.Groups[GroupIds.Gpu].ReadOnlyData!
            .Value.GetProperty("loadAggregation").GetString());
        var legacy = JsonSerializer.Deserialize<SnapshotContract>(serialized, ContractJson.Options)!;
        Assert.AreEqual(AvailabilityStates.Partial, legacy.Groups[GroupIds.Gpu].Availability);
        time.Advance(TimeSpan.FromSeconds(25)); canRecover = true;
        var recovered = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(2, primaryCalls);
        Assert.AreEqual(2, fallback.Calls);
        Assert.AreEqual(25d, Metric(recovered, MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual(60d, Metric(recovered, MetricIds.GpuTemperatureMaxCelsius));
        Assert.AreEqual(AvailabilityStates.Available, recovered.Availability);
    }

    [TestMethod]
    public async Task FallbackFailureDoesNotRepublishOldReadingAndRetriesAfterCooldown()
    {
        var time = new ManualTimeProvider(Start); var engineCalls = 0;
        var fallback = new EngineSource(() => ++engineCalls == 2
            ? throw new UnauthorizedAccessException() : new(true, 40, 1));
        await using var provider = new GpuLoadFallbackProvider(new DelegateProvider(Gpu,
            (context, _) => Task.FromResult(ProviderResult.Failure(Gpu, context.UtcNow,
                AvailabilityStates.NotSupported, StableErrorCodes.NotSupported))), fallback);
        Assert.AreEqual(40d, Metric(await provider.CollectAsync(Context(time), CancellationToken.None),
            MetricIds.GpuLoadMaxPercent));
        time.Advance(TimeSpan.FromSeconds(5));
        var failed = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.IsNull(failed.ObservedAtUtc);
        Assert.IsNull(Metric(failed, MetricIds.GpuLoadMaxPercent));
        Assert.IsTrue(failed.Errors.Any(error => error.ErrorCode == StableErrorCodes.AccessDenied));
        time.Advance(TimeSpan.FromSeconds(5));
        var waiting = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(2, engineCalls);
        Assert.IsNull(Metric(waiting, MetricIds.GpuLoadMaxPercent));
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.AreEqual(40d, Metric(await provider.CollectAsync(Context(time), CancellationToken.None),
            MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual(3, engineCalls);
    }

    [TestMethod]
    public async Task CancelledGpuPrimaryDoesNotStartFallbackAndNextCycleCanUseIt()
    {
        var time = new ManualTimeProvider(Start);
        using var cancellation = new CancellationTokenSource(); var primaryCalls = 0;
        var fallback = new EngineSource(() => new(true, 10, 1));
        await using var provider = new GpuLoadFallbackProvider(new DelegateProvider(Gpu, (_, token) =>
        { primaryCalls++; cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(GpuReading(Start, 1, 1)); }), fallback);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CollectAsync(Context(time), cancellation.Token));
        Assert.AreEqual(0, fallback.Calls);
        time.Advance(TimeSpan.FromSeconds(5));
        var next = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(1, primaryCalls);
        Assert.AreEqual(10d, Metric(next, MetricIds.GpuLoadMaxPercent));
    }

    [TestMethod]
    public async Task PausedGpuAndPreCancelledCallsDoNotStartFallback()
    {
        var primaryCalls = 0;
        var fallback = new EngineSource(() => throw new AssertFailedException("Unexpected fallback."));
        await using var provider = new GpuLoadFallbackProvider(new DelegateProvider(Gpu, (_, _) =>
        { primaryCalls++; return Task.FromResult(ProviderResult.Paused(Gpu)); }), fallback);
        var paused = await provider.CollectAsync(Context(new(Start)), CancellationToken.None);
        Assert.AreEqual("paused", paused.CollectionState);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CollectAsync(Context(new(Start)), cancelled.Token));
        Assert.AreEqual(1, primaryCalls);
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task GpuFallbackKeepsOnlySameCyclePrimaryTemperature()
    {
        var time = new ManualTimeProvider(Start); var first = true;
        var fallback = new EngineSource(() => first ? (first = false, new GpuEngineSample(false, null, 0)).Item2
            : new(true, 45, 1));
        await using var provider = new GpuLoadFallbackProvider(new DelegateProvider(Gpu,
            (context, _) => Task.FromResult(GpuReading(context.UtcNow, null, 71))), fallback);
        var current = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(71d, Metric(current, MetricIds.GpuTemperatureMaxCelsius));
        Assert.IsNull(Metric(current, MetricIds.GpuLoadMaxPercent));
        time.Advance(TimeSpan.FromSeconds(5));
        var later = await provider.CollectAsync(Context(time), CancellationToken.None);
        Assert.AreEqual(45d, Metric(later, MetricIds.GpuLoadMaxPercent));
        Assert.IsNull(Metric(later, MetricIds.GpuTemperatureMaxCelsius));
    }

    [TestMethod]
    public void WindowsGpuAggregationUsesBusiestPhysicalEngineAcrossProcesses()
    {
        var sample = PdhGpuEngineSource.Aggregate([
            Engine(1, 0, 0, 40), Engine(2, 0, 0, 20), Engine(1, 0, 0, 30),
            Engine(1, 0, 1, 80), Engine(1, 1, 0, 10)]);
        Assert.AreEqual(80d, sample.LoadPercent);
        Assert.AreEqual(3, sample.EngineCount);
        Assert.AreEqual(0d, PdhGpuEngineSource.Aggregate([Engine(1, 0, 0, 0)]).LoadPercent);
        Assert.ThrowsExactly<NotSupportedException>(() => PdhGpuEngineSource.Aggregate([]));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            PdhGpuEngineSource.Aggregate([new("unknown-instance", 0, 1)]));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(-1d)]
    [DataRow(101d)]
    public void InvalidGpuValuesRemainMissing(double value) => Assert.ThrowsExactly<InvalidDataException>(() =>
        PdhGpuEngineSource.Aggregate([Engine(1, 0, 0, value)]));

    [TestMethod]
    public async Task FailedWorkerStartsConsumeBudgetAndWindowAllowsLaterRecovery()
    {
        var time = new ManualTimeProvider(Start);
        var factory = new FailedStartsFactory();
        await using var client = new HardwareWorkerClient(HardwareWorkerOptions.Default with
            { MaxStartsPerWindow = 2 }, factory, time);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
                await client.CollectAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<WorkerRestartLimitException>(async () =>
            await client.CollectAsync(CancellationToken.None));
        Assert.AreEqual(2, factory.Calls);
        time.Advance(TimeSpan.FromSeconds(61)); factory.Recover = true;
        await client.CollectAsync(CancellationToken.None);
        Assert.AreEqual(3, factory.Calls);
    }

    [TestMethod]
    public async Task GlobalWorkerOpenFailureReleasesSessionForBoundedNextCycleRetry()
    {
        var failed = new Session((request, sequence) => Response(request, sequence) with
        { Status = WorkerStatuses.PermissionDenied, Errors = [new(WorkerErrorCodes.AccessDenied, null)] });
        var recovered = new Session(Response);
        var factory = new Sessions(failed, recovered);
        await using var client = new HardwareWorkerClient(HardwareWorkerOptions.Default, factory, new ManualTimeProvider(Start));
        var first = await client.CollectAsync(CancellationToken.None);
        Assert.AreEqual(WorkerStatuses.PermissionDenied, first.Status);
        Assert.IsTrue(failed.Aborted && failed.Disposed);
        await client.CollectAsync(CancellationToken.None);
        Assert.AreEqual(2, factory.Calls);
    }

    [TestMethod]
    public async Task UnsupportedHardwareDoesNotRestartWorkerAndOpenFailureDoesNotInventCounts()
    {
        var session = new Session(Response); var factory = new Sessions(session);
        await using (var client = new HardwareWorkerClient(HardwareWorkerOptions.Default, factory))
        {
            await client.CollectAsync(CancellationToken.None);
            await client.CollectAsync(CancellationToken.None);
            Assert.AreEqual(1, factory.Calls);
            Assert.IsFalse(session.Aborted);
        }
        var response = Response(new("1.0", new string('0', 32), "collect"), 1) with
        { Status = WorkerStatuses.Error, Errors = [new(WorkerErrorCodes.ProviderFailure, null)] };
        var providers = HardwareWorkerProviderFactory.Create(new WorkerClient(response));
        try
        {
            var time = new ManualTimeProvider(Start);
            var gpu = await providers[0].CollectAsync(Context(time), CancellationToken.None);
            var temperature = await providers[1].CollectAsync(Context(time), CancellationToken.None);
            Assert.IsNull(Metric(gpu, MetricIds.GpuDeviceCount));
            Assert.IsNull(Metric(temperature, MetricIds.HardwareTemperatureSensorCount));
            Assert.AreEqual("worker_open_failed", gpu.CollectionState);
            Assert.AreEqual("worker_open_failed", temperature.CollectionState);
        }
        finally { foreach (var provider in providers) await ((IAsyncDisposable)provider).DisposeAsync(); }
    }

    private static GpuEngineReading Engine(int pid, int adapter, int engine, double value) =>
        new($"pid_{pid}_luid_0x00000000_0x0000000{adapter}_phys_0_eng_{engine}_engtype_3D", 0, value);
    private static ProviderContext Context(ManualTimeProvider time) => new(time, time.GetUtcNow(), time.GetTimestamp(), 8);
    private static double? Metric(ProviderResult result, string id)
    {
        if (result.Data?["metrics"]?[id]?["value"] is not JsonValue value) return null;
        return value.TryGetValue<double>(out var number) ? number :
            value.TryGetValue<long>(out var integer) ? integer : null;
    }
    private static ProviderResult GpuReading(DateTimeOffset observed, double? load, double? temperature) =>
        new(GroupIds.Gpu, ProviderIds.Gpu, observed, AvailabilityStates.Available, ProviderCoverage.Complete, [],
            new JsonObject { ["metrics"] = new JsonObject
            { [MetricIds.GpuLoadMaxPercent] = MetricJson.Value(load, Units.Percent, SourceIds.LibreHardwareMonitor),
              [MetricIds.GpuTemperatureMaxCelsius] = MetricJson.Value(temperature, Units.Celsius, SourceIds.LibreHardwareMonitor) } });
    private static WorkerResponse Response(WorkerRequest request, long sequence) => new("1.0", request.RequestId,
        new string('0', 32), sequence, Start, WorkerStatuses.NotSupported, new(0, 0, 0), [], []);

    private sealed class PowerSource(Func<PowerStatusRead> read) : IPowerStatusSource
    { public PowerStatusRead Read() => read(); }
    private sealed class BatterySource(Func<BatteryStateRead> read) : IBatteryStateSource
    { public int Calls { get; private set; } public BatteryStateRead Read() { Calls++; return read(); } }
    private sealed class EngineSource(Func<GpuEngineSample> read) : IGpuEngineSource
    { public int Calls { get; private set; } public GpuEngineSample Collect() { Calls++; return read(); } public void Dispose() { } }
    private sealed class FailedStartsFactory : IWorkerSessionFactory
    {
        public int Calls { get; private set; } public bool Recover { get; set; }
        public ValueTask<IWorkerSession> StartAsync(CancellationToken cancellationToken)
        { Calls++; if (!Recover) throw new FileNotFoundException(); return ValueTask.FromResult<IWorkerSession>(new Session(Response)); }
    }
    private sealed class Sessions(params IWorkerSession[] sessions) : IWorkerSessionFactory
    {
        private readonly Queue<IWorkerSession> _sessions = new(sessions); public int Calls { get; private set; }
        public ValueTask<IWorkerSession> StartAsync(CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(_sessions.Dequeue()); }
    }
    private sealed class Session(Func<WorkerRequest, long, WorkerResponse> response) : IWorkerSession
    {
        private long _sequence;
        public bool HasExited => false; public long PrivateMemoryBytes => 1;
        public bool Aborted { get; private set; } public bool Disposed { get; private set; }
        public ValueTask<WorkerResponse> ExchangeAsync(WorkerRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(response(request, ++_sequence));
        public void Abort() => Aborted = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class WorkerClient(WorkerResponse response) : IHardwareWorkerClient
    {
        public ValueTask<WorkerResponse> CollectAsync(CancellationToken cancellationToken) => ValueTask.FromResult(response);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
