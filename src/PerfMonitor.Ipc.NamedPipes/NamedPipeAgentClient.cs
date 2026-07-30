using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Ipc.NamedPipes;

public sealed class IpcRemoteException : Exception
{
    public IpcRemoteException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class NamedPipeAgentClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _disposed;

    private NamedPipeAgentClient(
        NamedPipeClientStream pipe,
        int maxMessageSize,
        string instanceId,
        string productVersion,
        CapabilitiesContract capabilities)
    {
        _pipe = pipe;
        MaxMessageSize = maxMessageSize;
        InstanceId = instanceId;
        ProductVersion = productVersion;
        Capabilities = capabilities;
    }

    public int MaxMessageSize { get; }
    public string InstanceId { get; }
    public string ProductVersion { get; }
    public CapabilitiesContract Capabilities { get; }

    public static async Task<NamedPipeAgentClient> ConnectAsync(
        PipeEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var pipe = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            var timeoutMilliseconds = checked(
                (int)Math.Ceiling(timeout.TotalMilliseconds));
            await pipe.ConnectAsync(
                timeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);

            var requestId = Guid.NewGuid().ToString("N");
            await LengthPrefixedJson.WriteAsync(
                pipe,
                new IpcRequestMessage
                {
                    Type = "hello",
                    RequestId = requestId,
                    SupportedContractVersions = [ContractVersions.V1],
                    MaxMessageSize =
                        IpcProtocol.AbsoluteMaxMessageSize,
                },
                IpcProtocol.AbsoluteMaxMessageSize,
                cancellationToken).ConfigureAwait(false);

            var response = await ReadResponseAsync(
                pipe,
                requestId,
                IpcProtocol.AbsoluteMaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            if (response.Type != "helloAck" ||
                response.SelectedContractVersion != ContractVersions.V1 ||
                response.MaxMessageSize is null or <
                    IpcProtocol.MinimumNegotiatedMessageSize or >
                    IpcProtocol.AbsoluteMaxMessageSize ||
                string.IsNullOrWhiteSpace(response.ProductVersion) ||
                !Guid.TryParseExact(
                    response.InstanceId,
                    "N",
                    out _) ||
                response.Capabilities is null)
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }

            var capabilities = response.Capabilities.Value
                .Deserialize<CapabilitiesContract>(
                    IpcJson.Options) ??
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            return new NamedPipeAgentClient(
                pipe,
                response.MaxMessageSize.Value,
                response.InstanceId!,
                response.ProductVersion,
                capabilities);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<AgentSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken) =>
        RequestAsync<AgentSnapshot>(
            new IpcRequestMessage
            {
                Type = "getSnapshot",
                RequestId = Guid.NewGuid().ToString("N"),
            },
            "snapshot",
            cancellationToken);

    public Task<CapabilitiesContract> GetCapabilitiesAsync(
        CancellationToken cancellationToken) =>
        RequestAsync<CapabilitiesContract>(
            new IpcRequestMessage
            {
                Type = "getCapabilities",
                RequestId = Guid.NewGuid().ToString("N"),
            },
            "capabilities",
            cancellationToken);

    public Task<HealthContract> GetHealthAsync(
        CancellationToken cancellationToken) =>
        RequestAsync<HealthContract>(
            new IpcRequestMessage
            {
                Type = "getHealth",
                RequestId = Guid.NewGuid().ToString("N"),
            },
            "health",
            cancellationToken);

    public Task<HistoryContract> QueryHistoryAsync(
        HistoryQueryContract query,
        CancellationToken cancellationToken) =>
        RequestAsync<HistoryContract>(
            new IpcRequestMessage
            {
                Type = "queryHistory",
                RequestId = Guid.NewGuid().ToString("N"),
                MetricIds = query.MetricIds,
                FromEpochMs = query.FromEpochMs,
                ToEpochMs = query.ToEpochMs,
                MaxPoints = query.MaxPoints,
            },
            "history",
            cancellationToken);

    public Task<DiagnosticsContract> QueryDiagnosticsAsync(
        DiagnosticQueryContract query,
        CancellationToken cancellationToken) =>
        RequestAsync<DiagnosticsContract>(
            new IpcRequestMessage
            {
                Type = "queryDiagnostics",
                RequestId = Guid.NewGuid().ToString("N"),
                RuleIds = query.RuleIds,
                States = query.States,
                FromEpochMs = query.FromEpochMs,
                ToEpochMs = query.ToEpochMs,
                MaxEvents = query.MaxEvents,
            },
            "diagnostics",
            cancellationToken);

    public async IAsyncEnumerable<AgentSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            await LengthPrefixedJson.WriteAsync(
                _pipe,
                new IpcRequestMessage
                {
                    Type = "subscribe",
                    RequestId = requestId,
                },
                MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            var subscribed = await ReadResponseAsync(
                _pipe,
                requestId,
                MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            if (subscribed.Type != "subscribed" ||
                string.IsNullOrWhiteSpace(
                    subscribed.SubscriptionId))
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var response = await ReadResponseAsync(
                    _pipe,
                    requestId,
                    MaxMessageSize,
                    cancellationToken).ConfigureAwait(false);
                if (response.Type != "snapshotUpdate" ||
                    response.Payload is null)
                {
                    throw new IpcProtocolException(
                        IpcErrorCodes.InvalidRequest);
                }

                yield return response.Payload.Value
                    .Deserialize<AgentSnapshot>(
                        IpcJson.Options) ??
                    throw new IpcProtocolException(
                        IpcErrorCodes.InvalidRequest);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<T> RequestAsync<T>(
        IpcRequestMessage request,
        string expectedResponseType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await LengthPrefixedJson.WriteAsync(
                _pipe,
                request,
                MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(
                _pipe,
                request.RequestId,
                MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            if (response.Type != expectedResponseType ||
                response.Payload is null)
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }

            return response.Payload.Value.Deserialize<T>(
                IpcJson.Options) ??
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static async Task<IpcResponseMessage> ReadResponseAsync(
        Stream pipe,
        string expectedRequestId,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        using var document = await LengthPrefixedJson.ReadAsync(
            pipe,
            maxMessageSize,
            cancellationToken).ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "The Agent closed the IPC connection.");
        IpcResponseMessage response;
        try
        {
            response = document.Deserialize<IpcResponseMessage>(
                IpcJson.Options) ?? throw new JsonException(
                "IPC response is null.");
        }
        catch (JsonException)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.InvalidRequest);
        }

        if (!StringComparer.Ordinal.Equals(
            response.RequestId,
            expectedRequestId))
        {
            throw new IpcProtocolException(
                IpcErrorCodes.InvalidRequest);
        }

        if (response.Type == "error")
        {
            throw new IpcRemoteException(
                response.Error?.ErrorCode ??
                IpcErrorCodes.ServiceUnavailable);
        }

        return response;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
