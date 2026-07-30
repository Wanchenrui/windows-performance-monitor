using System.Text.Json;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.ProviderWorker;

public static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--stdio"])
        {
            await Console.Error.WriteLineAsync(
                "worker_usage_error").ConfigureAwait(false);
            return 2;
        }

        try
        {
            using var source =
                new LibreHardwareMonitorSnapshotSource();
            return await WorkerLoop.RunAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                source,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            JsonException)
        {
            await Console.Error.WriteLineAsync(
                $"worker_protocol_failure:" +
                exception.GetType().Name +
                $":{SafeDetail(exception.Message)}")
                .ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"worker_failure:" +
                exception.GetType().Name).ConfigureAwait(false);
            return 1;
        }
    }

    private static string SafeDetail(string detail)
    {
        var normalized = new string(
            detail
                .Select(static character =>
                    char.IsControl(character) ? ' ' : character)
                .ToArray());
        return normalized.Length <= 160
            ? normalized
            : normalized[..160];
    }
}

internal static class WorkerLoop
{
    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        IHardwareSnapshotSource source,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        long sequence = 0;
        while (true)
        {
            var frame = await LengthPrefixedJson
                .TryReadAsync<WorkerRequest>(
                    input,
                    cancellationToken).ConfigureAwait(false);
            if (!frame.HasValue)
            {
                return 0;
            }

            var request = frame.Value!;
            var response = IsValidRequest(request)
                ? source.Collect(
                    request.RequestId,
                    instanceId,
                    checked(++sequence),
                    timeProvider.GetUtcNow())
                : InvalidRequest(
                    request,
                    instanceId,
                    checked(++sequence),
                    timeProvider.GetUtcNow());
            await LengthPrefixedJson.WriteAsync(
                output,
                response,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsValidRequest(WorkerRequest request) =>
        request.ProtocolVersion ==
            ProviderWorkerProtocol.CurrentVersion &&
        request.Operation ==
            ProviderWorkerProtocol.CollectOperation &&
        request.RequestId is { Length: 32 } &&
        request.RequestId.All(static character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f');

    private static WorkerResponse InvalidRequest(
        WorkerRequest request,
        string instanceId,
        long sequence,
        DateTimeOffset observedAtUtc) =>
        new(
            ProviderWorkerProtocol.CurrentVersion,
            request.RequestId,
            instanceId,
            sequence,
            observedAtUtc,
            WorkerStatuses.Error,
            new WorkerCoverage(0, 0, 0),
            [],
            [new WorkerError(
                WorkerErrorCodes.InvalidData,
                null)]);
}
