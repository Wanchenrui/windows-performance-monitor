using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using PerfMonitor.Contracts;

namespace PerfMonitor.Ipc.NamedPipes;

public sealed class IpcServiceUnavailableException : Exception
{
    public IpcServiceUnavailableException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class NamedPipeAgentServer : IAsyncDisposable
{
    private readonly PipeEndpoint _endpoint;
    private readonly IAgentIpcService _service;
    private readonly SnapshotSubscriptionHub _subscriptions;
    private readonly SemaphoreSlim _clientSlots = new(
        PipeEndpoint.MaxServerInstances,
        PipeEndpoint.MaxServerInstances);
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _runTask;
    private long _nextClientId;
    private int _started;

    public NamedPipeAgentServer(
        PipeEndpoint endpoint,
        IAgentIpcService service,
        SnapshotSubscriptionHub subscriptions)
    {
        _endpoint = endpoint;
        _service = service;
        _subscriptions = subscriptions;
    }

    public PipeEndpoint Endpoint => _endpoint;

    public Task Completion => _runTask ?? Task.CompletedTask;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _runTask = RunAsync(_stopping.Token);
    }

    public async ValueTask StopAsync()
    {
        _stopping.Cancel();
        if (_runTask is null)
        {
            return;
        }

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _stopping.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _clientSlots.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var firstInstance = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _clientSlots.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = _endpoint.CreateServerStream(firstInstance);
                    firstInstance = false;
                    await pipe.WaitForConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    pipe?.Dispose();
                    _clientSlots.Release();
                    throw;
                }

                var clientId = Interlocked.Increment(
                    ref _nextClientId);
                var clientTask = HandleClientAsync(
                    pipe,
                    cancellationToken);
                if (!_clientTasks.TryAdd(clientId, clientTask))
                {
                    pipe.Dispose();
                    _clientSlots.Release();
                    throw new InvalidOperationException(
                        "Duplicate IPC client identifier.");
                }

                _ = ObserveClientAsync(clientId, clientTask);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var tasks = _clientTasks.Values.ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveClientAsync(
        long clientId,
        Task clientTask)
    {
        try
        {
            await clientTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _clientTasks.TryRemove(clientId, out _);
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellation)
    {
        await using var ownedPipe = pipe;
        using var clientStopping =
            CancellationTokenSource.CreateLinkedTokenSource(
                serverCancellation);
        using var writeGate = new SemaphoreSlim(1, 1);
        SnapshotSubscription? subscription = null;
        Task? subscriptionWriter = null;

        try
        {
            var negotiation = await NegotiateAsync(
                pipe,
                writeGate,
                clientStopping.Token).ConfigureAwait(false);
            if (negotiation is null)
            {
                return;
            }

            var seenRequestIds = new HashSet<string>(
                StringComparer.Ordinal)
            {
                negotiation.HelloRequestId,
            };

            while (!clientStopping.IsCancellationRequested &&
                pipe.IsConnected)
            {
                using var document = await LengthPrefixedJson.ReadAsync(
                    pipe,
                    negotiation.MaxMessageSize,
                    clientStopping.Token).ConfigureAwait(false);
                if (document is null)
                {
                    return;
                }

                var request = DeserializeRequest(document);
                if (!seenRequestIds.Add(request.RequestId) ||
                    seenRequestIds.Count >
                        IpcProtocol.MaxRequestsPerConnection)
                {
                    await WriteErrorAsync(
                        pipe,
                        writeGate,
                        request.RequestId,
                        IpcErrorCodes.InvalidRequest,
                        negotiation.MaxMessageSize,
                        clientStopping.Token).ConfigureAwait(false);
                    return;
                }

                switch (request.Type)
                {
                    case "getSnapshot":
                        await WritePayloadAsync(
                            pipe,
                            writeGate,
                            "snapshot",
                            request.RequestId,
                            _service.ReadLatestSnapshot(),
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;

                    case "getCapabilities":
                        await WritePayloadAsync(
                            pipe,
                            writeGate,
                            "capabilities",
                            request.RequestId,
                            _service.ReadCapabilities(),
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;

                    case "getHealth":
                        await WritePayloadAsync(
                            pipe,
                            writeGate,
                            "health",
                            request.RequestId,
                            _service.ReadHealth(),
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;

                    case "queryHistory":
                        await HandleHistoryAsync(
                            pipe,
                            writeGate,
                            request,
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;

                    case "subscribe":
                        if (subscription is not null)
                        {
                            await WriteErrorAsync(
                                pipe,
                                writeGate,
                                request.RequestId,
                                IpcErrorCodes.InvalidRequest,
                                negotiation.MaxMessageSize,
                                clientStopping.Token).ConfigureAwait(false);
                            break;
                        }

                        subscription = _subscriptions.Subscribe();
                        await WriteAsync(
                            pipe,
                            writeGate,
                            new IpcResponseMessage
                            {
                                Type = "subscribed",
                                RequestId = request.RequestId,
                                InstanceId = _service.InstanceId,
                                SubscriptionId =
                                    subscription.SubscriptionId,
                            },
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        subscriptionWriter = SendSubscriptionAsync(
                            pipe,
                            writeGate,
                            subscription,
                            request.RequestId,
                            negotiation.MaxMessageSize,
                            clientStopping);
                        break;

                    case "unsubscribe":
                        if (subscription is null ||
                            !StringComparer.Ordinal.Equals(
                                request.SubscriptionId,
                                subscription.SubscriptionId))
                        {
                            await WriteErrorAsync(
                                pipe,
                                writeGate,
                                request.RequestId,
                                IpcErrorCodes.InvalidRequest,
                                negotiation.MaxMessageSize,
                                clientStopping.Token).ConfigureAwait(false);
                            break;
                        }

                        await subscription.DisposeAsync()
                            .ConfigureAwait(false);
                        subscription = null;
                        if (subscriptionWriter is not null)
                        {
                            await subscriptionWriter.ConfigureAwait(false);
                            subscriptionWriter = null;
                        }

                        await WriteAsync(
                            pipe,
                            writeGate,
                            new IpcResponseMessage
                            {
                                Type = "unsubscribed",
                                RequestId = request.RequestId,
                                InstanceId = _service.InstanceId,
                            },
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;

                    default:
                        await WriteErrorAsync(
                            pipe,
                            writeGate,
                            request.RequestId,
                            IpcErrorCodes.InvalidRequest,
                            negotiation.MaxMessageSize,
                            clientStopping.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            IpcProtocolException or
            JsonException or
            OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            // v0.5.0: a malformed or failed client is isolated from the
            // listener and from other Desktop instances.
        }
        finally
        {
            clientStopping.Cancel();
            if (subscription is not null)
            {
                await subscription.DisposeAsync()
                    .ConfigureAwait(false);
            }

            if (subscriptionWriter is not null)
            {
                try
                {
                    await subscriptionWriter.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task<Negotiation?> NegotiateAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        using var helloCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        helloCancellation.CancelAfter(IpcProtocol.HelloTimeout);
        using var document = await LengthPrefixedJson.ReadAsync(
            pipe,
            IpcProtocol.AbsoluteMaxMessageSize,
            helloCancellation.Token).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var hello = DeserializeRequest(document);
        if (hello.Type != "hello" ||
            hello.SupportedContractVersions is null ||
            hello.SupportedContractVersions.Count is 0 or >
                IpcProtocol.MaxSupportedVersions ||
            hello.MaxMessageSize is null ||
            hello.MaxMessageSize <
                IpcProtocol.MinimumNegotiatedMessageSize ||
            hello.MaxMessageSize >
                IpcProtocol.AbsoluteMaxMessageSize)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                hello.RequestId,
                IpcErrorCodes.InvalidRequest,
                IpcProtocol.AbsoluteMaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!hello.SupportedContractVersions.Contains(
            ContractVersions.V1,
            StringComparer.Ordinal))
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                hello.RequestId,
                IpcErrorCodes.ContractVersionUnsupported,
                Math.Min(
                    hello.MaxMessageSize.Value,
                    IpcProtocol.AbsoluteMaxMessageSize),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var negotiatedMaximum = Math.Min(
            hello.MaxMessageSize.Value,
            IpcProtocol.AbsoluteMaxMessageSize);
        await WriteAsync(
            pipe,
            writeGate,
            new IpcResponseMessage
            {
                Type = "helloAck",
                RequestId = hello.RequestId,
                SelectedContractVersion = ContractVersions.V1,
                ProductVersion = ProductVersions.Agent,
                InstanceId = _service.InstanceId,
                MaxMessageSize = negotiatedMaximum,
                Capabilities = JsonSerializer.SerializeToElement(
                    _service.ReadCapabilities(),
                    IpcJson.Options),
            },
            negotiatedMaximum,
            cancellationToken).ConfigureAwait(false);
        return new Negotiation(
            hello.RequestId,
            negotiatedMaximum);
    }

    private async Task HandleHistoryAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        IpcRequestMessage request,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        if (request.MetricIds is null ||
            request.MaxPoints is null)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                request.RequestId,
                IpcErrorCodes.InvalidRequest,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        requestCancellation.CancelAfter(IpcProtocol.RequestTimeout);
        try
        {
            var history = await _service.QueryHistoryAsync(
                new HistoryQueryContract
                {
                    MetricIds = request.MetricIds,
                    FromEpochMs = request.FromEpochMs,
                    ToEpochMs = request.ToEpochMs,
                    MaxPoints = request.MaxPoints.Value,
                },
                requestCancellation.Token).ConfigureAwait(false);
            await WritePayloadAsync(
                pipe,
                writeGate,
                "history",
                request.RequestId,
                history,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            requestCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                request.RequestId,
                IpcErrorCodes.RequestTimedOut,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            OverflowException)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                request.RequestId,
                IpcErrorCodes.InvalidRequest,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IpcServiceUnavailableException)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                request.RequestId,
                IpcErrorCodes.ServiceUnavailable,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            await WriteErrorAsync(
                pipe,
                writeGate,
                request.RequestId,
                IpcErrorCodes.ServiceUnavailable,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendSubscriptionAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        SnapshotSubscription subscription,
        string requestId,
        int maxMessageSize,
        CancellationTokenSource clientStopping)
    {
        try
        {
            await WritePayloadAsync(
                pipe,
                writeGate,
                "snapshotUpdate",
                requestId,
                _service.ReadLatestSnapshot(),
                maxMessageSize,
                clientStopping.Token).ConfigureAwait(false);
            await foreach (var snapshot in subscription.Reader
                .ReadAllAsync(clientStopping.Token)
                .ConfigureAwait(false))
            {
                await WritePayloadAsync(
                    pipe,
                    writeGate,
                    "snapshotUpdate",
                    requestId,
                    snapshot,
                    maxMessageSize,
                    clientStopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            IpcProtocolException or
            OperationCanceledException)
        {
            clientStopping.Cancel();
        }
    }

    private static IpcRequestMessage DeserializeRequest(
        JsonDocument document)
    {
        try
        {
            var request = document.Deserialize<IpcRequestMessage>(
                IpcJson.Options) ?? throw new JsonException(
                "IPC request is null.");
            if (string.IsNullOrWhiteSpace(request.Type) ||
                string.IsNullOrWhiteSpace(request.RequestId) ||
                request.RequestId.Length >
                    IpcProtocol.MaxRequestIdLength)
            {
                throw new IpcProtocolException(
                    IpcErrorCodes.InvalidRequest);
            }

            return request;
        }
        catch (JsonException)
        {
            throw new IpcProtocolException(
                IpcErrorCodes.InvalidRequest);
        }
    }

    private Task WritePayloadAsync<T>(
        Stream pipe,
        SemaphoreSlim writeGate,
        string type,
        string requestId,
        T payload,
        int maxMessageSize,
        CancellationToken cancellationToken) =>
        WriteAsync(
            pipe,
            writeGate,
            new IpcResponseMessage
            {
                Type = type,
                RequestId = requestId,
                InstanceId = _service.InstanceId,
                Payload = JsonSerializer.SerializeToElement(
                    payload,
                    IpcJson.Options),
            },
            maxMessageSize,
            cancellationToken);

    private static Task WriteErrorAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        string requestId,
        string errorCode,
        int maxMessageSize,
        CancellationToken cancellationToken) =>
        WriteAsync(
            pipe,
            writeGate,
            new IpcResponseMessage
            {
                Type = "error",
                RequestId = requestId,
                Error = new IpcErrorPayload
                {
                    ErrorCode = errorCode,
                },
            },
            maxMessageSize,
            cancellationToken);

    private static async Task WriteAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        IpcResponseMessage response,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await LengthPrefixedJson.WriteAsync(
                pipe,
                response,
                maxMessageSize,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private sealed record Negotiation(
        string HelloRequestId,
        int MaxMessageSize);
}
