using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Collectors.Worker;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class HardwareWorkerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ProtocolRoundTripsOneBoundedFrame()
    {
        var request = new WorkerRequest(
            ProviderWorkerProtocol.CurrentVersion,
            "0123456789abcdef0123456789abcdef",
            ProviderWorkerProtocol.CollectOperation);
        await using var stream = new MemoryStream();

        await LengthPrefixedJson.WriteAsync(
            stream,
            request,
            CancellationToken.None);
        stream.Position = 0;
        var restored =
            await LengthPrefixedJson.ReadAsync<WorkerRequest>(
                stream,
                CancellationToken.None);

        Assert.AreEqual(request, restored);
    }

    [TestMethod]
    public async Task ProtocolRejectsOversizedFrameBeforeAllocation()
    {
        var prefix = BitConverter.GetBytes(
            ProviderWorkerProtocol.MaxMessageBytes + 1);
        await using var stream = new MemoryStream(prefix);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () =>
            {
                _ = await LengthPrefixedJson
                    .ReadAsync<WorkerResponse>(
                        stream,
                        CancellationToken.None);
            });
    }

    [TestMethod]
    public async Task ProtocolRejectsTruncatedFrame()
    {
        byte[] frame = [4, 0, 0, 0, (byte)'{'];
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
            async () =>
            {
                _ = await LengthPrefixedJson
                    .ReadAsync<WorkerResponse>(
                        stream,
                        CancellationToken.None);
            });
    }

    [TestMethod]
    public async Task ProtocolRejectsInvalidJson()
    {
        byte[] frame = [1, 0, 0, 0, 0xff];
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsExactlyAsync<
            System.Text.Json.JsonException>(
            async () =>
            {
                _ = await LengthPrefixedJson
                    .ReadAsync<WorkerResponse>(
                        stream,
                        CancellationToken.None);
            });
    }

    [TestMethod]
    public void ValidatorRejectsNonPhysicalSensorValue()
    {
        var response = Response(
            "0123456789abcdef0123456789abcdef",
            sequence: 1,
            [
                Device(
                    "gpu-nvidia-0",
                    "gpu-nvidia",
                    new WorkerSensorReading(
                        "load-0",
                        "GPU Core",
                        WorkerSensorTypes.Load,
                        101)),
            ]);

        Assert.ThrowsExactly<InvalidDataException>(
            () => WorkerResponseValidator.Validate(
                response,
                response.RequestId,
                null,
                0));
    }

    [TestMethod]
    public void ValidatorRejectsNonUnitInitialSequence()
    {
        var response = Response(
            "0123456789abcdef0123456789abcdef",
            sequence: 2,
            []);

        Assert.ThrowsExactly<InvalidDataException>(
            () => WorkerResponseValidator.Validate(
                response,
                response.RequestId,
                null,
                0));
    }

    [TestMethod]
    public void ValidatorRejectsUnclosedCoverage()
    {
        var response = Response(
            "0123456789abcdef0123456789abcdef",
            sequence: 1,
            []) with
        {
            Coverage = new WorkerCoverage(1, 0, 0),
        };

        Assert.ThrowsExactly<InvalidDataException>(
            () => WorkerResponseValidator.Validate(
                response,
                response.RequestId,
                null,
                0));
    }

    [TestMethod]
    public void ValidatorRejectsNonUtcTimestamp()
    {
        var response = Response(
            "0123456789abcdef0123456789abcdef",
            sequence: 1,
            []) with
        {
            ObservedAtUtc = Start.ToOffset(
                TimeSpan.FromHours(8)),
        };

        Assert.ThrowsExactly<InvalidDataException>(
            () => WorkerResponseValidator.Validate(
                response,
                response.RequestId,
                null,
                0));
    }

    [TestMethod]
    public async Task ProvidersReuseOneSnapshotAndUseMaxAggregation()
    {
        var response = Response(
            "ignored",
            sequence: 1,
            [
                Device(
                    "gpu-nvidia-0",
                    "gpu-nvidia",
                    new WorkerSensorReading(
                        "load-0",
                        "GPU Core",
                        WorkerSensorTypes.Load,
                        30),
                    new WorkerSensorReading(
                        "temperature-0",
                        "GPU Core",
                        WorkerSensorTypes.Temperature,
                        70)),
                Device(
                    "gpu-intel-0",
                    "gpu-intel",
                    new WorkerSensorReading(
                        "load-0",
                        "GPU Render",
                        WorkerSensorTypes.Load,
                        80),
                    new WorkerSensorReading(
                        "temperature-0",
                        "GPU Package",
                        WorkerSensorTypes.Temperature,
                        65)),
                Device(
                    "cpu-0",
                    "cpu",
                    new WorkerSensorReading(
                        "temperature-0",
                        "CPU Package",
                        WorkerSensorTypes.Temperature,
                        90)),
            ]);
        var client = new StubWorkerClient(response);
        var providers = HardwareWorkerProviderFactory.Create(
            client);
        var time = new ManualTimeProvider(Start);
        var context = Context(time);

        var gpu = await providers[0].CollectAsync(
            context,
            CancellationToken.None);
        var sensors = await providers[1].CollectAsync(
            context,
            CancellationToken.None);

        Assert.AreEqual(1, client.CollectionCount);
        Assert.AreEqual(
            80d,
            ReadMetric(gpu, MetricIds.GpuLoadMaxPercent));
        Assert.AreEqual(
            70d,
            ReadMetric(
                gpu,
                MetricIds.GpuTemperatureMaxCelsius));
        Assert.AreEqual(
            90d,
            ReadMetric(
                sensors,
                MetricIds.HardwareTemperatureMaxCelsius));
        Assert.AreEqual(
            AvailabilityStates.Available,
            gpu.Availability);
        Assert.AreEqual(
            AvailabilityStates.Available,
            sensors.Availability);

        await ((IAsyncDisposable)providers[0])
            .DisposeAsync();
        Assert.AreEqual(0, client.DisposeCount);
        await ((IAsyncDisposable)providers[1])
            .DisposeAsync();
        Assert.AreEqual(1, client.DisposeCount);
    }

    [TestMethod]
    public async Task ProvidersUseAgentTimeAndCloseGroupCoverage()
    {
        WorkerHardwareDevice[] devices =
        [
            Device("gpu-nvidia-0", "gpu-nvidia"),
            Device(
                "gpu-intel-0",
                "gpu-intel",
                new WorkerSensorReading(
                    "load-0",
                    "GPU Render",
                    WorkerSensorTypes.Load,
                    25)),
            Device(
                "cpu-0",
                "cpu",
                new WorkerSensorReading(
                    "temperature-0",
                    "CPU Package",
                    WorkerSensorTypes.Temperature,
                    55)),
        ];
        var response = Response(
            "ignored",
            sequence: 1,
            devices) with
        {
            ObservedAtUtc = Start.AddYears(-1),
            Status = WorkerStatuses.Partial,
            Coverage = new WorkerCoverage(3, 2, 1),
        };
        var client = new StubWorkerClient(response);
        var providers = HardwareWorkerProviderFactory.Create(
            client);
        var time = new ManualTimeProvider(
            Start.AddMinutes(5));
        var context = Context(time);

        var gpu = await providers[0].CollectAsync(
            context,
            CancellationToken.None);
        var sensors = await providers[1].CollectAsync(
            context,
            CancellationToken.None);

        Assert.AreEqual(context.UtcNow, gpu.ObservedAtUtc);
        Assert.AreEqual(context.UtcNow, sensors.ObservedAtUtc);
        Assert.AreEqual(
            2d,
            ReadMetric(gpu, MetricIds.GpuDeviceCount));
        Assert.AreEqual(2, gpu.Coverage.Enumerated);
        Assert.AreEqual(1, gpu.Coverage.Readable);
        Assert.AreEqual(1, gpu.Coverage.Skipped);
        Assert.AreEqual(
            AvailabilityStates.Partial,
            gpu.Availability);
        Assert.AreEqual(3, sensors.Coverage.Enumerated);
        Assert.AreEqual(1, sensors.Coverage.Readable);
        Assert.AreEqual(2, sensors.Coverage.Skipped);

        await ((IAsyncDisposable)providers[0])
            .DisposeAsync();
        await ((IAsyncDisposable)providers[1])
            .DisposeAsync();
    }

    [TestMethod]
    public async Task MissingWorkerOnlyMarksHardwareGroupUnsupported()
    {
        var client = new ThrowingWorkerClient(
            new FileNotFoundException());
        var providers = HardwareWorkerProviderFactory.Create(
            client);
        var time = new ManualTimeProvider(Start);

        var result = await providers[0].CollectAsync(
            Context(time),
            CancellationToken.None);

        Assert.AreEqual(
            AvailabilityStates.NotSupported,
            result.Availability);
        Assert.AreEqual(
            StableErrorCodes.NotSupported,
            result.Errors.Single().ErrorCode);
        await ((IAsyncDisposable)providers[0])
            .DisposeAsync();
        await ((IAsyncDisposable)providers[1])
            .DisposeAsync();
    }

    [TestMethod]
    public async Task ClientDiscardsCrashedSessionAndRestartsNextCycle()
    {
        var failed = new FakeWorkerSession(
            static (_, _) => ValueTask.FromException<WorkerResponse>(
                new EndOfStreamException()));
        var recovered = new FakeWorkerSession(
            static (request, _) => ValueTask.FromResult(
                Response(request.RequestId, 1, [])));
        var factory = new QueueSessionFactory(failed, recovered);
        var options = HardwareWorkerOptions.Default with
        {
            MaxStartsPerWindow = 3,
        };
        await using var client = new HardwareWorkerClient(
            options,
            factory);

        await Assert.ThrowsExactlyAsync<
            WorkerCommunicationException>(
            async () =>
            {
                _ = await client.CollectAsync(
                    CancellationToken.None);
            });
        var response = await client.CollectAsync(
            CancellationToken.None);

        Assert.AreEqual(2, factory.StartCount);
        Assert.IsTrue(failed.Aborted);
        Assert.IsTrue(failed.Disposed);
        Assert.AreEqual(1, response.Sequence);
    }

    [TestMethod]
    public async Task ClientKillsHungSessionOnCancellation()
    {
        var session = new FakeWorkerSession(
            static async (_, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new AssertFailedException(
                    "Unreachable after cancellation.");
            });
        var factory = new QueueSessionFactory(session);
        await using var client = new HardwareWorkerClient(
            HardwareWorkerOptions.Default,
            factory);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<
            OperationCanceledException>(
            async () =>
            {
                _ = await client.CollectAsync(
                    cancellation.Token);
            });

        Assert.IsTrue(session.Aborted);
        Assert.IsTrue(session.Disposed);
    }

    [TestMethod]
    public async Task ClientEnforcesRestartBudget()
    {
        static FakeWorkerSession Failed() =>
            new(static (_, _) =>
                ValueTask.FromException<WorkerResponse>(
                    new EndOfStreamException()));
        var factory = new QueueSessionFactory(
            Failed(),
            Failed(),
            Failed());
        var options = HardwareWorkerOptions.Default with
        {
            MaxStartsPerWindow = 2,
        };
        await using var client = new HardwareWorkerClient(
            options,
            factory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsExactlyAsync<
                WorkerCommunicationException>(
                async () =>
                {
                    _ = await client.CollectAsync(
                        CancellationToken.None);
                });
        }
        await Assert.ThrowsExactlyAsync<
            WorkerRestartLimitException>(
            async () =>
            {
                _ = await client.CollectAsync(
                    CancellationToken.None);
            });
        Assert.AreEqual(2, factory.StartCount);
    }

    [TestMethod]
    public async Task ClientEnforcesPrivateMemoryLimit()
    {
        var session = new FakeWorkerSession(
            static (request, _) => ValueTask.FromResult(
                Response(request.RequestId, 1, [])))
        {
            PrivateMemoryBytes = 1024,
        };
        var factory = new QueueSessionFactory(session);
        var options = HardwareWorkerOptions.Default with
        {
            MaxPrivateMemoryBytes = 512,
        };
        await using var client = new HardwareWorkerClient(
            options,
            factory);

        await Assert.ThrowsExactlyAsync<
            WorkerResourceLimitException>(
            async () =>
            {
                _ = await client.CollectAsync(
                    CancellationToken.None);
            });

        Assert.IsTrue(session.Aborted);
    }

    private static ProviderContext Context(
        ManualTimeProvider time) =>
        new(
            time,
            time.GetUtcNow(),
            time.GetTimestamp(),
            8);

    private static WorkerResponse Response(
        string requestId,
        long sequence,
        IReadOnlyList<WorkerHardwareDevice> devices) =>
        new(
            ProviderWorkerProtocol.CurrentVersion,
            requestId,
            "0123456789abcdef0123456789abcdef",
            sequence,
            Start,
            devices.Count == 0
                ? WorkerStatuses.NotSupported
                : WorkerStatuses.Available,
            new WorkerCoverage(
                devices.Count,
                devices.Count,
                0),
            devices,
            []);

    private static WorkerHardwareDevice Device(
        string id,
        string hardwareType,
        params WorkerSensorReading[] sensors) =>
        new(
            id,
            id,
            hardwareType,
            sensors);

    private static double ReadMetric(
        ProviderResult result,
        string metricId)
    {
        var value = result.Data?["metrics"]?[metricId]?["value"];
        if (value is JsonValue jsonValue &&
            jsonValue.TryGetValue<double>(out var number))
        {
            return number;
        }
        if (value is JsonValue integerValue &&
            integerValue.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        throw new AssertFailedException(
            $"Metric {metricId} is missing.");
    }

    private sealed class StubWorkerClient :
        IHardwareWorkerClient
    {
        private readonly WorkerResponse _response;

        public StubWorkerClient(WorkerResponse response)
        {
            _response = response;
        }

        public int CollectionCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<WorkerResponse> CollectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectionCount++;
            return ValueTask.FromResult(_response);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingWorkerClient :
        IHardwareWorkerClient
    {
        private readonly Exception _exception;

        public ThrowingWorkerClient(Exception exception)
        {
            _exception = exception;
        }

        public ValueTask<WorkerResponse> CollectAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromException<WorkerResponse>(
                _exception);

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

    private sealed class QueueSessionFactory :
        IWorkerSessionFactory
    {
        private readonly Queue<IWorkerSession> _sessions;

        public QueueSessionFactory(
            params IWorkerSession[] sessions)
        {
            _sessions = new Queue<IWorkerSession>(sessions);
        }

        public int StartCount { get; private set; }

        public ValueTask<IWorkerSession> StartAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return ValueTask.FromResult(_sessions.Dequeue());
        }
    }

    private sealed class FakeWorkerSession : IWorkerSession
    {
        private readonly Func<
            WorkerRequest,
            CancellationToken,
            ValueTask<WorkerResponse>> _exchange;

        public FakeWorkerSession(
            Func<
                WorkerRequest,
                CancellationToken,
                ValueTask<WorkerResponse>> exchange)
        {
            _exchange = exchange;
        }

        public bool HasExited { get; set; }

        public long PrivateMemoryBytes { get; set; }

        public bool Aborted { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<WorkerResponse> ExchangeAsync(
            WorkerRequest request,
            CancellationToken cancellationToken) =>
            _exchange(request, cancellationToken);

        public void Abort()
        {
            Aborted = true;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
