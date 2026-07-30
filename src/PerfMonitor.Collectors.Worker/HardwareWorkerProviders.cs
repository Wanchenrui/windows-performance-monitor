using System.ComponentModel;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal abstract class HardwareWorkerProviderBase :
    IMetricProvider,
    IAsyncDisposable
{
    private readonly HardwareWorkerCoordinator _coordinator;
    private int _disposed;

    protected HardwareWorkerProviderBase(
        HardwareWorkerCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public abstract ProviderDescriptor Descriptor { get; }

    public async ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _coordinator.CollectAsync(
                context,
                cancellationToken).ConfigureAwait(false);
            return MapResponse(response, context.UtcNow);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return ProviderResult.Timeout(
                Descriptor,
                context.UtcNow);
        }
        catch (FileNotFoundException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.NotSupported,
                StableErrorCodes.NotSupported);
        }
        catch (UnauthorizedAccessException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.PermissionDenied,
                StableErrorCodes.AccessDenied);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode == 5)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.PermissionDenied,
                StableErrorCodes.AccessDenied);
        }
        catch (WorkerRestartLimitException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.Unavailable,
                StableErrorCodes.ResourceExhausted);
        }
        catch (WorkerResourceLimitException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.Unavailable,
                StableErrorCodes.ResourceExhausted);
        }
        catch (InvalidDataException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.Error,
                StableErrorCodes.InvalidData);
        }
        catch (WorkerCommunicationException)
        {
            return ProviderResult.Failure(
                Descriptor,
                context.UtcNow,
                AvailabilityStates.Unavailable,
                StableErrorCodes.ProviderFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _coordinator.ReleaseAsync().ConfigureAwait(false);
    }

    protected abstract ProviderResult MapResponse(
        WorkerResponse response,
        DateTimeOffset observedAtUtc);

    protected static IReadOnlyList<ProviderError> MapErrors(
        IEnumerable<WorkerError> errors) =>
        errors
            .Select(static error => new ProviderError(
                error.ErrorCode,
                null))
            .Distinct()
            .OrderBy(
                static error => error.ErrorCode,
                StringComparer.Ordinal)
            .ToArray();

    protected static IReadOnlyDictionary<string, int>
        ErrorReasons(IEnumerable<WorkerError> errors)
    {
        var reasons = errors
            .GroupBy(
                static error => error.ErrorCode,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
        return MetricJson.ReadOnlyReasons(reasons);
    }

    protected static string NoDataAvailability(
        IReadOnlyCollection<WorkerError> errors) =>
        errors.Any(static error =>
            error.ErrorCode == WorkerErrorCodes.AccessDenied)
            ? AvailabilityStates.PermissionDenied
            : errors.Any(static error =>
                error.ErrorCode != WorkerErrorCodes.NotSupported)
                ? AvailabilityStates.Error
                : AvailabilityStates.NotSupported;

    protected static string DataAvailability(
        int skipped,
        int errorCount) =>
        skipped > 0 ||
        errorCount > 0
            ? AvailabilityStates.Partial
            : AvailabilityStates.Available;

    protected static IReadOnlyList<ProviderError>
        EnsureNoDataError(
            IReadOnlyList<ProviderError> errors,
            string availability)
    {
        if (errors.Count > 0)
        {
            return errors;
        }

        var code = availability switch
        {
            AvailabilityStates.PermissionDenied =>
                StableErrorCodes.AccessDenied,
            AvailabilityStates.NotSupported =>
                StableErrorCodes.NotSupported,
            _ => StableErrorCodes.ProviderFailure,
        };
        return [new ProviderError(code, null)];
    }
}

internal sealed class GpuWorkerProvider :
    HardwareWorkerProviderBase
{
    public GpuWorkerProvider(
        HardwareWorkerCoordinator coordinator)
        : base(coordinator)
    {
    }

    public override ProviderDescriptor Descriptor { get; } =
        new(
            GroupIds.Gpu,
            ProviderIds.Gpu,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            "user",
            "high");

    protected override ProviderResult MapResponse(
        WorkerResponse response,
        DateTimeOffset observedAtUtc)
    {
        var devices = response.Devices
            .Where(static device =>
                device.HardwareType.StartsWith(
                    "gpu-",
                    StringComparison.Ordinal))
            .OrderBy(
                static device => device.DeviceId,
                StringComparer.Ordinal)
            .ToArray();
        var deviceIds = devices
            .Select(static device => device.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        var workerErrors = response.Errors.Where(
            error => error.DeviceId is null ||
                deviceIds.Contains(error.DeviceId) ||
                error.DeviceId.StartsWith(
                    "gpu-",
                    StringComparison.Ordinal))
            .ToArray();
        var errors = MapErrors(workerErrors);
        var readable = devices.Count(static device =>
            device.Sensors.Any(static sensor =>
                sensor.SensorType is
                    WorkerSensorTypes.Load or
                    WorkerSensorTypes.Temperature));
        var failedDeviceCount = workerErrors
            .Where(static error => error.DeviceId is not null)
            .Select(static error => error.DeviceId!)
            .Where(deviceId => !deviceIds.Contains(deviceId))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var enumerated = checked(
            devices.Length + failedDeviceCount);
        var skipped = checked(enumerated - readable);
        if (readable == 0)
        {
            var availability = NoDataAvailability(
                workerErrors);
            return new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                observedAtUtc,
                availability,
                new ProviderCoverage(
                    "limited",
                    enumerated,
                    0,
                    skipped,
                    ErrorReasons(workerErrors)),
                EnsureNoDataError(errors, availability),
                new JsonObject
                {
                    ["workerInstanceId"] =
                        response.WorkerInstanceId,
                    ["workerSequence"] = response.Sequence,
                    ["metrics"] = new JsonObject
                    {
                        [MetricIds.GpuDeviceCount] =
                            MetricJson.Value(
                                enumerated,
                                Units.Count,
                                SourceIds.LibreHardwareMonitor),
                        [MetricIds.GpuLoadMaxPercent] =
                            MetricJson.Value(
                                (double?)null,
                                Units.Percent,
                                SourceIds.LibreHardwareMonitor),
                        [MetricIds.GpuTemperatureMaxCelsius] =
                            MetricJson.Value(
                                (double?)null,
                                Units.Celsius,
                                SourceIds.LibreHardwareMonitor),
                    },
                    ["devices"] = new JsonArray(),
                });
        }

        var loadValues = devices
            .SelectMany(static device => device.Sensors)
            .Where(static sensor =>
                sensor.SensorType == WorkerSensorTypes.Load)
            .Select(static sensor => sensor.Value)
            .ToArray();
        var temperatureValues = devices
            .SelectMany(static device => device.Sensors)
            .Where(static sensor =>
                sensor.SensorType ==
                    WorkerSensorTypes.Temperature)
            .Select(static sensor => sensor.Value)
            .ToArray();
        var deviceRows = new JsonArray();
        foreach (var device in devices)
        {
            deviceRows.Add(GpuDeviceJson(device));
        }

        var data = new JsonObject
        {
            ["workerInstanceId"] = response.WorkerInstanceId,
            ["workerSequence"] = response.Sequence,
            ["metrics"] = new JsonObject
            {
                [MetricIds.GpuDeviceCount] = MetricJson.Value(
                    enumerated,
                    Units.Count,
                    SourceIds.LibreHardwareMonitor),
                [MetricIds.GpuLoadMaxPercent] =
                    MetricJson.Value(
                        MaxOrNull(loadValues),
                        Units.Percent,
                        SourceIds.LibreHardwareMonitor),
                [MetricIds.GpuTemperatureMaxCelsius] =
                    MetricJson.Value(
                        MaxOrNull(temperatureValues),
                        Units.Celsius,
                        SourceIds.LibreHardwareMonitor),
            },
            ["devices"] = deviceRows,
        };
        return new ProviderResult(
            Descriptor.GroupId,
            Descriptor.ProviderId,
            observedAtUtc,
            DataAvailability(skipped, errors.Count),
            new ProviderCoverage(
                skipped == 0 && errors.Count == 0
                    ? "complete"
                    : "limited",
                enumerated,
                readable,
                skipped,
                ErrorReasons(workerErrors)),
            errors,
            data);
    }

    private static JsonObject GpuDeviceJson(
        WorkerHardwareDevice device)
    {
        var loads = device.Sensors
            .Where(static sensor =>
                sensor.SensorType == WorkerSensorTypes.Load)
            .Select(static sensor => sensor.Value)
            .ToArray();
        var temperatures = device.Sensors
            .Where(static sensor =>
                sensor.SensorType ==
                    WorkerSensorTypes.Temperature)
            .Select(static sensor => sensor.Value)
            .ToArray();
        return new JsonObject
        {
            ["deviceId"] = device.DeviceId,
            ["displayName"] = device.DisplayName,
            ["hardwareType"] = device.HardwareType,
            ["metrics"] = new JsonObject
            {
                [MetricIds.GpuDeviceLoadPercent] =
                    MetricJson.Value(
                        MaxOrNull(loads),
                        Units.Percent,
                        SourceIds.LibreHardwareMonitor),
                [MetricIds.GpuDeviceTemperatureCelsius] =
                    MetricJson.Value(
                        MaxOrNull(temperatures),
                        Units.Celsius,
                        SourceIds.LibreHardwareMonitor),
            },
        };
    }

    private static double? MaxOrNull(
        IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Max();
}

internal sealed class TemperatureWorkerProvider :
    HardwareWorkerProviderBase
{
    public TemperatureWorkerProvider(
        HardwareWorkerCoordinator coordinator)
        : base(coordinator)
    {
    }

    public override ProviderDescriptor Descriptor { get; } =
        new(
            GroupIds.Sensors,
            ProviderIds.Sensors,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            "user",
            "high");

    protected override ProviderResult MapResponse(
        WorkerResponse response,
        DateTimeOffset observedAtUtc)
    {
        var devices = response.Devices
            .Select(static device => new
            {
                Device = device,
                Temperatures = device.Sensors
                    .Where(static sensor =>
                        sensor.SensorType ==
                            WorkerSensorTypes.Temperature)
                    .OrderBy(
                        static sensor => sensor.SensorId,
                        StringComparer.Ordinal)
                    .ToArray(),
            })
            .Where(static row => row.Temperatures.Length > 0)
            .OrderBy(
                static row => row.Device.DeviceId,
                StringComparer.Ordinal)
            .ToArray();
        var workerErrors = response.Errors.ToArray();
        var errors = MapErrors(workerErrors);
        if (devices.Length == 0)
        {
            var availability = NoDataAvailability(
                workerErrors);
            return new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                observedAtUtc,
                availability,
                new ProviderCoverage(
                    "limited",
                    response.Coverage.Enumerated,
                    0,
                    response.Coverage.Enumerated,
                    ErrorReasons(workerErrors)),
                EnsureNoDataError(errors, availability),
                new JsonObject
                {
                    ["workerInstanceId"] =
                        response.WorkerInstanceId,
                    ["workerSequence"] = response.Sequence,
                    ["metrics"] = new JsonObject
                    {
                        [MetricIds.HardwareTemperatureSensorCount] =
                            MetricJson.Value(
                                0,
                                Units.Count,
                                SourceIds.LibreHardwareMonitor),
                        [MetricIds.HardwareTemperatureMaxCelsius] =
                            MetricJson.Value(
                                (double?)null,
                                Units.Celsius,
                                SourceIds.LibreHardwareMonitor),
                    },
                    ["devices"] = new JsonArray(),
                });
        }

        var sensorCount = devices.Sum(
            static row => row.Temperatures.Length);
        var deviceRows = new JsonArray();
        foreach (var row in devices)
        {
            var sensorRows = new JsonArray();
            foreach (var sensor in row.Temperatures)
            {
                sensorRows.Add(new JsonObject
                {
                    ["sensorId"] = sensor.SensorId,
                    ["displayName"] = sensor.DisplayName,
                    ["metrics"] = new JsonObject
                    {
                        [MetricIds.HardwareTemperatureCelsius] =
                            MetricJson.Value(
                                sensor.Value,
                                Units.Celsius,
                                SourceIds.LibreHardwareMonitor),
                    },
                });
            }

            deviceRows.Add(new JsonObject
            {
                ["deviceId"] = row.Device.DeviceId,
                ["displayName"] = row.Device.DisplayName,
                ["hardwareType"] = row.Device.HardwareType,
                ["sensors"] = sensorRows,
            });
        }

        var data = new JsonObject
        {
            ["workerInstanceId"] = response.WorkerInstanceId,
            ["workerSequence"] = response.Sequence,
            ["metrics"] = new JsonObject
            {
                [MetricIds.HardwareTemperatureSensorCount] =
                    MetricJson.Value(
                        sensorCount,
                        Units.Count,
                        SourceIds.LibreHardwareMonitor),
                [MetricIds.HardwareTemperatureMaxCelsius] =
                    MetricJson.Value(
                        devices
                            .SelectMany(
                                static row => row.Temperatures)
                            .Max(static sensor => sensor.Value),
                        Units.Celsius,
                        SourceIds.LibreHardwareMonitor),
            },
            ["devices"] = deviceRows,
        };
        var skipped = checked(
            response.Coverage.Enumerated -
                devices.Length);
        return new ProviderResult(
            Descriptor.GroupId,
            Descriptor.ProviderId,
            observedAtUtc,
            DataAvailability(skipped, errors.Count),
            new ProviderCoverage(
                skipped == 0 && errors.Count == 0
                    ? "complete"
                    : "limited",
                response.Coverage.Enumerated,
                devices.Length,
                skipped,
                ErrorReasons(workerErrors)),
            errors,
            data);
    }
}
