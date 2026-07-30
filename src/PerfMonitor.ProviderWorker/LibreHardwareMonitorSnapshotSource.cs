using System.ComponentModel;
using System.Text;
using LibreHardwareMonitor.Hardware;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.ProviderWorker;

internal interface IHardwareSnapshotSource
{
    WorkerResponse Collect(
        string requestId,
        string workerInstanceId,
        long sequence,
        DateTimeOffset observedAtUtc);
}

internal sealed class LibreHardwareMonitorSnapshotSource :
    IHardwareSnapshotSource,
    IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
    };
    private readonly Exception? _openFailure;
    private bool _opened;

    public LibreHardwareMonitorSnapshotSource()
    {
        try
        {
            _computer.Open();
            _opened = true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            Win32Exception or
            NotSupportedException or
            InvalidOperationException or
            IOException)
        {
            _openFailure = exception;
        }
    }

    public WorkerResponse Collect(
        string requestId,
        string workerInstanceId,
        long sequence,
        DateTimeOffset observedAtUtc)
    {
        if (_openFailure is not null)
        {
            var errorCode = Classify(_openFailure);
            return new WorkerResponse(
                ProviderWorkerProtocol.CurrentVersion,
                requestId,
                workerInstanceId,
                sequence,
                observedAtUtc,
                errorCode == WorkerErrorCodes.AccessDenied
                    ? WorkerStatuses.PermissionDenied
                    : WorkerStatuses.Error,
                new WorkerCoverage(0, 0, 0),
                [],
                [new WorkerError(errorCode, null)]);
        }

        var state = new CollectionState();
        foreach (var hardware in _computer.Hardware)
        {
            CollectHardware(hardware, state);
        }

        var status = Status(state);
        return new WorkerResponse(
            ProviderWorkerProtocol.CurrentVersion,
            requestId,
            workerInstanceId,
            sequence,
            observedAtUtc,
            status,
            new WorkerCoverage(
                state.Enumerated,
                state.Readable,
                state.Skipped),
            state.Devices,
            state.Errors);
    }

    public void Dispose()
    {
        if (!_opened)
        {
            return;
        }

        _opened = false;
        try
        {
            _computer.Close();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException)
        {
        }
    }

    private static void CollectHardware(
        IHardware hardware,
        CollectionState state)
    {
        if (state.Enumerated >=
            ProviderWorkerProtocol.MaxCoverageCount)
        {
            state.AddError(
                WorkerErrorCodes.ResourceExhausted,
                null);
            return;
        }

        var hardwareType = Identifier(
            hardware.HardwareType.ToString());
        var typeIndex = state.NextHardwareIndex(hardwareType);
        var deviceId = $"{hardwareType}-{typeIndex}";
        state.Enumerated++;
        try
        {
            hardware.Update();
            var sensors = ReadSensors(
                hardware,
                deviceId,
                state);
            if (state.Devices.Count <
                ProviderWorkerProtocol.MaxDevices)
            {
                state.Devices.Add(
                    new WorkerHardwareDevice(
                        deviceId,
                        DisplayName(
                            hardware.Name,
                            hardwareType),
                        hardwareType,
                        sensors));
                if (sensors.Count > 0)
                {
                    state.Readable++;
                }
                else
                {
                    state.Skipped++;
                }
            }
            else
            {
                state.Skipped++;
                state.AddError(
                    WorkerErrorCodes.ResourceExhausted,
                    deviceId);
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            Win32Exception or
            NotSupportedException or
            InvalidOperationException or
            IOException)
        {
            state.Skipped++;
            state.AddError(Classify(exception), deviceId);
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            CollectHardware(subHardware, state);
        }
    }

    private static IReadOnlyList<WorkerSensorReading>
        ReadSensors(
            IHardware hardware,
            string deviceId,
            CollectionState state)
    {
        var sensors = new List<WorkerSensorReading>();
        var indexes = new Dictionary<string, int>(
            StringComparer.Ordinal);
        foreach (var sensor in hardware.Sensors)
        {
            var sensorType = sensor.SensorType.ToString()
                .ToLowerInvariant();
            if (sensorType is not
                WorkerSensorTypes.Load and not
                WorkerSensorTypes.Temperature ||
                sensor.Value is not float rawValue)
            {
                continue;
            }

            var value = (double)rawValue;
            if (!IsPhysical(sensorType, value))
            {
                state.AddError(
                    WorkerErrorCodes.InvalidData,
                    deviceId);
                continue;
            }

            indexes.TryGetValue(sensorType, out var index);
            indexes[sensorType] = index + 1;
            if (sensors.Count >=
                ProviderWorkerProtocol.MaxSensorsPerDevice)
            {
                state.AddError(
                    WorkerErrorCodes.ResourceExhausted,
                    deviceId);
                break;
            }

            sensors.Add(
                new WorkerSensorReading(
                    $"{sensorType}-{index}",
                    DisplayName(sensor.Name, sensorType),
                    sensorType,
                    value));
        }

        return sensors;
    }

    private static bool IsPhysical(
        string sensorType,
        double value) =>
        double.IsFinite(value) &&
        sensorType switch
        {
            WorkerSensorTypes.Load =>
                value is >= 0 and <= 100,
            WorkerSensorTypes.Temperature =>
                value is >= -100 and <= 250,
            _ => false,
        };

    private static string Status(CollectionState state)
    {
        if (state.Readable > 0)
        {
            return state.Errors.Count == 0 &&
                state.Skipped == 0
                ? WorkerStatuses.Available
                : WorkerStatuses.Partial;
        }
        if (state.Errors.Any(static error =>
                error.ErrorCode ==
                    WorkerErrorCodes.AccessDenied))
        {
            return WorkerStatuses.PermissionDenied;
        }

        return state.Errors.Count == 0
            ? WorkerStatuses.NotSupported
            : WorkerStatuses.Error;
    }

    private static string Classify(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                WorkerErrorCodes.AccessDenied,
            Win32Exception { NativeErrorCode: 5 } =>
                WorkerErrorCodes.AccessDenied,
            NotSupportedException =>
                WorkerErrorCodes.NotSupported,
            OutOfMemoryException =>
                WorkerErrorCodes.ResourceExhausted,
            InvalidDataException or FormatException =>
                WorkerErrorCodes.InvalidData,
            _ => WorkerErrorCodes.ProviderFailure,
        };

    private static string Identifier(string value)
    {
        var builder = new StringBuilder(
            ProviderWorkerProtocol.MaxIdentifierLength);
        foreach (var character in value)
        {
            if (builder.Length >=
                ProviderWorkerProtocol.MaxIdentifierLength)
            {
                break;
            }

            if (char.IsUpper(character) &&
                builder.Length > 0 &&
                builder[^1] != '-')
            {
                builder.Append('-');
            }

            var normalized = char.ToLowerInvariant(character);
            if (normalized is >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '.' or
                '_' or
                '-')
            {
                builder.Append(normalized);
            }
        }

        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "unknown" : result;
    }

    private static string DisplayName(
        string? value,
        string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value;
        var builder = new StringBuilder(
            Math.Min(
                source.Length,
                ProviderWorkerProtocol.MaxDisplayNameLength));
        foreach (var character in source)
        {
            if (builder.Length >=
                ProviderWorkerProtocol.MaxDisplayNameLength)
            {
                break;
            }

            builder.Append(
                char.IsControl(character) ? ' ' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? fallback : result;
    }

    private sealed class CollectionState
    {
        private readonly Dictionary<string, int>
            _hardwareIndexes = new(StringComparer.Ordinal);

        public int Enumerated { get; set; }

        public int Readable { get; set; }

        public int Skipped { get; set; }

        public List<WorkerHardwareDevice> Devices { get; } = [];

        public List<WorkerError> Errors { get; } = [];

        public int NextHardwareIndex(string hardwareType)
        {
            _hardwareIndexes.TryGetValue(
                hardwareType,
                out var index);
            _hardwareIndexes[hardwareType] = index + 1;
            return index;
        }

        public void AddError(
            string errorCode,
            string? deviceId)
        {
            if (Errors.Count <
                ProviderWorkerProtocol.MaxErrors)
            {
                Errors.Add(new WorkerError(
                    errorCode,
                    deviceId));
            }
        }
    }
}
