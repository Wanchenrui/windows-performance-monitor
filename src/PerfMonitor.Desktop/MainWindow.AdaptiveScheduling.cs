using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PerfMonitor.Contracts;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public sealed partial class MainWindow
{
    private readonly DesktopAdaptiveSchedulingSession _adaptiveScheduling;
    private AdaptiveSchedulingDrawerView? _adaptiveView;
    private DispatcherTimer? _adaptiveRefreshTimer;
    internal DesktopAdaptiveSchedulingSession AdaptiveScheduling => _adaptiveScheduling;

    private void OnAdaptiveSchedulingStateChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) Refresh();
        else _ = Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
        void Refresh()
        {
            if (_stopTask is null && _panel == DashboardPanel.AdaptiveScheduling) PopulateAdaptiveSchedulingDrawer();
        }
    }

    private void PopulateAdaptiveSchedulingDrawer()
    {
        var view = _adaptiveView ??= new(this);
        _drawerTitle.Text = "智能采集调度";
        if (!_drawerRows.Children.Contains(view.Root))
        { _drawerRows.Children.Clear(); _drawerRows.Children.Add(view.Root); }
        if (_adaptiveRefreshTimer is null)
        {
            _adaptiveRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(10) };
            _adaptiveRefreshTimer.Tick += (_, _) =>
            {
                if (_stopTask is null && IsVisible && WindowState != WindowState.Minimized && _panel == DashboardPanel.AdaptiveScheduling)
                    _ = _adaptiveScheduling.QueryAsync();
            };
            _adaptiveRefreshTimer.Start();
        }

        var state = _adaptiveScheduling.Current;
        var actual = state.Actual;
        view.SynchronizePreference(actual);
        var expired = actual is { Enabled: true } && state.RemainingSeconds is <= 0;
        view.Status.Text = state.Supported == false ? "当前 Agent 不支持" : actual is null ? "实际状态未确认" : expired ? "期限已到 · 等待刷新实际状态"
            : actual.Enabled ? "已启用 · " + PreferenceName(actual.Preference) : "智能调度已关闭";
        view.Status.Foreground = actual is { Enabled: true } && !expired ? Paint("#77D9C8") : Muted;
        view.Reason.Text = actual?.Reason ?? (state.Supported == false ? "请使用支持智能采集调度的 Agent。" : "连接后刷新；不会自动开启。");
        view.Remaining.Text = actual is { Enabled: true } && state.RemainingSeconds is { } seconds
            ? $"剩余约 {Math.Max(0, (int)Math.Ceiling(seconds / 60))} 分钟 · 切换偏好不续期"
            : "默认关闭 · 期限 15 / 30 / 60 分钟";
        view.Error.Text = state.Error ?? ""; view.Error.Visibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible;
        view.Refresh.Content = state.Busy ? "处理中…" : "刷新实际状态";
        view.Refresh.IsEnabled = !state.Busy && state.Connected && _stopTask is null;
        var canAct = !state.Busy && state.Connected && actual is { Supported: true } && _stopTask is null && !expired;
        view.Enable.IsEnabled = canAct && actual!.Enabled == false;
        view.Disable.IsEnabled = canAct && actual!.Enabled && actual.CanRestore;
        view.Switch.IsEnabled = canAct && actual!.Enabled && actual.Preference != view.SelectedPreference;
        foreach (var (preference, button) in view.Preferences)
        {
            button.IsEnabled = !state.Busy && state.Supported != false && _stopTask is null;
            MarkSelected(button, preference == view.SelectedPreference);
            AutomationProperties.SetItemStatus(button, preference == view.SelectedPreference ? "已选中" : "未选中");
        }
        foreach (var (duration, button) in view.Durations)
        {
            button.IsEnabled = !state.Busy && actual?.Enabled != true && state.Supported != false && _stopTask is null;
            var selected = actual?.Enabled != true && duration == view.SelectedDuration;
            MarkSelected(button, selected);
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
        }
        view.DurationControls.Visibility = actual?.Enabled == true ? Visibility.Collapsed : Visibility.Visible;
        view.DurationHint.Text = actual?.Enabled == true ? "运行中不能更改期限；关闭后再启用可选新期限。" : "仅在启用时确定期限";
        view.SelectionHint.Text = actual is not null && actual.Preference != view.SelectedPreference
            ? "待应用偏好 · " + PreferenceName(view.SelectedPreference) : "选择偏好 · " + PreferenceName(view.SelectedPreference);
        foreach (var (groupId, row) in view.Groups)
            row.Update(expired ? null : actual?.Groups.FirstOrDefault(group => group.GroupId == groupId), actual is not null && !expired);
        view.Recommendations.Text = actual is null ? "刷新实际状态后查看建议。" : actual.Recommendations.Count == 0 ? "当前没有额外建议。" : string.Join("\n", actual.Recommendations.Take(4));
        view.Impact.Text = actual?.ImpactDescription ?? "仅影响本软件可选硬件查询周期。";
        view.Scope.Text = "仅本次 Agent 会话有效 · 到期自动恢复\n只降低本软件 GPU / 温度查询频率\n性能收益未实测\n手动暂停优先；基础指标继续采集\n关闭 Desktop 后仍由 Agent 按期限恢复";
    }

    private static string PreferenceName(string preference) => preference switch
    {
        AdaptiveSchedulingPreferences.Throughput => "任务吞吐",
        AdaptiveSchedulingPreferences.EnergySaving => "节能",
        _ => "交互流畅",
    };

    private sealed class AdaptiveSchedulingDrawerView
    {
        public StackPanel Root { get; } = new();
        public TextBlock Status { get; } = Text("实际状态未确认", 13, Muted, FontWeights.SemiBold);
        public TextBlock Reason { get; } = Text("", 11, Muted);
        public TextBlock Remaining { get; } = Text("", 10, Muted);
        public TextBlock Error { get; } = Text("", 11, Warning);
        public TextBlock Scope { get; } = Text("", 10, Muted);
        public TextBlock DurationHint { get; } = Text("", 10, Muted);
        public TextBlock Recommendations { get; } = Text("", 11, Muted);
        public TextBlock Impact { get; } = Text("", 10, Muted);
        public TextBlock SelectionHint { get; } = Text("", 10, Muted);
        private bool _preferenceDirty;
        public Dictionary<string, Button> Preferences { get; } = [];
        public Dictionary<int, Button> Durations { get; } = [];
        public WrapPanel DurationControls { get; } = new();
        public Dictionary<string, AdaptiveGroupView> Groups { get; } = [];
        public string SelectedPreference { get; private set; } = AdaptiveSchedulingPreferences.Responsiveness;
        public int SelectedDuration { get; private set; } = AdaptiveSchedulingLimits.DefaultDurationSeconds;
        public Button Refresh { get; }
        public Button Enable { get; }
        public Button Disable { get; }
        public Button Switch { get; }
        public AdaptiveSchedulingDrawerView(MainWindow owner)
        {
            Root.Children.Add(Status); Reason.Margin = new Thickness(0, 5, 0, 5); Root.Children.Add(Reason); Root.Children.Add(Remaining);
            Root.Children.Add(SelectionHint);
            var preferences = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            foreach (var preference in AdaptiveSchedulingPreferences.All)
            {
                var button = Action(PreferenceName(preference), (_, _) => { SelectedPreference = preference; _preferenceDirty = true; owner.PopulateAdaptiveSchedulingDrawer(); });
                AutomationProperties.SetName(button, "智能调度偏好 · " + PreferenceName(preference));
                Preferences.Add(preference, button); preferences.Children.Add(button);
            }
            Root.Children.Add(preferences);
            foreach (var duration in AdaptiveSchedulingLimits.DurationSeconds)
            {
                var button = Action($"{duration / 60} 分钟", (_, _) => { SelectedDuration = duration; owner.PopulateAdaptiveSchedulingDrawer(); });
                AutomationProperties.SetName(button, $"智能调度期限 · {duration / 60} 分钟");
                Durations.Add(duration, button); DurationControls.Children.Add(button);
            }
            Root.Children.Add(DurationControls); Root.Children.Add(DurationHint);
            var commands = new WrapPanel { Margin = new Thickness(0, 6, 0, 5) };
            Enable = Action("启用", async (_, _) => await owner._adaptiveScheduling.SetAsync(true, SelectedPreference, SelectedDuration));
            Disable = Action("关闭并恢复", async (_, _) => await owner._adaptiveScheduling.SetAsync(false, owner._adaptiveScheduling.Current.Actual?.Preference ?? SelectedPreference, SelectedDuration));
            Switch = Action("切换偏好（不续期）", async (_, _) => await owner._adaptiveScheduling.SetAsync(true, SelectedPreference, SelectedDuration));
            commands.Children.Add(Enable); commands.Children.Add(Disable); commands.Children.Add(Switch); Root.Children.Add(commands);
            foreach (var group in new[] { (GroupIds.Gpu, "GPU"), (GroupIds.Sensors, "温度") })
            {
                var row = new AdaptiveGroupView(group.Item2); Groups.Add(group.Item1, row); Root.Children.Add(row.Root);
            }
            Root.Children.Add(Text("条形表示配置请求频率相对于基础频率", 10, Muted));
            Refresh = Action("刷新实际状态", async (_, _) => await owner._adaptiveScheduling.QueryAsync());
            Root.Children.Add(Refresh); Root.Children.Add(Error);
            var detailRows = new StackPanel(); detailRows.Children.Add(Recommendations); Impact.Margin = new Thickness(0, 8, 0, 0); detailRows.Children.Add(Impact);
            var details = new Expander { Header = "具体建议", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 7, 0, 7), Content = detailRows };
            AutomationProperties.SetName(details, "展开具体建议"); Root.Children.Add(details);
            Scope.Margin = new Thickness(0, 5, 0, 0); Root.Children.Add(Scope);
        }
        public void SynchronizePreference(AdaptiveSchedulingContract? actual)
        {
            if (actual is null) return;
            if (!_preferenceDirty) SelectedPreference = actual.Preference;
            if (actual.Preference == SelectedPreference) _preferenceDirty = false;
        }
    }

    private sealed class AdaptiveGroupView
    {
        public StackPanel Root { get; } = new() { Margin = new Thickness(0, 8, 0, 5) };
        private readonly string _name;
        private readonly TextBlock _period = Text("", 12, Primary);
        private readonly TextBlock _state = Text("", 10, Muted);
        private readonly ProgressBar _frequency = new() { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 5, 0, 3), Background = Line, BorderThickness = new Thickness(0) };
        public AdaptiveGroupView(string name)
        { _name = name; Root.Children.Add(_period); Root.Children.Add(_frequency); Root.Children.Add(_state); }
        public void Update(AdaptiveSchedulingGroupContract? group, bool confirmed)
        {
            if (group is null)
            {
                _period.Text = _name + " · " + (confirmed ? "未注册" : "周期未确认");
                _frequency.Value = 0; _frequency.Foreground = Muted; _state.Text = ""; return;
            }
            _period.Text = $"{_name}  {group.BasePeriodMs / 1000d:0.##} 秒 → " + (group.Paused ? "暂停" : $"{group.EffectivePeriodMs / 1000d:0.##} 秒");
            var percent = group.EffectivePeriodMs > 0 ? Math.Clamp(100d * group.BasePeriodMs / group.EffectivePeriodMs, 0, 100) : 0;
            _frequency.Value = group.Paused ? 0 : percent;
            _frequency.Foreground = group.Paused ? Warning : group.EffectivePeriodMs > group.BasePeriodMs ? Paint("#77D9C8") : Paint("#82AFF7");
            _state.Text = group.Paused ? "手动暂停优先 · 无请求" : $"配置频率约 {percent:0}% · " + (group.EffectivePeriodMs > group.BasePeriodMs ? "已延长查询周期" : "基础周期");
        }
    }
}

internal sealed record DesktopAdaptiveSchedulingState(bool Connected, bool Busy, bool? Supported, AdaptiveSchedulingContract? Actual, double? RemainingSeconds, string? Error);

internal interface IDesktopAdaptiveSchedulingConnection : IAsyncDisposable
{
    string InstanceId { get; }
    bool Supported { get; }
    Task<AdaptiveSchedulingContract> QueryAsync(CancellationToken token);
    Task<AdaptiveSchedulingContract> SetAsync(AdaptiveSchedulingRequestContract request, CancellationToken token);
}

/// <summary>One bounded operation at a time; confirmed state belongs to the raw connected Agent session.</summary>
internal sealed class DesktopAdaptiveSchedulingSession
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IDesktopAdaptiveSchedulingConnection>> _connect;
    private readonly TimeProvider _time;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private DesktopConnectionState _connection = new(DesktopConnectionStatus.Starting, null, 0, null, null);
    private CancellationTokenSource? _operationCancellation;
    private Task? _pending;
    private bool _busy;
    private bool? _supported;
    private AdaptiveSchedulingContract? _actual;
    private long _revision, _confirmedAt;
    private string? _error;
    private bool _stopped;
    private bool _stoppingDisposed;

    internal DesktopAdaptiveSchedulingSession(PipeEndpoint endpoint) : this(async token => new PipeConnection(await NamedPipeAgentClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(3), token))) { }
    internal DesktopAdaptiveSchedulingSession(Func<CancellationToken, Task<IDesktopAdaptiveSchedulingConnection>> connect, TimeProvider? time = null, TimeSpan? operationTimeout = null)
    { _connect = connect; _time = time ?? TimeProvider.System; _operationTimeout = operationTimeout ?? OperationTimeout; }
    internal event Action? StateChanged;
    internal DesktopAdaptiveSchedulingState Current
    {
        get
        {
            lock (_gate)
            {
                var remaining = _actual?.RemainingSeconds is { } seconds ? Math.Max(0, seconds - _time.GetElapsedTime(_confirmedAt).TotalSeconds) : (double?)null;
                return new(_connection.Status == DesktopConnectionStatus.Connected && !_stopped, _busy, _supported, _actual, remaining, _error);
            }
        }
    }
    internal void UpdateConnection(DesktopConnectionState state)
    {
        lock (_gate)
        {
            if (_stopped) return;
            var changed = _connection.Status != state.Status || _connection.InstanceId != state.InstanceId || _connection.RestartCount != state.RestartCount;
            _connection = state;
            if (!changed) return;
            _revision++; _actual = null; _supported = null; _error = "连接已变化 · 请刷新实际状态";
            _operationCancellation?.Cancel();
        }
        StateChanged?.Invoke();
    }
    internal Task QueryAsync() => StartAsync(null, AdaptiveSchedulingPreferences.Responsiveness, AdaptiveSchedulingLimits.DefaultDurationSeconds);
    internal Task SetAsync(bool enabled, string preference, int seconds) => StartAsync(enabled, preference, seconds);
    private Task StartAsync(bool? enabled, string preference, int seconds)
    {
        lock (_gate)
        {
            if (_stopped || _busy) return _pending ?? Task.CompletedTask;
            if (_connection.Status != DesktopConnectionStatus.Connected || _connection.InstanceId is null)
            { _actual = null; _error = "等待连接后刷新实际状态"; StateChanged?.Invoke(); return Task.CompletedTask; }
            if (enabled is not null && (_actual is not { Supported: true } || _actual.InstanceId != _connection.InstanceId))
            { _error = "实际状态未确认 · 请先刷新"; StateChanged?.Invoke(); return Task.CompletedTask; }
            if (!AdaptiveSchedulingPreferences.All.Contains(preference) || !AdaptiveSchedulingLimits.DurationSeconds.Contains(seconds))
                throw new ArgumentException("adaptive_scheduling_invalid_selection");
            _busy = true; _error = null;
            var revision = _revision; var instance = _connection.InstanceId;
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _operationCancellation.CancelAfter(_operationTimeout);
            return _pending = RunOperationAsync(enabled, preference, seconds, revision, instance, _operationCancellation);
        }
    }
    private async Task RunOperationAsync(bool? enabled, string preference, int seconds, long revision, string instance, CancellationTokenSource cancellation)
    {
        StateChanged?.Invoke();
        var token = cancellation.Token;
        try
        {
            await using var client = await _connect(token).WaitAsync(token).ConfigureAwait(false);
            EnsureCurrent(revision, instance, client.InstanceId, token);
            AdaptiveSchedulingContract response;
            if (!client.Supported) response = AdaptiveSchedulingContract.Unsupported(instance);
            else if (enabled is { } desired)
                response = await client.SetAsync(new() { InstanceId = instance, Enabled = desired, Preference = preference, DurationSeconds = seconds }, token).WaitAsync(token).ConfigureAwait(false);
            else response = await client.QueryAsync(token).WaitAsync(token).ConfigureAwait(false);
            EnsureCurrent(revision, instance, response.InstanceId, token);
            ValidateResponse(response);
            lock (_gate)
            {
                EnsureCurrent(revision, instance, response.InstanceId, token);
                _actual = response; _supported = response.Supported; _confirmedAt = _time.GetTimestamp(); _error = null;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException or IpcProtocolException or IpcRemoteException)
        {
            lock (_gate)
            {
                _actual = null;
                if (!_stopped) _error = _revision != revision ? "连接已变化 · 请刷新实际状态"
                    : exception is OperationCanceledException or TimeoutException ? "查询或操作超时 · 结果未确认，请刷新实际状态" : "操作未确认 · 请刷新实际状态后重试";
            }
        }
        finally
        {
            lock (_gate) { _busy = false; _operationCancellation = null; cancellation.Dispose(); }
            StateChanged?.Invoke();
        }
    }
    private void EnsureCurrent(long revision, string instance, string responseInstance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_stopped || revision != _revision || _connection.Status != DesktopConnectionStatus.Connected || instance != _connection.InstanceId || instance != responseInstance)
                throw new IpcProtocolException("adaptive_scheduling_connection_changed");
    }
    private static void ValidateResponse(AdaptiveSchedulingContract response)
    {
        var remaining = response.RemainingSeconds;
        if (!response.SessionOnly || !AdaptiveSchedulingPreferences.All.Contains(response.Preference) ||
            remaining is { } seconds && (!double.IsFinite(seconds) || seconds < 0) ||
            response.Enabled && (!response.Supported || remaining is null or <= 0 || response.ExpiresAtUtc is null) ||
            response.Groups is null || response.Recommendations is null || string.IsNullOrWhiteSpace(response.Reason) ||
            response.Groups.Count > 2 ||
            response.Groups.Any(group => group is null || group.GroupId is not (GroupIds.Gpu or GroupIds.Sensors) || group.BasePeriodMs <= 0 || group.EffectivePeriodMs <= 0) ||
            response.Groups.Select(group => group.GroupId).Distinct(StringComparer.Ordinal).Count() != response.Groups.Count)
            throw new IpcProtocolException("adaptive_scheduling_state_invalid");
    }
    internal async Task StopAsync()
    {
        Task? pending;
        lock (_gate)
        {
            if (_stopped) { pending = _pending; }
            else { _stopped = true; _stopping.Cancel(); _operationCancellation?.Cancel(); _actual = null; pending = _pending; }
        }
        if (pending is not null) await pending.ConfigureAwait(false);
        lock (_gate) { if (!_stoppingDisposed) { _stoppingDisposed = true; _stopping.Dispose(); } }
    }
    private sealed class PipeConnection(NamedPipeAgentClient client) : IDesktopAdaptiveSchedulingConnection
    {
        public string InstanceId => client.InstanceId;
        public bool Supported => client.Capabilities.Endpoints.ContainsKey("adaptiveScheduling");
        public Task<AdaptiveSchedulingContract> QueryAsync(CancellationToken token) => client.GetAdaptiveSchedulingAsync(token);
        public Task<AdaptiveSchedulingContract> SetAsync(AdaptiveSchedulingRequestContract request, CancellationToken token) => client.SetAdaptiveSchedulingAsync(request, token);
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
