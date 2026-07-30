using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using PerfMonitor.Broker.Protocol;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker.Client;

public sealed record BrokerClientOptions
{
    public string PipeName { get; init; } =
        "PerfMonitor.Broker.v1";
    public TimeSpan ConnectTimeout { get; init; } =
        TimeSpan.FromSeconds(2);

    public BrokerClientOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) ||
            PipeName.Length > 240 ||
            PipeName.Contains('\\', StringComparison.Ordinal) ||
            ConnectTimeout <= TimeSpan.Zero ||
            ConnectTimeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BrokerClientOptions));
        }

        return this;
    }
}

public interface IBrokerActionClient
{
    bool LastKnownAvailable { get; }

    ValueTask<ActionsCapabilityContract> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    ValueTask<ActionResultContract> ExecuteAsync(
        ActionExecutionRequestContract request,
        CancellationToken cancellationToken);
}

public sealed class BrokerUnavailableException : Exception
{
    public BrokerUnavailableException(
        string errorCode,
        Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class BrokerActionClient : IBrokerActionClient
{
    private readonly BrokerClientOptions _options;
    private int _lastKnownAvailable;

    public BrokerActionClient(BrokerClientOptions? options = null)
    {
        _options = (options ?? new BrokerClientOptions())
            .Validate();
    }

    public bool LastKnownAvailable =>
        Volatile.Read(ref _lastKnownAvailable) != 0;

    public async ValueTask<ActionsCapabilityContract>
        GetCapabilitiesAsync(
            CancellationToken cancellationToken)
    {
        await using var session = await ConnectAsync(
            cancellationToken).ConfigureAwait(false);
        return session.Capabilities;
    }

    public async ValueTask<ActionResultContract> ExecuteAsync(
        ActionExecutionRequestContract request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ActionContractValidation.Validate(
                request,
                DateTimeOffset.UtcNow);
        }
        catch (ArgumentException exception)
        {
            throw new BrokerUnavailableException(
                ActionErrorCodes.InvalidRequest,
                exception);
        }

        await using var session = await ConnectAsync(
            cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            await BrokerFraming.WriteAsync(
                session.Pipe,
                new BrokerRequestMessage
                {
                    Type = BrokerMessageTypes.ExecuteAction,
                    RequestId = requestId,
                    IdempotencyKey = request.IdempotencyKey,
                    DeadlineUtc = request.DeadlineUtc,
                    DryRun = request.DryRun,
                    AgentPolicyVersion =
                        request.AgentPolicyVersion,
                    Action = request.Action,
                },
                session.MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            using var document = await BrokerFraming.ReadAsync(
                session.Pipe,
                session.MaxMessageSize,
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                throw new BrokerUnavailableException(
                    ActionErrorCodes.ServiceUnavailable);
            }

            var response =
                document.Deserialize<BrokerResponseMessage>(
                    BrokerJson.Options) ??
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            if (!StringComparer.Ordinal.Equals(
                    response.RequestId,
                    requestId) ||
                response.Type != BrokerMessageTypes.ActionResult ||
                response.Result is null ||
                !StringComparer.Ordinal.Equals(
                    response.Result.IdempotencyKey,
                    request.IdempotencyKey) ||
                !ActionStatuses.Terminal.Contains(
                    response.Result.Status,
                    StringComparer.Ordinal))
            {
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            }

            Volatile.Write(ref _lastKnownAvailable, 1);
            return response.Result;
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            BrokerProtocolException or
            UnauthorizedAccessException)
        {
            Volatile.Write(ref _lastKnownAvailable, 0);
            throw new BrokerUnavailableException(
                ActionErrorCodes.ServiceUnavailable,
                exception);
        }
    }

    private async ValueTask<Session> ConnectAsync(
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _options.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous |
                PipeOptions.WriteThrough,
            impersonationLevel:
                TokenImpersonationLevel.Impersonation);
        try
        {
            using var connectCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            connectCancellation.CancelAfter(
                _options.ConnectTimeout);
            await pipe.ConnectAsync(
                connectCancellation.Token).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");
            await BrokerFraming.WriteAsync(
                pipe,
                new BrokerRequestMessage
                {
                    Type = BrokerMessageTypes.Hello,
                    RequestId = requestId,
                    SupportedBrokerProtocolVersions =
                    [
                        BrokerProtocol.Version,
                    ],
                    MaxMessageSize =
                        BrokerProtocol.AbsoluteMaxMessageSize,
                },
                BrokerProtocol.AbsoluteMaxMessageSize,
                connectCancellation.Token).ConfigureAwait(false);
            using var document = await BrokerFraming.ReadAsync(
                pipe,
                BrokerProtocol.AbsoluteMaxMessageSize,
                connectCancellation.Token).ConfigureAwait(false);
            if (document is null)
            {
                throw new BrokerProtocolException(
                    ActionErrorCodes.ServiceUnavailable);
            }

            var response =
                document.Deserialize<BrokerResponseMessage>(
                    BrokerJson.Options) ??
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            if (response.Type != BrokerMessageTypes.HelloAck ||
                !StringComparer.Ordinal.Equals(
                    response.RequestId,
                    requestId) ||
                !StringComparer.Ordinal.Equals(
                    response.SelectedBrokerProtocolVersion,
                    BrokerProtocol.Version) ||
                string.IsNullOrWhiteSpace(
                    response.BrokerInstanceId) ||
                response.MaxMessageSize is null or <
                    BrokerProtocol.MinimumNegotiatedMessageSize or >
                    BrokerProtocol.AbsoluteMaxMessageSize ||
                response.Capabilities is null)
            {
                throw new BrokerProtocolException(
                    ActionErrorCodes.InvalidRequest);
            }

            Volatile.Write(ref _lastKnownAvailable, 1);
            return new Session(
                pipe,
                response.MaxMessageSize.Value,
                response.Capabilities);
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            OperationCanceledException or
            BrokerProtocolException or
            UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _lastKnownAvailable, 0);
            if (exception is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new BrokerUnavailableException(
                ActionErrorCodes.ServiceUnavailable,
                exception);
        }
    }

    private sealed class Session(
        NamedPipeClientStream pipe,
        int maxMessageSize,
        ActionsCapabilityContract capabilities) :
        IAsyncDisposable
    {
        public NamedPipeClientStream Pipe { get; } = pipe;
        public int MaxMessageSize { get; } = maxMessageSize;
        public ActionsCapabilityContract Capabilities { get; } =
            capabilities;

        public ValueTask DisposeAsync() =>
            Pipe.DisposeAsync();
    }
}
