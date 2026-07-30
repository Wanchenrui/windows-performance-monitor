using System.Collections.Concurrent;
using System.IO.Pipes;
using PerfMonitor.Actions;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker;

public sealed class BrokerNamedPipeServer : IAsyncDisposable
{
    private readonly BrokerPipeEndpoint _endpoint;
    private readonly BrokerActionCoordinator _coordinator;
    private readonly IBrokerClientIdentityResolver _identityResolver;
    private readonly bool _forceDryRunOnly;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _clientSlots = new(
        BrokerPipeEndpoint.MaxServerInstances,
        BrokerPipeEndpoint.MaxServerInstances);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _instanceId =
        Guid.NewGuid().ToString("N");
    private Task? _runTask;
    private long _nextClientId;
    private int _started;

    public BrokerNamedPipeServer(
        BrokerPipeEndpoint endpoint,
        BrokerActionCoordinator coordinator,
        IBrokerClientIdentityResolver? identityResolver = null,
        bool forceDryRunOnly = false,
        TimeProvider? timeProvider = null)
    {
        _endpoint = endpoint ??
            throw new ArgumentNullException(nameof(endpoint));
        _coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        _identityResolver = identityResolver ??
            new BrokerClientIdentityResolver();
        _forceDryRunOnly = forceDryRunOnly;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string InstanceId => _instanceId;

    public BrokerPipeEndpoint Endpoint => _endpoint;

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

    private async Task RunAsync(
        CancellationToken cancellationToken)
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
                    pipe = _endpoint.CreateServerStream(
                        firstInstance);
                    firstInstance = false;
                    await pipe.WaitForConnectionAsync(
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    pipe?.Dispose();
                    _clientSlots.Release();
                    throw;
                }

                var id = Interlocked.Increment(
                    ref _nextClientId);
                var task = HandleClientAsync(
                    pipe,
                    cancellationToken);
                if (!_clients.TryAdd(id, task))
                {
                    pipe.Dispose();
                    _clientSlots.Release();
                    throw new InvalidOperationException(
                        "Duplicate Broker client identifier.");
                }

                _ = ObserveClientAsync(id, task);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var clients = _clients.Values.ToArray();
            if (clients.Length > 0)
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveClientAsync(
        long id,
        Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
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
        try
        {
            var caller = _identityResolver.Resolve(pipe);
            var negotiation = await NegotiateAsync(
                pipe,
                caller,
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
                using var document = await BrokerFraming.ReadAsync(
                    pipe,
                    negotiation.MaxMessageSize,
                    clientStopping.Token).ConfigureAwait(false);
                if (document is null)
                {
                    return;
                }

                BrokerRequestMessage request;
                try
                {
                    request = BrokerRequestParser.Parse(document);
                }
                catch (BrokerProtocolException exception)
                {
                    await WriteErrorAsync(
                        pipe,
                        requestId: "invalid",
                        exception.ErrorCode,
                        negotiation.MaxMessageSize,
                        clientStopping.Token).ConfigureAwait(false);
                    return;
                }

                if (!seenRequestIds.Add(request.RequestId) ||
                    seenRequestIds.Count >
                        BrokerProtocol.MaxRequestsPerConnection ||
                    request.Type !=
                        BrokerMessageTypes.ExecuteAction)
                {
                    await WriteErrorAsync(
                        pipe,
                        request.RequestId,
                        ActionErrorCodes.InvalidRequest,
                        negotiation.MaxMessageSize,
                        clientStopping.Token).ConfigureAwait(false);
                    return;
                }

                var result = await _coordinator.ExecuteAsync(
                    request.ToExecutionRequest(),
                    caller,
                    _timeProvider.GetUtcNow(),
                    clientStopping.Token).ConfigureAwait(false);
                await BrokerFraming.WriteAsync(
                    pipe,
                    new BrokerResponseMessage
                    {
                        Type = BrokerMessageTypes.ActionResult,
                        RequestId = request.RequestId,
                        BrokerInstanceId = _instanceId,
                        Result = result,
                    },
                    negotiation.MaxMessageSize,
                    clientStopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            BrokerProtocolException or
            ActionExecutorException or
            OperationCanceledException or
            UnauthorizedAccessException)
        {
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            // A failed local client is isolated from the listener.
        }
    }

    private async Task<Negotiation?> NegotiateAsync(
        Stream pipe,
        BrokerCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        using var helloCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        helloCancellation.CancelAfter(
            BrokerProtocol.HelloTimeout);
        using var document = await BrokerFraming.ReadAsync(
            pipe,
            BrokerProtocol.AbsoluteMaxMessageSize,
            helloCancellation.Token).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        BrokerRequestMessage request;
        try
        {
            request = BrokerRequestParser.Parse(document);
        }
        catch (BrokerProtocolException exception)
        {
            await WriteErrorAsync(
                pipe,
                requestId: "invalid",
                exception.ErrorCode,
                BrokerProtocol.AbsoluteMaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (request.Type != BrokerMessageTypes.Hello ||
            request.SupportedBrokerProtocolVersions is null ||
            request.MaxMessageSize is null)
        {
            await WriteErrorAsync(
                pipe,
                request.RequestId,
                ActionErrorCodes.InvalidRequest,
                BrokerProtocol.AbsoluteMaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var negotiatedMaximum = Math.Min(
            request.MaxMessageSize.Value,
            BrokerProtocol.AbsoluteMaxMessageSize);
        if (!request.SupportedBrokerProtocolVersions.Contains(
            BrokerProtocol.Version,
            StringComparer.Ordinal))
        {
            await WriteErrorAsync(
                pipe,
                request.RequestId,
                ActionErrorCodes.ProtocolVersionUnsupported,
                negotiatedMaximum,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        await BrokerFraming.WriteAsync(
            pipe,
            new BrokerResponseMessage
            {
                Type = BrokerMessageTypes.HelloAck,
                RequestId = request.RequestId,
                SelectedBrokerProtocolVersion =
                    BrokerProtocol.Version,
                BrokerInstanceId = _instanceId,
                MaxMessageSize = negotiatedMaximum,
                Capabilities = BuildCapabilities(caller),
            },
            negotiatedMaximum,
            cancellationToken).ConfigureAwait(false);
        return new Negotiation(
            request.RequestId,
            negotiatedMaximum);
    }

    private ActionsCapabilityContract BuildCapabilities(
        BrokerCallerIdentity caller)
    {
        var policy = _coordinator.Policy;
        var callerAllowed = policy.AllowedCallerSids.Contains(
            caller.Sid,
            StringComparer.Ordinal);
        var imageAllowed =
            !policy.RequireApprovedClientImage ||
            policy.ApprovedClientImages.Any(image =>
                image.Matches(
                    caller.ClientImagePath,
                    caller.ClientImageSha256));
        var actions = callerAllowed && imageAllowed
            ? policy.EnabledActionTypes.Select(
                static action =>
                    new ActionCapabilityItemContract
                    {
                        ActionType = action,
                        DryRunSupported = true,
                    }).ToArray()
            : [];
        return new ActionsCapabilityContract
        {
            BrokerProtocolVersion = BrokerProtocol.Version,
            BrokerConfigured = true,
            BrokerAvailable = true,
            DryRunOnly = policy.DryRunOnly ||
                _forceDryRunOnly,
            Actions = actions,
        };
    }

    private static ValueTask WriteErrorAsync(
        Stream pipe,
        string requestId,
        string errorCode,
        int maxMessageSize,
        CancellationToken cancellationToken) =>
        BrokerFraming.WriteAsync(
            pipe,
            new BrokerResponseMessage
            {
                Type = BrokerMessageTypes.Error,
                RequestId = requestId,
                Error = new BrokerErrorPayload
                {
                    ErrorCode = errorCode,
                },
            },
            maxMessageSize,
            cancellationToken);

    private sealed record Negotiation(
        string HelloRequestId,
        int MaxMessageSize);
}
