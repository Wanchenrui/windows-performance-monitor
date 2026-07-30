namespace PerfMonitor.ProviderWorker.Protocol;

public static class ProviderWorkerProtocol
{
    public const string CurrentVersion = "1.0";
    public const string CollectOperation = "collect";
    public const int MaxMessageBytes = 1_048_576;
    public const int MaxDevices = 128;
    public const int MaxSensorsPerDevice = 128;
    public const int MaxErrors = 256;
    public const int MaxCoverageCount = 4_096;
    public const int MaxIdentifierLength = 64;
    public const int MaxDisplayNameLength = 128;
}

public static class WorkerStatuses
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string NotSupported = "not_supported";
    public const string PermissionDenied = "permission_denied";
    public const string Error = "error";

    public static bool IsKnown(string status) =>
        status is
            Available or
            Partial or
            NotSupported or
            PermissionDenied or
            Error;
}

public static class WorkerSensorTypes
{
    public const string Load = "load";
    public const string Temperature = "temperature";

    public static bool IsKnown(string sensorType) =>
        sensorType is Load or Temperature;
}

public static class WorkerErrorCodes
{
    public const string AccessDenied = "access_denied";
    public const string NotSupported = "not_supported";
    public const string Timeout = "timeout";
    public const string InvalidData = "invalid_data";
    public const string ResourceExhausted = "resource_exhausted";
    public const string ProviderFailure = "provider_failure";

    public static bool IsKnown(string errorCode) =>
        errorCode is
            AccessDenied or
            NotSupported or
            Timeout or
            InvalidData or
            ResourceExhausted or
            ProviderFailure;
}

public sealed record WorkerRequest(
    string ProtocolVersion,
    string RequestId,
    string Operation);

public sealed record WorkerResponse(
    string ProtocolVersion,
    string RequestId,
    string WorkerInstanceId,
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    string Status,
    WorkerCoverage Coverage,
    IReadOnlyList<WorkerHardwareDevice> Devices,
    IReadOnlyList<WorkerError> Errors);

public sealed record WorkerCoverage(
    int Enumerated,
    int Readable,
    int Skipped);

public sealed record WorkerHardwareDevice(
    string DeviceId,
    string DisplayName,
    string HardwareType,
    IReadOnlyList<WorkerSensorReading> Sensors);

public sealed record WorkerSensorReading(
    string SensorId,
    string DisplayName,
    string SensorType,
    double Value);

public sealed record WorkerError(
    string ErrorCode,
    string? DeviceId);
