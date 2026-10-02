using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public enum ResourceView { Cpu, Memory, Network, Disk }
public enum DashboardPanel { Processes, Diagnostics, Quality }

public sealed class MainWindow : Window
{
    private static readonly Brush Surface = Paint("#171A1E"), Raised = Paint("#1D2227"), Primary = Paint("#E8EDF1"),
        Muted = Paint("#8D99A5"), Line = Paint("#30373E"), Warning = Paint("#DDBB78");
    private static readonly (ResourceView View, string Name, string Group, string First, string? Second, string Legend, string Color)[] ResourceSpecs =
    [
        (ResourceView.Cpu, "CPU", GroupIds.SystemCpu, MetricIds.SystemCpuUtilization, null, "整机归一化利用率", "#C5DB74"),
        (ResourceView.Memory, "内存", GroupIds.Memory, MetricIds.MemoryUtilization, null, "物理内存利用率", "#77D9C8"),
        (ResourceView.Network, "网络", GroupIds.Network, MetricIds.NetworkReceiveBytesPerSecond, MetricIds.NetworkSendBytesPerSecond, "下载 / 上传 · 活动非回环网卡", "#82AFF7"),
        (ResourceView.Disk, "磁盘", GroupIds.DiskIo, MetricIds.DiskReadBytesPerSecond, MetricIds.DiskWriteBytesPerSecond, "读取 / 写入 · 所有物理磁盘", "#C1A3F0"),
    ];
    private readonly PipeEndpoint _endpoint;
    private readonly DesktopAgentSession _session;
    private readonly CancellationTokenSource _stopping = new();
    private readonly LatestStateRenderer<DesktopConnectionState> _renderer;
    private readonly DesktopTrendBuffer _trends = new();
    private readonly Dictionary<ResourceView, Button> _selectors = [];
    private readonly Dictionary<ResourceView, TextBlock> _readouts = [];
    private readonly Dictionary<ResourceView, TextBlock> _qualities = [];
    private readonly Dictionary<ResourceView, LoadRing> _rings = [];
    private readonly Dictionary<ResourceView, TrendChart> _mini = [];
    private readonly Dictionary<int, Button> _ranges = [];
    private readonly TextBlock _status = Text("正在连接", 11, Muted), _dot = Text("●", 10, Warning),
        _hardware = Text("GPU 与温度 · 等待数据", 10, Muted), _resident = Text("从托盘呼出 / 退出", 10, Muted),
        _title = Text("CPU", 25, Primary, FontWeights.SemiBold), _value = Text("—", 28, Primary, FontWeights.SemiBold),
        _legend = Text("整机归一化利用率", 11, Muted), _quality = Text("等待新观测", 11, Muted);
    private readonly TrendChart _chart = new();
    private readonly LoadRing _heroRing = new() { Width = 100, Height = 100 };
    private readonly Grid _details = new() { Visibility = Visibility.Collapsed };
    private readonly Border _drawer = new() { Visibility = Visibility.Collapsed, Width = 320, Background = Raised,
        BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
        Padding = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _drawerRows = new();
    private readonly TextBlock _drawerTitle = Text("", 19, Primary, FontWeights.SemiBold);
    private readonly Button _expand;
    private DesktopConnectionState _state;
    private ResourceView _selected;
    private DashboardPanel? _panel;
    private DiagnosticsContract? _diagnostics;
    private bool _queryBusy, _expanded;
    private Task? _sessionTask, _stopTask, _queryTask;

    public MainWindow(PipeEndpoint endpoint)
    {
        _endpoint = endpoint; _session = new(endpoint); _state = _session.Current;
        _renderer = new(callback => { _ = Dispatcher.InvokeAsync(callback, DispatcherPriority.Background); }, RenderState);
        _session.StateChanged += OnStateChanged;
        Title = "PerfMonitor · 性能状态"; Width = 760; Height = 200; MinWidth = 680; MinHeight = 190;
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(5),
            CornerRadius = new CornerRadius(12), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        Background = Surface; Foreground = Primary; FontFamily = new("Microsoft YaHei UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _expand = Action("趋势与细节  ↗", (_, _) => ToggleDetails());
        Content = BuildContent();
        Loaded += (_, _) => { if (_stopTask is null) { _renderer.Publish(_session.Current); UpdateVisibility(); _sessionTask ??= _session.RunAsync(_stopping.Token); } };
        Closed += async (_, _) => await StopSessionAsync();
        StateChanged += (_, _) => UpdateVisibility(); IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    public void SetResidentStatus(string text)
    {
        _resident.Text = text.Contains("占用", StringComparison.Ordinal) || text.Contains("不可用", StringComparison.Ordinal)
            ? text.Split('·')[0].Trim() : "Ctrl+Alt+P 显示/隐藏";
        _resident.ToolTip = text;
    }
    public Task StopSessionAsync() => _stopTask ??= StopSessionCoreAsync();
    private async Task StopSessionCoreAsync()
    {
        _renderer.Close(); _session.StateChanged -= OnStateChanged; _stopping.Cancel();
        try { await Task.WhenAll(_sessionTask ?? Task.CompletedTask, _queryTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
    public void ToggleDetails()
    {
        _expanded = !_expanded; _details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _drawer.Visibility = Visibility.Collapsed; _panel = null;
        MinHeight = _expanded ? 560 : 190; Height = _expanded ? 680 : 200; Width = _expanded ? 1000 : 760;
        _expand.Content = _expanded ? "紧凑视图  ↙" : "趋势与细节  ↗";
        if (_expanded) Reveal(_details); RefreshCharts();
    }
    public void SelectResource(ResourceView resource)
    {
        _selected = resource; if (!_expanded) ToggleDetails();
        _drawer.Visibility = Visibility.Collapsed; _panel = null; RenderState(_state);
    }
    public void OpenPanel(DashboardPanel panel)
    {
        if (!_expanded) ToggleDetails(); _panel = panel; _drawer.Visibility = Visibility.Visible;
        PopulateDrawer(); Reveal(_drawer); if (panel == DashboardPanel.Diagnostics) _ = QueryDiagnosticsAsync();
    }
    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(20, 12, 20, 12) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text("PERFMONITOR", 14, Primary, FontWeights.SemiBold));
        _dot.Margin = new Thickness(16, 0, 5, 0); head.Children.Add(_dot); head.Children.Add(_status); header.Children.Add(head);
        var commands = new StackPanel { Orientation = Orientation.Horizontal }; commands.Children.Add(_expand);
        commands.Children.Add(Action("收起  −", (_, _) => Close())); Grid.SetColumn(commands, 1); header.Children.Add(commands); root.Children.Add(header);
        var strip = new Grid { Height = 80, Margin = new Thickness(0, 10, 0, 8) };
        for (var i = 0; i < ResourceSpecs.Length; i++)
        {
            var resource = ResourceSpecs[i]; strip.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var content = new Grid { Margin = new Thickness(8, 5, 8, 5) };
            if (resource.Second is null)
            {
                content.ColumnDefinitions.Add(new() { Width = new GridLength(64) }); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var ring = new LoadRing { Width = 62, Height = 62, Accent = Tint(resource.Color) }; _rings[resource.View] = ring; content.Children.Add(ring);
                var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                labels.Children.Add(Text(resource.Name, 13, Primary, FontWeights.SemiBold));
                var quality = Text("等待", 10, Muted); labels.Children.Add(quality); _qualities[resource.View] = quality; Grid.SetColumn(labels, 1); content.Children.Add(labels);
            }
            else
            {
                content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new() { Height = new GridLength(31) }); content.RowDefinitions.Add(new() { Height = GridLength.Auto });
                var labels = new StackPanel { Orientation = Orientation.Horizontal };
                labels.Children.Add(Text(resource.Name + "   ", 12, Primary));
                labels.Children.Add(Text(resource.View == ResourceView.Network ? "↓  " : "读  ", 11, Paint(resource.Color)));
                labels.Children.Add(Text(resource.View == ResourceView.Network ? "↑" : "写", 11, new SolidColorBrush(SecondaryTint(resource.View))));
                content.Children.Add(labels);
                var mini = new TrendChart { ShowAxes = false, Percent = false, Accent = Tint(resource.Color), SecondaryAccent = SecondaryTint(resource.View) };
                _mini[resource.View] = mini; Grid.SetRow(mini, 1); content.Children.Add(mini);
                var value = Text("等待数据", 10, Muted); value.TextWrapping = TextWrapping.NoWrap; value.TextTrimming = TextTrimming.CharacterEllipsis;
                _readouts[resource.View] = value; Grid.SetRow(value, 2); content.Children.Add(value);
            }
            var selector = Action(content, (_, _) => SelectResource(resource.View)); selector.Padding = new Thickness(0);
            _selectors[resource.View] = selector; Grid.SetColumn(selector, i); strip.Children.Add(selector);
            if (i > 0)
            {
                var divider = new Border { Width = 1, Background = Line, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 16), IsHitTestVisible = false };
                Grid.SetColumn(divider, i); strip.Children.Add(divider);
            }
        }
        Grid.SetRow(strip, 1); root.Children.Add(strip); BuildDetails(); Grid.SetRow(_details, 2); root.Children.Add(_details);
        var footer = new DockPanel();
        DockPanel.SetDock(_resident, Dock.Right); _resident.Margin = new Thickness(10, 2, 0, 1); footer.Children.Add(_resident);
        _resident.TextWrapping = TextWrapping.NoWrap;
        _hardware.TextWrapping = TextWrapping.NoWrap; _hardware.TextTrimming = TextTrimming.CharacterEllipsis;
        _hardware.Margin = new Thickness(4, 2, 0, 1); footer.Children.Add(_hardware);
        Grid.SetRow(footer, 3); root.Children.Add(footer); return root;
    }
    private void BuildDetails()
    {
        _details.Margin = new Thickness(0, 4, 0, 10);
        var surface = new Border { Background = Raised, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 16, 22, 16) };
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = new GridLength(108) }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = new GridLength(112) });
        var title = new StackPanel(); foreach (var text in new[] { _title, _legend, _value, _quality }) title.Children.Add(text); heading.Children.Add(title);
        Grid.SetColumn(_heroRing, 1); heading.Children.Add(_heroRing); body.Children.Add(heading); Grid.SetRow(_chart, 1); _chart.MinHeight = 140; body.Children.Add(_chart);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var ranges = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var seconds in new[] { 60, 300 })
        {
            var button = Action(seconds == 60 ? "最近 60 秒" : "最近 5 分钟", (_, _) => { _chart.WindowSeconds = seconds; RenderState(_state); });
            _ranges[seconds] = button; ranges.Children.Add(button);
        }
        footer.Children.Add(ranges); var panels = new StackPanel { Orientation = Orientation.Horizontal };
        panels.Children.Add(Action("进程", (_, _) => OpenPanel(DashboardPanel.Processes))); panels.Children.Add(Action("诊断", (_, _) => OpenPanel(DashboardPanel.Diagnostics))); panels.Children.Add(Action("质量", (_, _) => OpenPanel(DashboardPanel.Quality)));
        Grid.SetColumn(panels, 1); footer.Children.Add(panels); Grid.SetRow(footer, 2); body.Children.Add(footer); surface.Child = body; _details.Children.Add(surface);
        var drawerRoot = new Grid(); drawerRoot.RowDefinitions.Add(new() { Height = GridLength.Auto }); drawerRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel(); var close = Action("×", (_, _) => { _drawer.Visibility = Visibility.Collapsed; _panel = null; }); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(_drawerTitle); drawerRoot.Children.Add(header);
        var scroll = new ScrollViewer { Content = _drawerRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, Margin = new Thickness(0, 14, 0, 0) };
        scroll.Resources[typeof(ScrollBar)] = DarkScrollBarStyle();
        _drawerRows.MaxWidth = 284;
        Grid.SetRow(scroll, 1); drawerRoot.Children.Add(scroll); _drawer.Child = drawerRoot; _details.Children.Add(_drawer);
    }
    private void OnStateChanged(DesktopConnectionState state) => _renderer.Publish(state);
    private void UpdateVisibility()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized; _renderer.SetEnabled(visible);
        if (!visible) { _trends.BreakContinuity(); _details.BeginAnimation(OpacityProperty, null); _drawer.BeginAnimation(OpacityProperty, null); }
    }
    private void RenderState(DesktopConnectionState state)
    {
        _state = state; _trends.Observe(state);
        var historical = state.LatestSnapshot is not null && (state.Status != DesktopConnectionStatus.Connected || state.InstanceId != state.LatestSnapshot.InstanceId);
        _status.Text = state.Status == DesktopConnectionStatus.Connected && !historical ? "已连接" : state.LatestSnapshot is null ? "正在连接" : "历史快照 · 等待重连";
        _dot.Foreground = state.Status == DesktopConnectionStatus.Connected && !historical ? Paint("#77D9C8") : Warning;
        _status.ToolTip = state.InstanceId is null ? "等待本机 Agent" : "当前实例 " + state.InstanceId;
        foreach (var resource in ResourceSpecs)
        {
            var value = Value(resource.First, resource.Group); var quality = SnapshotPresentation.Group(state, resource.Group);
            MarkSelected(_selectors[resource.View], _expanded && resource.View == _selected);
            _selectors[resource.View].ToolTip = quality.Status + "\n" + quality.Detail + "\n点击查看最近变化";
            if (_rings.TryGetValue(resource.View, out var ring))
            {
                var stale = state.LatestSnapshot?.Groups.GetValueOrDefault(resource.Group)?.Freshness == FreshnessStates.Stale;
                ring.Update(value, historical || stale); _qualities[resource.View].Text = historical ? "历史" : stale ? "陈旧" : value is null ? "缺测" : quality.IsHealthy ? "实时" : "部分可读";
                _qualities[resource.View].Foreground = historical || value is null || stale ? Muted : Paint(resource.Color);
            }
            else _readouts[resource.View].Text = (historical ? "历史 · " : "") + Pair(value, Value(resource.Second!, resource.Group));
        }
        var selected = ResourceSpecs.First(resource => resource.View == _selected); var selectedQuality = SnapshotPresentation.Group(state, selected.Group);
        _title.Text = selected.Name;
        _legend.Text = (selected.View switch { ResourceView.Cpu => "整机 CPU 利用率", ResourceView.Memory => "物理内存利用率",
            ResourceView.Network => "总网络吞吐", _ => "总磁盘吞吐" }) + (_chart.WindowSeconds == 60 ? " · 最近 60 秒" : " · 最近 5 分钟");
        _legend.ToolTip = selected.Legend + "\n仅本次会话收到的新观测 · 缺测留空";
        foreach (var (seconds, button) in _ranges) MarkSelected(button, seconds == _chart.WindowSeconds);
        _value.Text = selected.Second is null ? Percent(Value(selected.First, selected.Group)) : Pair(Value(selected.First, selected.Group), Value(selected.Second, selected.Group));
        _quality.Text = selectedQuality.Status; _quality.Foreground = selectedQuality.IsHealthy ? Muted : Warning; _quality.ToolTip = selectedQuality.Detail;
        _heroRing.Visibility = selected.Second is null ? Visibility.Visible : Visibility.Collapsed; _heroRing.Accent = Tint(selected.Color); _heroRing.Update(Value(selected.First, selected.Group), historical);
        var gpu = Value(MetricIds.GpuLoadMaxPercent, GroupIds.Gpu); var temperature = Value(MetricIds.HardwareTemperatureMaxCelsius, GroupIds.Sensors);
        _hardware.Text = (historical ? "历史  ·  " : "") + (gpu is null && temperature is null ? "硬件读数暂不可用" :
            $"GPU  {(gpu is null ? "缺测" : Percent(gpu))}  ·  温度  {(temperature is null ? "缺测" : $"{temperature:0.#} °C")}") + "  ·  " + PowerText();
        _hardware.ToolTip = "所有可读 GPU 的最大负载 / 最高温度\n" + SnapshotPresentation.CardQuality(state, GroupIds.Gpu, MetricIds.GpuLoadMaxPercent, MetricIds.GpuTemperatureMaxCelsius) + "\n全部可读传感器\n" + SnapshotPresentation.CardQuality(state, GroupIds.Sensors, MetricIds.HardwareTemperatureMaxCelsius);
        RefreshCharts(); if (_panel is not null) PopulateDrawer();
    }
    private void RefreshCharts()
    {
        foreach (var resource in ResourceSpecs.Where(resource => resource.Second is not null)) _mini[resource.View].Update(_trends.Points(resource.First), _trends.Points(resource.Second!), _trends.EndElapsedSeconds);
        var selected = ResourceSpecs.First(resource => resource.View == _selected); _chart.Accent = Tint(selected.Color); _chart.Percent = selected.Second is null;
        _chart.SecondaryAccent = SecondaryTint(selected.View);
        _chart.PrimaryName = selected.View == ResourceView.Network ? "下载" : selected.View == ResourceView.Disk ? "读取" : selected.Name; _chart.SecondaryName = selected.View == ResourceView.Network ? "上传" : "写入";
        _chart.Update(_trends.Points(selected.First), selected.Second is null ? [] : _trends.Points(selected.Second), _trends.EndElapsedSeconds);
    }
    private void PopulateDrawer()
    {
        _drawerRows.Children.Clear();
        if (_panel == DashboardPanel.Quality)
        {
            _drawerTitle.Text = "采集质量";
            foreach (var group in SnapshotPresentation.Groups(_state)) Row(group.Name, group.Status, group.Detail, group.IsHealthy ? Muted : Warning);
        }
        else if (_panel == DashboardPanel.Processes)
        {
            _drawerTitle.Text = "进程活动"; var quality = SnapshotPresentation.Group(_state, GroupIds.Processes); Row("CPU 占用排行 · 最多 12 项", quality.Status, quality.Detail, Muted);
            if (_state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Processes)?.Data is JsonArray)
            {
                var top = SnapshotPresentation.ProcessRows(_state);
                foreach (var item in top) ProcessRow(item);
                if (top.Count == 0) _drawerRows.Children.Add(Text("尚无可读进程", 12, Muted));
            }
            else _drawerRows.Children.Add(Text("等待新的进程观测", 12, Muted));
        }
        else if (_panel == DashboardPanel.Diagnostics)
        {
            _drawerTitle.Text = "诊断事件"; _drawerRows.Children.Add(Action(_queryBusy ? "查询中…" : "刷新最近 24 小时", async (_, _) => await QueryDiagnosticsAsync()));
            _drawerRows.Children.Add(Text("最近 24 小时 · 仅上次查询结果", 10, Muted));
            var summary = Text(_diagnostics is null ? "按需查询本机诊断" : SnapshotPresentation.DiagnosticsSummary(_diagnostics, _state), 12, Muted);
            summary.MaxWidth = 276; _drawerRows.Children.Add(summary);
            if (_diagnostics is not null) foreach (var item in _diagnostics.Events.GroupBy(item => (item.InstanceId, item.RuleId, item.SubjectId))
                .Select(group => group.MaxBy(item => item.ObservationSequence ?? 0)!).Reverse().Take(12))
                Row(RuleName(item.RuleId), _state.Status != DesktopConnectionStatus.Connected || _diagnostics.InstanceId != _state.InstanceId ||
                    item.ObservationSequence is not > 0 || item.InstanceId != _state.InstanceId ? "历史记录" : item.State == DiagnosticStates.Resolved ? "已恢复" : "活动告警",
                    $"最后观测 {item.LastSeenUtc.ToLocalTime():MM-dd HH:mm:ss}", item.State == DiagnosticStates.Active ? Warning : Muted);
        }
    }
    private Task QueryDiagnosticsAsync()
    {
        if (_queryBusy || _stopTask is not null) return _queryTask ?? Task.CompletedTask;
        return _queryTask = QueryDiagnosticsCoreAsync();
    }
    private async Task QueryDiagnosticsCoreAsync()
    {
        if (_queryBusy || _stopTask is not null) return; _queryBusy = true;
        var queryToken = _stopping.Token;
        if (_panel == DashboardPanel.Diagnostics) PopulateDrawer();
        try
        {
            await using var client = await NamedPipeAgentClient.ConnectAsync(_endpoint, TimeSpan.FromSeconds(3), queryToken);
            var now = DateTimeOffset.UtcNow; _diagnostics = await client.QueryDiagnosticsAsync(new() { FromEpochMs = now.AddHours(-24).ToUnixTimeMilliseconds(), ToEpochMs = now.ToUnixTimeMilliseconds(), MaxEvents = 100 }, queryToken);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or TimeoutException or IpcProtocolException or IpcRemoteException)
        { if (_panel == DashboardPanel.Diagnostics) _drawerRows.Children.Add(Text("诊断暂不可用 · 等待连接后重试", 12, Warning)); }
        finally { _queryBusy = false; if (_stopTask is null && _panel == DashboardPanel.Diagnostics && _diagnostics is not null) PopulateDrawer(); }
    }
    private void Row(string title, string status, string detail, Brush color)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; row.Children.Add(Text(title, 12, Primary, FontWeights.SemiBold));
        row.Children.Add(Text(status, 11, color)); if (detail.Length > 0) row.Children.Add(Text(detail, 10, Muted)); _drawerRows.Children.Add(row);
    }
    private void ProcessRow(ProcessPresentation item)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
        var heading = new DockPanel();
        var value = Text(Percent(item.Cpu), 11, item.Cpu is null ? Muted : Paint("#C5DB74"));
        value.MinWidth = 46; value.TextAlignment = TextAlignment.Right; DockPanel.SetDock(value, Dock.Right); heading.Children.Add(value);
        var name = Text(item.Name, 12, Primary); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = item.Name;
        heading.Children.Add(name); row.Children.Add(heading);
        var bar = new Grid { Height = 3, Background = Line, Margin = new Thickness(0, 5, 0, 0) };
        var cpu = Math.Clamp(item.Cpu ?? 0, 0, 100);
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(cpu, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new() { Width = new GridLength(100 - cpu, GridUnitType.Star) });
        bar.Children.Add(new Border { Background = Paint("#C5DB74"), CornerRadius = new CornerRadius(1) });
        bar.ToolTip = item.Cpu is null ? "CPU 缺测" : $"整机归一化 CPU：{item.Cpu:0.0}%";
        row.Children.Add(bar); _drawerRows.Children.Add(row);
    }
    private static void MarkSelected(Button button, bool selected)
    {
        button.Tag = selected ? "selected" : null;
        button.Background = selected ? Paint("#293832") : Brushes.Transparent;
        button.Foreground = selected ? Primary : Muted;
    }
    private static Style DarkScrollBarStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ScrollBar}">
          <Setter Property="Width" Value="6"/><Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="{x:Type ScrollBar}">
            <Grid Background="Transparent"><Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}"
              Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
              <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" IsTabStop="False"/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="{x:Type Thumb}"><Border Background="#53606B" CornerRadius="3" Margin="1,0,0,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" IsTabStop="False"/></Track.IncreaseRepeatButton>
            </Track></Grid>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    private double? Value(string metric, string group) => _state.LatestSnapshot is { } snapshot ? SnapshotPresentation.ReadMetric(snapshot, group, metric) : null;
    private static string Percent(double? value) => value is null ? "—" : $"{value:0.#}%";
    private static string Pair(double? first, double? second) => (first is null ? "—" : TrendChart.FormatRate(first.Value)) + " / " + (second is null ? "—" : TrendChart.FormatRate(second.Value));
    private string PowerText()
    {
        var group = _state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Power);
        if (group?.Availability is not (AvailabilityStates.Available or AvailabilityStates.Partial)) return "电源状态未知";
        return ReadString((group.Data as JsonObject)?["powerSource"]) switch { "ac" => "交流电", "battery" => "电池供电", _ => "电源状态未知" };
    }
    private static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string RuleName(string rule) => rule switch
    {
        DiagnosticRuleIds.HighCpu => "CPU 持续繁忙", DiagnosticRuleIds.MemoryPressure => "内存压力", DiagnosticRuleIds.SystemDiskLow => "系统盘空间不足",
        DiagnosticRuleIds.ProcessCpuSpike => "进程 CPU 突增", DiagnosticRuleIds.SamplingGap => "采样中断", DiagnosticRuleIds.ProviderUnavailable => "采集暂不可用",
        DiagnosticRuleIds.AgentResourceAnomaly => "监测资源开销", _ => "诊断事件",
    };
    private void Reveal(FrameworkElement element)
    {
        if (SystemParameters.ClientAreaAnimation && IsVisible && WindowState != WindowState.Minimized)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
    }
    private static Color Tint(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color SecondaryTint(ResourceView view) => Tint(view == ResourceView.Network ? "#E7BA82" : "#77D9C8");
    private static Brush Paint(string hex)
    {
        var brush = new SolidColorBrush(Tint(hex)); brush.Freeze(); return brush;
    }
    private static TextBlock Text(string text, double size, Brush color, FontWeight? weight = null) => new()
    { Text = text, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Button Action(object content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, Foreground = Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(9, 5, 9, 5), Cursor = Cursors.Hand, FontSize = 11 };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.Click += click; button.MouseEnter += (_, _) => { if (button.Background == Brushes.Transparent) button.Background = Paint("#242B31"); };
        button.MouseLeave += (_, _) => button.Background = button.Tag as string == "selected" ? Paint("#293832") : Brushes.Transparent; return button;
    }
}
