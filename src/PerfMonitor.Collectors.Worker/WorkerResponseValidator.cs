using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal static class WorkerResponseValidator
{
    public static void Validate(
        WorkerResponse response,
        string expectedRequestId,
        string? previousWorkerInstanceId,
        long previousSequence)
    {
        if (response.ProtocolVersion !=
                ProviderWorkerProtocol.CurrentVersion ||
            response.RequestId != expectedRequestId ||
            !IsLowerHexIdentifier(response.WorkerInstanceId, 32) ||
            response.Sequence <= 0 ||
            response.ObservedAtUtc == default ||
            response.ObservedAtUtc.Offset != TimeSpan.Zero ||
            !WorkerStatuses.IsKnown(response.Status) ||
            response.Coverage is null ||
            response.Devices is null ||
            response.Errors is null)
        {
            throw Invalid();
        }

        if (previousWorkerInstanceId is not null &&
            response.WorkerInstanceId != previousWorkerInstanceId)
        {
            throw new InvalidDataException(
                "Worker instance changed within one session.");
        }
        if (previousWorkerInstanceId is null &&
            response.Sequence != 1)
        {
            throw new InvalidDataException(
                "The first Worker sequence must be one.");
        }
        if (previousWorkerInstanceId is not null &&
            response.Sequence <= previousSequence)
        {
            throw new InvalidDataException(
                "Worker sequence did not increase.");
        }

        ValidateCoverage(response.Coverage);
        if (response.Devices.Count >
            ProviderWorkerProtocol.MaxDevices)
        {
            throw Invalid();
        }

        var deviceIds = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var device in response.Devices)
        {
            if (device is null ||
                !IsIdentifier(device.DeviceId) ||
                !deviceIds.Add(device.DeviceId) ||
                !IsDisplayName(device.DisplayName) ||
                !IsIdentifier(device.HardwareType) ||
                device.Sensors is null ||
                device.Sensors.Count >
                    ProviderWorkerProtocol.MaxSensorsPerDevice)
            {
                throw Invalid();
            }

            ValidateSensors(device.Sensors);
        }

        if (response.Errors.Count >
            ProviderWorkerProtocol.MaxErrors)
        {
            throw Invalid();
        }
        foreach (var error in response.Errors)
        {
            if (error is null ||
                !WorkerErrorCodes.IsKnown(error.ErrorCode) ||
                error.DeviceId is not null &&
                !IsIdentifier(error.DeviceId))
            {
                throw Invalid();
            }
        }

        var readableDevices = response.Devices.Count(
            static device => device.Sensors.Count > 0);
        if (readableDevices != response.Coverage.Readable)
        {
            throw Invalid();
        }

        ValidateStatus(response);
    }

    private static void ValidateCoverage(WorkerCoverage coverage)
    {
        if (coverage.Enumerated < 0 ||
            coverage.Readable < 0 ||
            coverage.Skipped < 0 ||
            coverage.Enumerated >
                ProviderWorkerProtocol.MaxCoverageCount ||
            coverage.Readable > coverage.Enumerated ||
            coverage.Skipped > coverage.Enumerated ||
            (long)coverage.Readable + coverage.Skipped !=
                coverage.Enumerated)
        {
            throw Invalid();
        }
    }

    private static void ValidateStatus(WorkerResponse response)
    {
        var hasErrors = response.Errors.Count > 0;
        var hasSkipped = response.Coverage.Skipped > 0;
        var hasReadable = response.Coverage.Readable > 0;
        var valid = response.Status switch
        {
            WorkerStatuses.Available =>
                hasReadable && !hasSkipped && !hasErrors,
            WorkerStatuses.Partial =>
                hasReadable && (hasSkipped || hasErrors),
            WorkerStatuses.NotSupported =>
                !hasReadable && !hasErrors,
            WorkerStatuses.PermissionDenied =>
                !hasReadable &&
                response.Errors.Any(static error =>
                    error.ErrorCode ==
                        WorkerErrorCodes.AccessDenied),
            WorkerStatuses.Error =>
                !hasReadable && hasErrors,
            _ => false,
        };
        if (!valid)
        {
            throw Invalid();
        }
    }

    private static void ValidateSensors(
        IReadOnlyList<WorkerSensorReading> sensors)
    {
        var sensorIds = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
            if (sensor is null ||
                !IsIdentifier(sensor.SensorId) ||
                !sensorIds.Add(sensor.SensorId) ||
                !IsDisplayName(sensor.DisplayName) ||
                !WorkerSensorTypes.IsKnown(sensor.SensorType) ||
                !double.IsFinite(sensor.Value))
            {
                throw Invalid();
            }

            var inRange = sensor.SensorType switch
            {
                WorkerSensorTypes.Load =>
                    sensor.Value is >= 0 and <= 100,
                WorkerSensorTypes.Temperature =>
                    sensor.Value is >= -100 and <= 250,
                _ => false,
            };
            if (!inRange)
            {
                throw Invalid();
            }
        }
    }

    private static bool IsIdentifier(string? value) =>
        value is not null &&
        value.Length is > 0 and <=
            ProviderWorkerProtocol.MaxIdentifierLength &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '.' or
                '_' or
                '-');

    private static bool IsLowerHexIdentifier(
        string? value,
        int length) =>
        value is not null &&
        value.Length == length &&
        value.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f');

    private static bool IsDisplayName(string? value) =>
        value is not null &&
        value.Length is > 0 and <=
            ProviderWorkerProtocol.MaxDisplayNameLength &&
        value.All(static character => !char.IsControl(character));

    private static InvalidDataException Invalid() =>
        new("The hardware Worker response is invalid.");
}
