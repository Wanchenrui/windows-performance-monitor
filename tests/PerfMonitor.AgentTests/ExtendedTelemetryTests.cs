using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Collectors.Windows;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class ExtendedTelemetryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NetworkRateUsesPerInterfaceDeltaAndMonotonicTime()
    {
        var source = new QueueNetworkSource(
            [
                new NetworkCounterRead("a", 1_000, 2_000, null),
                new NetworkCounterRead("b", 500, 500, null),
            ],
            [
                new NetworkCounterRead("a", 1_300, 2_100, null),
                new NetworkCounterRead("b", 600, 700, null),
            ]);
        var provider = new NetworkThroughputProvider(source);
        var time = new ManualTimeProvider(Start);

        var first = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);
        Assert.IsNull(ReadMetric(
            first,
            MetricIds.NetworkReceiveBytesPerSecond));
        Assert.IsFalse(ReadBoolean(first, "sampleReady"));

        time.Advance(TimeSpan.FromSeconds(2));
        var second = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);

        Assert.AreEqual(
            200d,
            ReadMetric(
                second,
                MetricIds.NetworkReceiveBytesPerSecond)!.Value,
            0.0001);
        Assert.AreEqual(
            150d,
            ReadMetric(
                second,
                MetricIds.NetworkSendBytesPerSecond)!.Value,
            0.0001);
        Assert.AreEqual(
            2d,
            ReadMetric(
                second,
                MetricIds.NetworkActiveInterfaceCount)!.Value);
        Assert.IsTrue(ReadBoolean(second, "sampleReady"));
    }

    [TestMethod]
    public async Task NetworkCounterResetDoesNotCreateFalseSpike()
    {
        var source = new QueueNetworkSource(
            [new NetworkCounterRead("a", 1_000, 2_000, null)],
            [new NetworkCounterRead("a", 10, 20, null)]);
        var provider = new NetworkThroughputProvider(source);
        var time = new ManualTimeProvider(Start);
        _ = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);

        Assert.IsNull(ReadMetric(
            result,
            MetricIds.NetworkReceiveBytesPerSecond));
        Assert.IsFalse(ReadBoolean(result, "sampleReady"));
        Assert.IsTrue(result.Errors.Any(
            error => error.ErrorCode ==
                StableErrorCodes.InvalidData));
    }

    [TestMethod]
    public async Task DiskIoFirstSampleIsExplicitlyNotReady()
    {
        var source = new QueueDiskIoSource(
            new DiskIoCounterSample(
                false,
                null,
                null,
                null,
                null),
            new DiskIoCounterSample(
                true,
                1_024,
                2_048,
                3,
                4));
        using var provider = new DiskIoProvider(source);
        var time = new ManualTimeProvider(Start);

        var first = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);
        Assert.IsFalse(ReadBoolean(first, "sampleReady"));
        Assert.IsNull(ReadMetric(
            first,
            MetricIds.DiskReadBytesPerSecond));

        time.Advance(TimeSpan.FromSeconds(1));
        var second = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);
        Assert.IsTrue(ReadBoolean(second, "sampleReady"));
        Assert.AreEqual(
            1_024d,
            ReadMetric(
                second,
                MetricIds.DiskReadBytesPerSecond)!.Value);
        Assert.AreEqual(
            4d,
            ReadMetric(
                second,
                MetricIds.DiskWriteOperationsPerSecond)!.Value);
    }

    [TestMethod]
    public async Task PowerStatusRepresentsNoBatteryWithoutFakeZero()
    {
        var provider = new PowerStatusProvider(
            new StubPowerSource(
                new PowerStatusRead(
                    1,
                    128,
                    byte.MaxValue,
                    0,
                    uint.MaxValue,
                    uint.MaxValue)));
        var time = new ManualTimeProvider(Start);

        var result = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);

        Assert.AreEqual("ac", ReadString(result, "powerSource"));
        Assert.IsFalse(ReadNullableBoolean(
            result,
            "batteryPresent"));
        Assert.AreEqual(
            "not_present",
            ReadString(result, "chargeStatus"));
        Assert.IsNull(ReadMetric(
            result,
            MetricIds.BatteryChargePercent));
        Assert.IsNull(ReadMetric(
            result,
            MetricIds.BatteryLifeRemainingSeconds));
    }

    [TestMethod]
    public async Task PowerStatusPublishesKnownBatteryPhysics()
    {
        var provider = new PowerStatusProvider(
            new StubPowerSource(
                new PowerStatusRead(
                    0,
                    8,
                    73,
                    1,
                    3_600,
                    7_200)));
        var time = new ManualTimeProvider(Start);

        var result = await provider.CollectAsync(
            Context(time),
            CancellationToken.None);

        Assert.AreEqual(
            "battery",
            ReadString(result, "powerSource"));
        Assert.IsTrue(ReadNullableBoolean(
            result,
            "batteryPresent"));
        Assert.IsTrue(ReadNullableBoolean(result, "charging"));
        Assert.IsTrue(ReadNullableBoolean(
            result,
            "batterySaver"));
        Assert.AreEqual(
            73d,
            ReadMetric(
                result,
                MetricIds.BatteryChargePercent)!.Value);
        Assert.AreEqual(
            3_600d,
            ReadMetric(
                result,
                MetricIds.BatteryLifeRemainingSeconds)!.Value);
    }

    [TestMethod]
    public async Task SchedulerDisposesStatefulProvider()
    {
        var provider = new DisposableProvider();
        await using (var scheduler = new ProviderScheduler(
            [provider],
            new CaptureSink(),
            1))
        {
        }

        Assert.IsTrue(provider.Disposed);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task SchedulerDefersDisposeWhileCollectionUsesState()
    {
        var provider = new BlockingDisposableProvider();
        var scheduler = new ProviderScheduler(
            [provider],
            new CaptureSink(),
            1);
        scheduler.Start();
        await provider.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        var dispose = scheduler.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        Assert.IsTrue(dispose.IsCompleted);
        Assert.IsFalse(provider.Disposed);

        provider.Release.TrySetResult();
        await provider.DisposedSignal.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.IsTrue(provider.Disposed);
    }

    private static ProviderContext Context(
        ManualTimeProvider time) =>
        new(
            time,
            time.GetUtcNow(),
            time.GetTimestamp(),
            8);

    private static JsonObject Data(ProviderResult result) =>
        result.Data as JsonObject ??
        throw new AssertFailedException("Missing object data.");

    private static double? ReadMetric(
        ProviderResult result,
        string metricId)
    {
        var node = Data(result)["metrics"]?[metricId]?["value"];
        if (node is null)
        {
            return null;
        }
        if (node is JsonValue value &&
            value.TryGetValue<double>(out var number))
        {
            return number;
        }
        if (node is JsonValue integerValue &&
            integerValue.TryGetValue<long>(out var integer))
        {
            return integer;
        }
        if (node is JsonValue intValue &&
            intValue.TryGetValue<int>(out var smallInteger))
        {
            return smallInteger;
        }

        throw new AssertFailedException(
            $"Metric {metricId} is not numeric.");
    }

    private static bool ReadBoolean(
        ProviderResult result,
        string propertyName) =>
        Data(result)[propertyName]?.GetValue<bool>() ??
        throw new AssertFailedException(
            $"Missing boolean {propertyName}.");

    private static bool ReadNullableBoolean(
        ProviderResult result,
        string propertyName) =>
        Data(result)[propertyName]?.GetValue<bool>() ??
        throw new AssertFailedException(
            $"Missing boolean {propertyName}.");

    private static string ReadString(
        ProviderResult result,
        string propertyName) =>
        Data(result)[propertyName]?.GetValue<string>() ??
        throw new AssertFailedException(
            $"Missing string {propertyName}.");

    private sealed class QueueNetworkSource : INetworkCounterSource
    {
        private readonly Queue<IReadOnlyList<NetworkCounterRead>>
            _reads;

        public QueueNetworkSource(
            params IReadOnlyList<NetworkCounterRead>[] reads)
        {
            _reads = new Queue<IReadOnlyList<NetworkCounterRead>>(
                reads);
        }

        public IReadOnlyList<NetworkCounterRead> Read() =>
            _reads.Dequeue();
    }

    private sealed class QueueDiskIoSource : IDiskIoCounterSource
    {
        private readonly Queue<DiskIoCounterSample> _samples;

        public QueueDiskIoSource(
            params DiskIoCounterSample[] samples)
        {
            _samples = new Queue<DiskIoCounterSample>(samples);
        }

        public DiskIoCounterSample Collect() =>
            _samples.Dequeue();

        public void Dispose()
        {
        }
    }

    private sealed class StubPowerSource : IPowerStatusSource
    {
        private readonly PowerStatusRead _status;

        public StubPowerSource(PowerStatusRead status)
        {
            _status = status;
        }

        public PowerStatusRead Read() => _status;
    }

    private sealed class DisposableProvider :
        IMetricProvider,
        IDisposable
    {
        public ProviderDescriptor Descriptor { get; } = new(
            "disposable",
            "test.disposable.v1",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            "user",
            "low");

        public bool Disposed { get; private set; }

        public ValueTask<ProviderResult> CollectAsync(
            ProviderContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                DelegateProvider.Available(
                    Descriptor,
                    context.UtcNow));

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class BlockingDisposableProvider :
        IMetricProvider,
        IDisposable
    {
        public ProviderDescriptor Descriptor { get; } = new(
            "blocking-disposable",
            "test.blocking-disposable.v1",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(50),
            "user",
            "low");

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public async ValueTask<ProviderResult> CollectAsync(
            ProviderContext context,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return DelegateProvider.Available(
                Descriptor,
                context.UtcNow);
        }

        public void Dispose()
        {
            Disposed = true;
            DisposedSignal.TrySetResult();
        }
    }
}
