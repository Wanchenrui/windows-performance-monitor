using System.IO;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public enum DesktopConnectionStatus
{
    Starting,
    Connected,
    Reconnecting,
    Stopped,
}

public sealed record DesktopConnectionState(
    DesktopConnectionStatus Status,
    string? InstanceId,
    long RestartCount,
    AgentSnapshot? LatestSnapshot,
    string? ErrorCode);

public sealed class DesktopAgentSession
{
    private readonly PipeEndpoint _endpoint;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _reconnectDelay;
    private DesktopConnectionState _current = new(
        DesktopConnectionStatus.Starting,
        null,
        0,
        null,
        null);

    public DesktopAgentSession(
        PipeEndpoint endpoint,
        TimeSpan? connectTimeout = null,
        TimeSpan? reconnectDelay = null)
    {
        _endpoint = endpoint;
        _connectTimeout =
            connectTimeout ?? TimeSpan.FromSeconds(3);
        _reconnectDelay =
            reconnectDelay ?? TimeSpan.FromSeconds(1);
    }

    public DesktopConnectionState Current =>
        Volatile.Read(ref _current);

    public event Action<DesktopConnectionState>? StateChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string? lastSeenInstanceId = Current.InstanceId;
        while (!cancellationToken.IsCancellationRequested)
        {
            Publish(Current with
            {
                Status = lastSeenInstanceId is null
                    ? DesktopConnectionStatus.Starting
                    : DesktopConnectionStatus.Reconnecting,
                ErrorCode = null,
            });

            try
            {
                await using var client =
                    await NamedPipeAgentClient.ConnectAsync(
                        _endpoint,
                        _connectTimeout,
                        cancellationToken).ConfigureAwait(false);
                var restartCount = Current.RestartCount;
                if (lastSeenInstanceId is not null &&
                    !StringComparer.Ordinal.Equals(
                        lastSeenInstanceId,
                        client.InstanceId))
                {
                    restartCount++;
                }

                lastSeenInstanceId = client.InstanceId;
                Publish(Current with
                {
                    Status = DesktopConnectionStatus.Connected,
                    InstanceId = client.InstanceId,
                    RestartCount = restartCount,
                    ErrorCode = null,
                });

                await foreach (var snapshot in client
                    .SubscribeAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!StringComparer.Ordinal.Equals(
                        snapshot.InstanceId,
                        client.InstanceId))
                    {
                        throw new IpcProtocolException(
                            "snapshot_instance_mismatch");
                    }

                    Publish(Current with
                    {
                        Status = DesktopConnectionStatus.Connected,
                        InstanceId = snapshot.InstanceId,
                        LatestSnapshot = snapshot,
                        ErrorCode = null,
                    });
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or
                TimeoutException or
                EndOfStreamException or
                IpcProtocolException or
                IpcRemoteException)
            {
                Publish(Current with
                {
                    Status = DesktopConnectionStatus.Reconnecting,
                    ErrorCode = StableErrorCode(exception),
                });
            }

            try
            {
                await Task.Delay(
                    _reconnectDelay,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        Publish(Current with
        {
            Status = DesktopConnectionStatus.Stopped,
            ErrorCode = null,
        });
    }

    private static string StableErrorCode(Exception exception) =>
        exception switch
        {
            IpcRemoteException remote => remote.ErrorCode,
            IpcProtocolException protocol => protocol.ErrorCode,
            TimeoutException => "agent_connect_timeout",
            EndOfStreamException => "agent_disconnected",
            IOException => "agent_io_failure",
            _ => "agent_unavailable",
        };

    private void Publish(DesktopConnectionState state)
    {
        Volatile.Write(ref _current, state);
        StateChanged?.Invoke(state);
    }
}
