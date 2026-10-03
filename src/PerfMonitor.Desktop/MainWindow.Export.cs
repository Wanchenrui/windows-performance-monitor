using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using PerfMonitor.Contracts;
using PerfMonitor.Diagnostics;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public sealed partial class MainWindow
{
    private readonly object _exportConnectionGate = new();
    private DesktopConnectionStatus _exportConnectionStatus = DesktopConnectionStatus.Starting;
    private string? _exportConnectionInstanceId;
    private long _exportConnectionRevision;
    private bool _exportBusy;
    private Task? _exportTask;
    private CancellationTokenSource? _exportCancellation;
    private Button? _trendExportButton, _trendExportCancel;
    private readonly TextBlock _trendExportStatus = Text("", 10, Muted);
    private DiagnosticsDrawerView? _diagnosticsView;
    private string? _diagnosticQueryError;

    private UIElement BuildTrendExportControls()
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var commands = new WrapPanel();
        _trendExportButton = Action("导出趋势 JSON", async (_, _) => await ExportRangeAsync(false));
        _trendExportButton.ToolTip = "导出当前图形的所选范围 · 仅 Desktop 已收到的缓冲，不补历史";
        AutomationProperties.SetName(_trendExportButton, "导出当前趋势为 JSON");
        _trendExportCancel = Action("取消导出", (_, _) => CancelRangeExport());
        _trendExportCancel.Visibility = Visibility.Collapsed;
        commands.Children.Add(_trendExportButton); commands.Children.Add(_trendExportCancel);
        root.Children.Add(commands); _trendExportStatus.Margin = new Thickness(9, 0, 4, 0);
        _trendExportStatus.Visibility = Visibility.Collapsed; root.Children.Add(_trendExportStatus);
        return root;
    }

    private void PopulateDiagnosticsDrawer()
    {
        var view = _diagnosticsView ??= new(this);
        _drawerTitle.Text = "诊断事件";
        if (!_drawerRows.Children.Contains(view.Root))
        { _drawerRows.Children.Clear(); _drawerRows.Children.Add(view.Root); }
        view.Summary.Text = _diagnostics is null ? "按需查询本机诊断" : SnapshotPresentation.DiagnosticsSummary(_diagnostics, _state);
        view.QueryRange.Text = _diagnostics?.Query is { FromEpochMs: { } from, ToEpochMs: { } to }
            ? $"上次查询：{DateTimeOffset.FromUnixTimeMilliseconds(from).ToLocalTime():MM-dd HH:mm:ss} — {DateTimeOffset.FromUnixTimeMilliseconds(to).ToLocalTime():MM-dd HH:mm:ss}"
            : "尚未查询";
        view.QueryStatus.Text = _diagnosticQueryError ?? "";
        view.QueryStatus.Visibility = _diagnosticQueryError is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateExportControls();
        // Export controls keep their identity and focus while received snapshots refresh the drawer.
        var selectedEvent = _diagnostics?.Events.FirstOrDefault(item => item.EventId == _diagnosticEventId);
        var limitation = selectedEvent is null ? null : DiagnosticExplainer.Explain(selectedEvent,
            _state.Status == DesktopConnectionStatus.Connected && _state.LatestSnapshot?.InstanceId == _state.InstanceId ? _state.LatestSnapshot : null).Limitation;
        var key = (_diagnostics, _diagnosticEventId, _state.Status, _state.InstanceId, limitation);
        if (view.Rendered == key) return;
        view.Rendered = key; view.Events.Children.Clear();
        if (_diagnostics is not null)
            foreach (var item in _diagnostics.Events.GroupBy(item => (item.InstanceId, item.RuleId, item.SubjectId))
                .Select(group => group.MaxBy(item => item.ObservationSequence ?? 0)!).Reverse().Take(12))
                DiagnosticRow(item, view.Events);
    }

    private int SelectedDiagnosticSeconds() => _diagnosticsView?.SelectedSeconds ?? 86400;
    private static DiagnosticQueryContract CreateDiagnosticRange(int seconds)
    {
        var now = DateTimeOffset.UtcNow;
        return new() { FromEpochMs = now.AddSeconds(-seconds).ToUnixTimeMilliseconds(), ToEpochMs = now.ToUnixTimeMilliseconds(), MaxEvents = DiagnosticPolicyLimits.MaxEvents };
    }

    private Task ExportRangeAsync(bool diagnostics)
    {
        if (_exportBusy || _stopTask is not null || diagnostics && _queryBusy) return _exportTask ?? Task.CompletedTask;
        _exportBusy = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        lock (_exportConnectionGate) _exportCancellation = cancellation;
        return _exportTask = ExportRangeCoreAsync(diagnostics, cancellation.Token);
    }

    private async Task ExportRangeCoreAsync(bool diagnostics, CancellationToken token)
    {
        var connection = CaptureExportConnection();
        var seconds = diagnostics ? SelectedDiagnosticSeconds() : (int)_chart.WindowSeconds;
        string? outputPath = null;
        var fileSaved = false;
        SetExportStatus(diagnostics, "正在选择保存位置…", false); UpdateExportControls();
        try
        {
            EnsureExportConnection(connection);
            if (_state.InstanceId != connection.InstanceId || _state.Status != connection.Status)
                throw new ExportConnectionChangedException();
            DesktopRangeExportEnvelope? frozen = null;
            if (!diagnostics)
                frozen = _processTrend.Identity is null
                    ? DesktopRangeExport.CreateTrend(_trends, _selected, seconds, _state, DateTimeOffset.UtcNow)
                    : DesktopRangeExport.CreateProcessTrend(_processTrend, seconds, _state, DateTimeOffset.UtcNow);
            else if (connection.Status != DesktopConnectionStatus.Connected)
                throw new ExportConnectionChangedException();
            var dialog = new SaveFileDialog
            {
                Title = diagnostics ? "导出诊断范围" : "导出当前趋势范围",
                Filter = "JSON 文件 (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
                OverwritePrompt = true, CheckPathExists = true,
                FileName = $"perfmonitor-{(diagnostics ? "diagnostics" : "trend")}-{seconds}s-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            };
            if (dialog.ShowDialog(this) != true) { SetExportStatus(diagnostics, "已取消导出", false); return; }
            outputPath = dialog.FileName; token.ThrowIfCancellationRequested(); EnsureExportConnection(connection);
            if (diagnostics)
            {
                SetExportStatus(true, "正在查询所选范围…", false);
                await using var client = await NamedPipeAgentClient.ConnectAsync(_endpoint, TimeSpan.FromSeconds(3), token);
                EnsureExportConnection(connection, client.InstanceId);
                var query = CreateDiagnosticRange(seconds);
                var response = await client.QueryDiagnosticsAsync(query, token);
                EnsureExportConnection(connection, response.InstanceId);
                EnsureDiagnosticRangeResponse(query, response);
                frozen = DesktopRangeExport.CreateDiagnostics(response, DateTimeOffset.UtcNow, storageAvailable: null);
            }
            token.ThrowIfCancellationRequested(); EnsureExportConnection(connection);
            SetExportStatus(diagnostics, "正在保存 JSON…", false);
            var saved = frozen ?? throw new InvalidOperationException("export_data_missing");
            await DesktopRangeExport.WriteAsync(outputPath, saved, token);
            fileSaved = true; EnsureExportConnection(connection);
            if (saved.Diagnostics?.Response is { } savedDiagnostics)
                SetExportStatus(true, $"已保存 {savedDiagnostics.EventCount} 条记录 · " +
                    (savedDiagnostics.Truncated ? "结果已截断，请缩小范围 · " : "") +
                    "存储覆盖未知，空结果不代表没有诊断", savedDiagnostics.Truncated, outputPath);
            else
                SetExportStatus(false, $"已保存 {saved.Trend?.Series.Sum(series => series.PointCount) ?? 0} 个观测点 · 仅当前图形的缓冲范围", false, outputPath);
        }
        catch (ExportConnectionChangedException)
        { SetExportStatus(diagnostics, fileSaved ? "文件已保存 · 连接已变化，内容以文件中捕获时点为准" : "连接已变化 · 导出未确认，请重新导出", true, outputPath); }
        catch (OperationCanceledException)
        {
            var changed = !IsExportConnectionCurrent(connection);
            SetExportStatus(diagnostics, fileSaved ? "文件已保存 · 操作已结束，内容以文件中捕获时点为准" :
                changed ? "连接已变化 · 导出未确认，请重新导出" : "已取消导出", changed || fileSaved, outputPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or IpcProtocolException or IpcRemoteException or ArgumentException or InvalidOperationException or NotSupportedException)
        { SetExportStatus(diagnostics, ExportFailureText(exception), true, outputPath); }
        finally
        {
            lock (_exportConnectionGate) { _exportCancellation?.Dispose(); _exportCancellation = null; }
            _exportBusy = false; UpdateExportControls();
        }
    }

    private void CancelRangeExport()
    {
        lock (_exportConnectionGate) _exportCancellation?.Cancel();
    }
    private static void EnsureDiagnosticRangeResponse(DiagnosticQueryContract query, DiagnosticsContract response)
    {
        if (response.Query.FromEpochMs != query.FromEpochMs || response.Query.ToEpochMs != query.ToEpochMs ||
            response.Query.MaxEvents != query.MaxEvents || !response.Query.RuleIds.SequenceEqual(query.RuleIds) ||
            !response.Query.States.SequenceEqual(query.States))
            throw new IpcProtocolException("diagnostic_query_mismatch");
    }
    private static string ExportFailureText(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "导出失败 · 没有写入权限，请更换保存位置",
        TimeoutException or IpcProtocolException or IpcRemoteException => "诊断查询失败 · 等待连接后重试",
        InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException => "导出失败 · 数据或文件类型无法保存，请刷新后重试",
        _ => "导出失败 · 请检查连接或保存位置后重试",
    };
    private void SetExportStatus(bool diagnostics, string text, bool warning, string? outputPath = null)
    {
        var status = diagnostics ? (_diagnosticsView ??= new(this)).ExportStatus : _trendExportStatus;
        status.Text = text; status.Foreground = warning ? Warning : Muted;
        status.Visibility = Visibility.Visible; status.ToolTip = outputPath;
    }
    private void UpdateExportControls()
    {
        if (_trendExportButton is not null)
        {
            _trendExportButton.IsEnabled = !_exportBusy && _stopTask is null;
            _trendExportButton.ToolTip = $"{(_processTrend.Identity is null ? "当前资源" : "所选进程实例")} · 最近 {(_chart.WindowSeconds == 60 ? "60 秒" : "5 分钟")} · 仅已有缓冲";
        }
        if (_trendExportCancel is not null) _trendExportCancel.Visibility = _exportBusy ? Visibility.Visible : Visibility.Collapsed;
        if (_diagnosticsView is not { } view) return;
        foreach (var button in view.RangeButtons.Values) button.IsEnabled = !_exportBusy && !_queryBusy && _stopTask is null;
        view.Refresh.IsEnabled = !_exportBusy && !_queryBusy && _stopTask is null;
        view.Refresh.Content = _queryBusy ? "查询中…" : "刷新列表";
        view.Export.IsEnabled = !_exportBusy && !_queryBusy && _stopTask is null;
        view.Cancel.Visibility = _exportBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record ExportConnection(long Revision, DesktopConnectionStatus Status, string? InstanceId, long RestartCount);
    private sealed class ExportConnectionChangedException : Exception { }
    private ExportConnection CaptureExportConnection()
    {
        lock (_exportConnectionGate)
        {
            var state = _session.Current;
            return new(_exportConnectionRevision, state.Status, state.InstanceId, state.RestartCount);
        }
    }
    private bool IsExportConnectionCurrent(ExportConnection connection)
    {
        lock (_exportConnectionGate)
        {
            var state = _session.Current;
            return _exportConnectionRevision == connection.Revision && state.Status == connection.Status &&
                state.InstanceId == connection.InstanceId && state.RestartCount == connection.RestartCount;
        }
    }
    private void EnsureExportConnection(ExportConnection connection, string? responseInstanceId = null)
    {
        if (!IsExportConnectionCurrent(connection) || responseInstanceId is not null &&
            (responseInstanceId != connection.InstanceId || connection.Status != DesktopConnectionStatus.Connected))
            throw new ExportConnectionChangedException();
    }
    private void TrackExportConnection(DesktopConnectionState state)
    {
        lock (_exportConnectionGate)
        {
            if (_exportConnectionStatus == state.Status && _exportConnectionInstanceId == state.InstanceId) return;
            _exportConnectionStatus = state.Status; _exportConnectionInstanceId = state.InstanceId;
            _exportConnectionRevision++; _exportCancellation?.Cancel();
        }
    }

    private sealed class DiagnosticsDrawerView
    {
        public StackPanel Root { get; } = new();
        public StackPanel Events { get; } = new();
        public Dictionary<int, Button> RangeButtons { get; } = [];
        public int SelectedSeconds { get; private set; } = 86400;
        public Button Refresh { get; }
        public Button Export { get; }
        public Button Cancel { get; }
        public TextBlock Summary { get; } = Text("", 12, Muted);
        public TextBlock QueryRange { get; } = Text("", 10, Muted);
        public TextBlock QueryStatus { get; } = Text("", 11, Warning);
        public TextBlock ExportStatus { get; } = Text("", 10, Muted);
        public (DiagnosticsContract?, string?, DesktopConnectionStatus, string?, string?)? Rendered { get; set; }
        public DiagnosticsDrawerView(MainWindow owner)
        {
            Root.Children.Add(Text("按事件最后观测时间筛选", 11, Muted));
            var ranges = new WrapPanel { Margin = new Thickness(0, 4, 0, 7) };
            foreach (var item in new[] { (60, "60 秒"), (300, "5 分钟"), (3600, "1 小时"), (86400, "24 小时") })
            {
                var button = Action(item.Item2, (_, _) => SelectRange(item.Item1));
                AutomationProperties.SetName(button, "诊断查询与导出范围 · 最近 " + item.Item2);
                RangeButtons.Add(item.Item1, button); ranges.Children.Add(button);
            }
            SelectRange(SelectedSeconds); Root.Children.Add(ranges);
            var commands = new WrapPanel();
            Refresh = Action("刷新列表", async (_, _) => await owner.QueryDiagnosticsAsync());
            Export = Action("导出 JSON", async (_, _) => await owner.ExportRangeAsync(true));
            Export.ToolTip = "重新查询所选范围 · 最多 2000 条 · 保留原始证据";
            AutomationProperties.SetName(Export, "重新查询所选诊断范围并导出 JSON");
            Cancel = Action("取消导出", (_, _) => owner.CancelRangeExport()); Cancel.Visibility = Visibility.Collapsed;
            commands.Children.Add(Refresh); commands.Children.Add(Export); commands.Children.Add(Cancel); Root.Children.Add(commands);
            ExportStatus.Visibility = Visibility.Collapsed; ExportStatus.Margin = new Thickness(0, 4, 0, 6); Root.Children.Add(ExportStatus);
            Root.Children.Add(Text("闭区间 · 最多 2000 条\n记录不代表当前状态或完整生命周期\n存储覆盖未知，空结果不代表没有诊断", 10, Muted));
            QueryRange.Margin = new Thickness(0, 8, 0, 3); Root.Children.Add(QueryRange); Root.Children.Add(QueryStatus); Root.Children.Add(Summary);
            Events.Margin = new Thickness(0, 8, 0, 0); Root.Children.Add(Events);
        }
        private void SelectRange(int seconds)
        {
            SelectedSeconds = seconds;
            foreach (var (range, button) in RangeButtons)
            {
                var selected = range == seconds;
                MarkSelected(button, selected);
                AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
            }
        }
    }
}
