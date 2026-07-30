using System.Text.Json;
using PerfMonitor.ProviderWorker.Protocol;

namespace PerfMonitor.Collectors.Worker;

internal interface IHardwareWorkerClient : IAsyncDisposable
{
    ValueTask<WorkerResponse> CollectAsync(
        CancellationToken cancellationToken);
}

internal interface IWorkerSessionFactory
{
    ValueTask<IWorkerSession> StartAsync(
        CancellationToken cancellationToken);
}

internal interface IWorkerSession : IAsyncDisposable
{
    bool HasExited { get; }

    long PrivateMemoryBytes { get; }

    ValueTask<WorkerResponse> ExchangeAsync(
        WorkerRequest request,
        CancellationToken cancellationToken);

    void Abort();
}

internal sealed class WorkerCommunicationException :
    IOException
{
    public WorkerCommunicationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class WorkerRestartLimitException :
    InvalidOperationException
{
    public WorkerRestartLimitException()
        : base("The hardware Worker restart budget is exhausted.")
    {
    }
}

internal sealed class WorkerResourceLimitException :
    InvalidOperationException
{
    public WorkerResourceLimitException(long privateMemoryBytes)
        : base(
            $"The hardware Worker exceeded its private-memory limit: " +
            $"{privateMemoryBytes} bytes.")
    {
    }
}

internal sealed class HardwareWorkerClient : IHardwareWorkerClient
{
    private readonly HardwareWorkerOptions _options;
    private readonly IWorkerSessionFactory _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<DateTimeOffset> _starts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IWorkerSession? _session;
    private string? _workerInstanceId;
    private long _lastSequence;
    private bool _disposed;

    public HardwareWorkerClient(
        HardwareWorkerOptions options,
        IWorkerSessionFactory sessionFactory,
        TimeProvider? timeProvider = null)
    {
        options.Validate();
        _options = options;
        _sessionFactory = sessionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<WorkerResponse> CollectAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session?.HasExited == true)
            {
                await ResetSessionAsync().ConfigureAwait(false);
            }

            _session ??= await StartSessionAsync(
                cancellationToken).ConfigureAwait(false);
            var request = new WorkerRequest(
                ProviderWorkerProtocol.CurrentVersion,
                Guid.NewGuid().ToString("N"),
                ProviderWorkerProtocol.CollectOperation);
            WorkerResponse response;
            try
            {
                response = await _session.ExchangeAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ResetSessionAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                InvalidDataException or
                JsonException)
            {
                await ResetSessionAsync().ConfigureAwait(false);
                throw new WorkerCommunicationException(
                    "The hardware Worker session failed.",
                    exception);
            }

            try
            {
                WorkerResponseValidator.Validate(
                    response,
                    request.RequestId,
                    _workerInstanceId,
                    _lastSequence);
            }
            catch (InvalidDataException)
            {
                await ResetSessionAsync().ConfigureAwait(false);
                throw;
            }

            var privateMemoryBytes = _session.PrivateMemoryBytes;
            if (privateMemoryBytes >
                _options.MaxPrivateMemoryBytes)
            {
                await ResetSessionAsync().ConfigureAwait(false);
                throw new WorkerResourceLimitException(
                    privateMemoryBytes);
            }

            _workerInstanceId = response.WorkerInstanceId;
            _lastSequence = response.Sequence;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await ResetSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<IWorkerSession> StartSessionAsync(
        CancellationToken cancellationToken)
    {
        var startedAt = EnsureStartAllowed();
        var session = await _sessionFactory.StartAsync(
            cancellationToken).ConfigureAwait(false);
        _starts.Enqueue(startedAt);
        return session;
    }

    private DateTimeOffset EnsureStartAllowed()
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _options.RestartWindow;
        while (_starts.TryPeek(out var startedAt) &&
            startedAt <= cutoff)
        {
            _starts.Dequeue();
        }

        if (_starts.Count >= _options.MaxStartsPerWindow)
        {
            throw new WorkerRestartLimitException();
        }

        return now;
    }

    private async ValueTask ResetSessionAsync()
    {
        var session = _session;
        _session = null;
        _workerInstanceId = null;
        _lastSequence = 0;
        if (session is null)
        {
            return;
        }

        session.Abort();
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            ObjectDisposedException)
        {
        }
    }
}
