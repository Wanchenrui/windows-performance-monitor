using System.IO;
using System.Runtime.InteropServices;
using PerfMonitor.Contracts;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public sealed record DesktopDiagnosticNotification(
    string InstanceId,
    IReadOnlyList<DiagnosticEventContract> Events);

public sealed record DesktopNotificationState(
    bool Enabled,
    bool TrayAvailable,
    bool DoNotDisturb,
    TimeSpan? DoNotDisturbRemaining,
    string? QueryError);

/// <summary>Opt-in, Desktop-session-only diagnostic notifications.</summary>
public sealed class DesktopDiagnosticNotifications : IAsyncDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan NotificationInterval = TimeSpan.FromMinutes(2);
    private const int MaxRememberedSubjects = 4096;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<string, DiagnosticQueryContract, CancellationToken, Task<DiagnosticsContract>> _query;
    private readonly Func<DesktopDiagnosticNotification, CancellationToken, Task<bool>> _show;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly HashSet<(string Rule, string Subject)> _seen = [];
    private CancellationTokenSource _generation = new();
    private DesktopConnectionState _connection = new(DesktopConnectionStatus.Starting, null, 0, null, null);
    private bool _enabled;
    private bool _trayAvailable;
    private bool _baseline = true;
    private bool _dnd;
    private long _dndStarted;
    private TimeSpan? _dndDuration;
    private long? _lastAttempt;
    private long? _lastSuccessfulPoll;
    private bool _capacityReached;
    private long _watermark;
    private string? _queryError;
    private Task? _runTask;
    private bool _disposed;

    public DesktopDiagnosticNotifications(
        PipeEndpoint endpoint,
        Func<DesktopDiagnosticNotification, CancellationToken, Task<bool>> showAsync,
        TimeProvider? timeProvider = null)
        : this(async (instance, query, token) =>
        {
            await using var client = await NamedPipeAgentClient.ConnectAsync(
                endpoint, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(client.InstanceId, instance))
                throw new IpcProtocolException("notification_instance_changed");
            return await client.QueryDiagnosticsAsync(query, token).ConfigureAwait(false);
        }, showAsync, timeProvider)
    {
    }

    internal DesktopDiagnosticNotifications(
        Func<string, DiagnosticQueryContract, CancellationToken, Task<DiagnosticsContract>> queryAsync,
        Func<DesktopDiagnosticNotification, CancellationToken, Task<bool>> showAsync,
        TimeProvider? timeProvider = null)
    {
        _query = queryAsync;
        _show = showAsync;
        _time = timeProvider ?? TimeProvider.System;
    }

    public DesktopNotificationState Current
    {
        get { lock (_gate) { ExpireDoNotDisturb(); return State(); } }
    }

    public event Action<DesktopNotificationState>? StateChanged;

    public void UpdateConnection(DesktopConnectionState connection)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested) return;
            var changed = _connection.Status != connection.Status ||
                !StringComparer.Ordinal.Equals(_connection.InstanceId, connection.InstanceId) ||
                _connection.RestartCount != connection.RestartCount;
            if (!StringComparer.Ordinal.Equals(_connection.InstanceId, connection.InstanceId))
            {
                _seen.Clear(); _watermark = 0; _capacityReached = false;
                _lastAttempt = null;
            }
            _connection = connection;
            if (!changed) return;
            ResetBaseline();
        }
        PublishAndWake();
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested || _enabled == enabled) return;
            _enabled = enabled; ResetBaseline();
        }
        PublishAndWake();
    }

    public void SetTrayAvailable(bool available)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested || _trayAvailable == available) return;
            _trayAvailable = available; ResetBaseline();
        }
        PublishAndWake();
    }

    public void SetDoNotDisturb(TimeSpan? duration)
    {
        if (duration is { } value && value != TimeSpan.FromMinutes(15) && value != TimeSpan.FromMinutes(60))
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested) return;
            _dnd = true; _dndStarted = _time.GetTimestamp(); _dndDuration = duration;
            ResetBaseline();
        }
        PublishAndWake();
    }

    public void EndDoNotDisturb()
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested || !_dnd) return;
            _dnd = false; _dndDuration = null; ResetBaseline();
        }
        PublishAndWake();
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runTask ??= RunCoreAsync(cancellationToken);
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await PollOnceAsync(lifetime.Token).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var signaled = _wake.WaitAsync(wait.Token);
                var delay = Task.Delay(PollInterval, _time, wait.Token);
                await Task.WhenAny(signaled, delay).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(signaled, delay).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string instance;
            CancellationToken generation;
            lock (_gate)
            {
                ExpireDoNotDisturb();
                if (!CanQuery()) return;
                instance = _connection.InstanceId!; generation = _generation.Token;
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _stopping.Token, generation);
            using var timeout = new CancellationTokenSource(QueryTimeout, _time);
            using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, timeout.Token);
            var to = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var request = new DiagnosticQueryContract
            {
                FromEpochMs = to - (long)TimeSpan.FromMinutes(5).TotalMilliseconds,
                ToEpochMs = to,
                MaxEvents = 200,
            };
            DiagnosticsContract response;
            try
            {
                response = await _query(instance, request, queryCancellation.Token)
                    .WaitAsync(queryCancellation.Token).ConfigureAwait(false);
                if (!ValidResponse(instance, request, response))
                {
                    QueryFailed(generation, "notification_query_mismatch"); return;
                }
                if (response.Truncated)
                {
                    QueryFailed(generation, "notification_query_truncated"); return;
                }
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { QueryFailed(generation, "notification_query_timeout"); return; }
            catch (Exception exception) when (exception is IOException or TimeoutException or
                UnauthorizedAccessException or IpcProtocolException or IpcRemoteException)
            { QueryFailed(generation, "notification_query_unavailable"); return; }

            DesktopDiagnosticNotification? notification = null;
            lock (_gate)
            {
                if (attempt.IsCancellationRequested || !CanQuery() || _generation.Token != generation) return;
                ExpireDoNotDisturb();
                var limited = _lastAttempt is { } last &&
                    _time.GetElapsedTime(last) < NotificationInterval;
                // A real polling gap (for example sleep/resume) establishes a new baseline.
                // Regular polls consume events during cooldown without discarding a new episode after it.
                if (_lastSuccessfulPoll is { } previous &&
                    _time.GetElapsedTime(previous) > PollInterval + QueryTimeout) _baseline = true;
                _lastSuccessfulPoll = _time.GetTimestamp();
                _queryError = _capacityReached ? "notification_capacity_reached" : null;
                var current = response.Events.Where(item => item.InstanceId == instance &&
                    item.ObservationSequence is > 0 && DiagnosticRuleIds.All.Contains(item.RuleId) &&
                    !string.IsNullOrWhiteSpace(item.SubjectId) &&
                    item.LastSeenUtc.ToUnixTimeMilliseconds() >= request.FromEpochMs &&
                    item.LastSeenUtc.ToUnixTimeMilliseconds() <= request.ToEpochMs).ToArray();
                var latest = current.GroupBy(item => (item.RuleId, item.SubjectId))
                    .Select(group => (Event: group.MaxBy(item => item.ObservationSequence)!,
                        Recovered: group.Any(item => item.State == DiagnosticStates.Resolved &&
                            item.ObservationSequence > _watermark)))
                    .Where(item => item.Event.ObservationSequence > _watermark)
                    .ToArray();
                var candidates = new List<DiagnosticEventContract>();
                foreach (var entry in latest)
                {
                    var item = entry.Event;
                    var key = (item.RuleId, item.SubjectId);
                    // Resolution ends the episode, including resolved -> active within one batch.
                    if (entry.Recovered) _seen.Remove(key);
                    if (item.State != DiagnosticStates.Active ||
                        item.Severity is not (DiagnosticSeverities.Warning or DiagnosticSeverities.Critical)) continue;
                    if (_seen.Contains(key)) continue;
                    if (_seen.Count >= MaxRememberedSubjects)
                    { _capacityReached = true; _queryError = "notification_capacity_reached"; continue; }
                    _seen.Add(key);
                    candidates.Add(item);
                }
                if (current.Length > 0)
                    _watermark = Math.Max(_watermark, current.Max(item => item.ObservationSequence!.Value));
                if (!_baseline && !_dnd && !limited && !_capacityReached && candidates.Count > 0)
                {
                    notification = new(instance, candidates.ToArray());
                    // Even a rejected display request is consumed, avoiding repeated attempts.
                    _lastAttempt = _time.GetTimestamp();
                }
                _baseline = false;
            }
            Publish();
            if (notification is not null && !attempt.IsCancellationRequested)
            {
                // The UI must check this token again on its Dispatcher before requesting a balloon.
                // A true return only means the tray accepted the request; Windows may suppress it.
                try { await _show(notification, attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                catch (Exception exception) when (exception is ExternalException or ObjectDisposedException)
                {
                    lock (_gate)
                    {
                        if (_generation.Token != generation || !CanQuery()) return;
                        _queryError = "notification_display_unavailable";
                    }
                    Publish();
                }
            }
        }
        finally { _pollGate.Release(); }
    }

    private static bool ValidResponse(string instance, DiagnosticQueryContract query, DiagnosticsContract response) =>
        response.ContractVersion == ContractVersions.V1 && response.InstanceId == instance &&
        response.EventCount == response.Events.Count && response.Events.Count <= query.MaxEvents &&
        response.Query.FromEpochMs == query.FromEpochMs && response.Query.ToEpochMs == query.ToEpochMs &&
        response.Query.MaxEvents == query.MaxEvents &&
        response.Query.RuleIds.SequenceEqual(query.RuleIds, StringComparer.Ordinal) &&
        response.Query.States.SequenceEqual(query.States, StringComparer.Ordinal);

    private bool CanQuery() => !_disposed && !_stopping.IsCancellationRequested && _enabled && _trayAvailable &&
        _connection.Status == DesktopConnectionStatus.Connected && !string.IsNullOrWhiteSpace(_connection.InstanceId);

    private void QueryFailed(CancellationToken generation, string error)
    {
        lock (_gate)
        {
            if (_generation.Token != generation || !CanQuery()) return;
            _baseline = true; _queryError = error;
        }
        Publish();
    }

    private void ResetBaseline()
    {
        _baseline = true; _queryError = null; _lastSuccessfulPoll = null;
        _generation.Cancel(); _generation.Dispose(); _generation = new();
    }

    private void ExpireDoNotDisturb()
    {
        if (_dnd && _dndDuration is { } duration && _time.GetElapsedTime(_dndStarted) >= duration)
        { _dnd = false; _dndDuration = null; _baseline = true; }
    }

    private DesktopNotificationState State() => new(_enabled, _trayAvailable, _dnd,
        _dnd && _dndDuration is { } duration
            ? duration - _time.GetElapsedTime(_dndStarted) : null, _queryError);

    private void Publish()
    {
        DesktopNotificationState state;
        lock (_gate) { if (_disposed || _stopping.IsCancellationRequested) return; state = State(); }
        StateChanged?.Invoke(state);
    }

    private void PublishAndWake()
    {
        Publish();
        lock (_gate)
        {
            if (!_disposed && !_stopping.IsCancellationRequested && _wake.CurrentCount == 0) _wake.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            if (_disposed) return;
            _stopping.Cancel(); _generation.Cancel(); run = _runTask;
        }
        if (run is not null) await run.ConfigureAwait(false);
        await _pollGate.WaitAsync().ConfigureAwait(false);
        _pollGate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation.Dispose(); _stopping.Dispose();
            _wake.Dispose(); _pollGate.Dispose();
        }
    }
}
