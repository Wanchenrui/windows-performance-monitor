using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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
using PerfMonitor.Diagnostics;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public enum ResourceView { Cpu, Memory, Network, Disk }
public enum DashboardPanel { Processes, Diagnostics, Quality, LightMode }

public sealed partial class MainWindow : Window
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
    private readonly SelectedProcessTrendBuffer _processTrend = new();
    private readonly WatchedProcessList _watchedProcesses = new();
    private string? _watchMessage;
    private ProcessDrawerView? _processView;
    private readonly StackPanel _qualityRowsRoot = new();
    private readonly Dictionary<string, CollectionQualityView> _qualityRows = [];
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
    private readonly Border _trendSurface = new() { Background = Raised, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 16, 22, 16) };
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
    private string? _diagnosticEventId;
    private string? _qualityGroupId;
    private LightModeContract? _lightMode;
    private string? _lightError;
    private bool? _lightSupported;
    private bool _queryBusy, _lightBusy, _expanded;
    private Task? _sessionTask, _stopTask, _queryTask, _lightTask;

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
        _title.TextWrapping = TextWrapping.NoWrap; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _hardware.Cursor = Cursors.Hand;
        _hardware.ToolTip = "点击查看 GPU / 温度 / 电源的采集路径与帮助";
        _hardware.MouseLeftButtonUp += (_, _) => { _qualityGroupId = GroupIds.Gpu; OpenPanel(DashboardPanel.Quality); };
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _expand = Action("趋势与细节  ↗", (_, _) => ToggleDetails());
        Content = BuildContent();
        Loaded += (_, _) => StartSession();
        Closed += async (_, _) => await StopSessionAsync();
        StateChanged += (_, _) => UpdateVisibility(); IsVisibleChanged += (_, _) => UpdateVisibility();
    }
    internal void StartSession()
    {
        Dispatcher.VerifyAccess();
        if (_stopTask is not null) return;
        _renderer.Publish(_session.Current);
        UpdateVisibility();
        _sessionTask ??= _session.RunAsync(_stopping.Token);
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
        try { await Task.WhenAll(_sessionTask ?? Task.CompletedTask, _queryTask ?? Task.CompletedTask, _lightTask ?? Task.CompletedTask, _exportTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
    public void ToggleDetails()
    {
        _expanded = !_expanded; _details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _drawer.Visibility = Visibility.Collapsed; _panel = null;
        UpdateDrawerLayout();
        MinHeight = _expanded ? 560 : 190; Height = _expanded ? 680 : 200; Width = _expanded ? 1000 : 760;
        _expand.Content = _expanded ? "紧凑视图  ↙" : "趋势与细节  ↗";
        if (_expanded) Reveal(_details); RefreshCharts();
    }
    public void SelectResource(ResourceView resource)
    {
        _processTrend.Clear();
        _selected = resource; if (!_expanded) ToggleDetails();
        _drawer.Visibility = Visibility.Collapsed; _panel = null; RenderState(_state);
    }
    public void OpenPanel(DashboardPanel panel)
    {
        if (panel != DashboardPanel.Processes) _processTrend.Clear();
        if (!_expanded) ToggleDetails(); _panel = panel; _drawer.Visibility = Visibility.Visible;
        RenderState(_state); Reveal(_drawer); if (panel == DashboardPanel.Diagnostics) _ = QueryDiagnosticsAsync();
        if (panel == DashboardPanel.LightMode) _ = QueryLightModeAsync();
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
        var surface = _trendSurface;
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = new GridLength(108) }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = new GridLength(112) });
        var title = new StackPanel(); foreach (var text in new[] { _title, _legend, _value, _quality }) title.Children.Add(text); heading.Children.Add(title);
        Grid.SetColumn(_heroRing, 1); heading.Children.Add(_heroRing); body.Children.Add(heading); Grid.SetRow(_chart, 1); _chart.MinHeight = 140; body.Children.Add(_chart);
        var footer = new StackPanel();
        var navigation = new WrapPanel();
        var ranges = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var seconds in new[] { 60, 300 })
        {
            var button = Action(seconds == 60 ? "最近 60 秒" : "最近 5 分钟", (_, _) => { _chart.WindowSeconds = seconds; RenderState(_state); });
            _ranges[seconds] = button; ranges.Children.Add(button);
        }
        navigation.Children.Add(ranges); var panels = new WrapPanel();
        panels.Children.Add(Action("进程", (_, _) => OpenPanel(DashboardPanel.Processes))); panels.Children.Add(Action("诊断", (_, _) => OpenPanel(DashboardPanel.Diagnostics))); panels.Children.Add(Action("质量", (_, _) => OpenPanel(DashboardPanel.Quality)));
        panels.Children.Add(Action("轻量模式", (_, _) => OpenPanel(DashboardPanel.LightMode)));
        navigation.Children.Add(panels); footer.Children.Add(navigation); footer.Children.Add(BuildTrendExportControls());
        Grid.SetRow(footer, 2); body.Children.Add(footer); surface.Child = body; _details.Children.Add(surface);
        var drawerRoot = new Grid(); drawerRoot.RowDefinitions.Add(new() { Height = GridLength.Auto }); drawerRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel(); var close = Action("×", (_, _) => { _drawer.Visibility = Visibility.Collapsed; _panel = null; UpdateDrawerLayout(); }); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(_drawerTitle); drawerRoot.Children.Add(header);
        var scroll = new ScrollViewer { Content = _drawerRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true, Margin = new Thickness(0, 14, 0, 0) };
        scroll.Resources[typeof(ScrollBar)] = DarkScrollBarStyle();
        _drawerRows.MaxWidth = 284;
        Grid.SetRow(scroll, 1); drawerRoot.Children.Add(scroll); _drawer.Child = drawerRoot; _details.Children.Add(_drawer);
    }
    private void OnStateChanged(DesktopConnectionState state)
    {
        TrackExportConnection(state);
        _renderer.Publish(state);
    }
    private void UpdateVisibility()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized; _renderer.SetEnabled(visible);
        if (!visible) { _trends.BreakContinuity(); _processTrend.BreakContinuity(); _details.BeginAnimation(OpacityProperty, null); _drawer.BeginAnimation(OpacityProperty, null); }
    }
    private void RenderState(DesktopConnectionState state)
    {
        UpdateDrawerLayout();
        var previousInstance = _state.InstanceId;
        _state = state; _trends.Observe(state); _processTrend.Observe(state);
        if (previousInstance != state.InstanceId || _lightMode is not null && _lightMode.InstanceId != state.InstanceId)
        { _lightMode = null; _lightSupported = null; _lightError = "Agent 实例已更换 · 请刷新模式状态"; }
        var historical = state.LatestSnapshot is not null && (state.Status != DesktopConnectionStatus.Connected || state.InstanceId != state.LatestSnapshot.InstanceId);
        _status.Text = state.Status == DesktopConnectionStatus.Connected && !historical ? "已连接" : state.LatestSnapshot is null ? "正在连接" : "历史快照 · 等待重连";
        _dot.Foreground = state.Status == DesktopConnectionStatus.Connected && !historical ? Paint("#77D9C8") : Warning;
        _status.ToolTip = state.InstanceId is null ? "等待本机 Agent" : "当前实例 " + state.InstanceId;
        foreach (var resource in ResourceSpecs)
        {
            var value = Value(resource.First, resource.Group); var quality = SnapshotPresentation.Group(state, resource.Group);
            MarkSelected(_selectors[resource.View], _expanded && _processTrend.Identity is null && resource.View == _selected);
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
        if (!historical && _state.LatestSnapshot is { } currentSnapshot &&
            currentSnapshot.Groups.GetValueOrDefault(GroupIds.Gpu)?.CollectionState == "paused" &&
            currentSnapshot.Groups.GetValueOrDefault(GroupIds.Sensors)?.CollectionState == "paused")
            _hardware.Text = "轻量模式 · GPU 与温度已暂停  ·  " + PowerText();
        _hardware.ToolTip = "所有可读 GPU 的最大负载 / 最高温度\n" + SnapshotPresentation.CardQuality(state, GroupIds.Gpu, MetricIds.GpuLoadMaxPercent, MetricIds.GpuTemperatureMaxCelsius) + "\n全部可读传感器\n" + SnapshotPresentation.CardQuality(state, GroupIds.Sensors, MetricIds.HardwareTemperatureMaxCelsius);
        if (_processTrend.Identity is not null)
        {
            var process = CurrentSelectedProcess();
            var cpu = _state.LatestSnapshot?.Groups.GetValueOrDefault(GroupIds.Processes)?.Freshness == FreshnessStates.Fresh ? process?.Cpu : null;
            _title.Text = _processTrend.Name;
            _legend.Text = "进程 CPU · 整机归一化 · 选中后记录";
            _legend.ToolTip = "仅所选进程实例 · 从选中后收到的新观测开始 · 缺测留空";
            _value.Text = Percent(cpu); _quality.Text = _processTrend.Status;
            _quality.Foreground = cpu is null ? Warning : Muted;
            _heroRing.Visibility = Visibility.Visible; _heroRing.Accent = Tint("#C5DB74"); _heroRing.Update(cpu, historical);
        }
        RefreshCharts(); UpdateExportControls(); if (_panel is not null) PopulateDrawer();
    }
    private void UpdateDrawerLayout()
    {
        var open = _drawer.Visibility == Visibility.Visible;
        _trendSurface.Margin = new Thickness(0, 0, open ? 334 : 0, 0);
        MinWidth = open ? 900 : 680;
    }
    private void RefreshCharts()
    {
        foreach (var resource in ResourceSpecs.Where(resource => resource.Second is not null)) _mini[resource.View].Update(_trends.Points(resource.First), _trends.Points(resource.Second!), _trends.EndElapsedSeconds);
        if (_processTrend.Identity is not null)
        {
            _chart.Accent = Tint("#C5DB74"); _chart.Percent = true; _chart.PrimaryName = _processTrend.Name;
            _chart.Update(_processTrend.Points, [], _processTrend.EndElapsedSeconds); return;
        }
        var selected = ResourceSpecs.First(resource => resource.View == _selected); _chart.Accent = Tint(selected.Color); _chart.Percent = selected.Second is null;
        _chart.SecondaryAccent = SecondaryTint(selected.View);
        _chart.PrimaryName = selected.View == ResourceView.Network ? "下载" : selected.View == ResourceView.Disk ? "读取" : selected.Name; _chart.SecondaryName = selected.View == ResourceView.Network ? "上传" : "写入";
        _chart.Update(_trends.Points(selected.First), selected.Second is null ? [] : _trends.Points(selected.Second), _trends.EndElapsedSeconds);
    }
    private void PopulateDrawer()
    {
        if (_panel == DashboardPanel.Processes) { PopulateProcessDrawer(); return; }
        if (_panel == DashboardPanel.Quality) { PopulateQualityDrawer(); return; }
        if (_panel == DashboardPanel.Diagnostics) { PopulateDiagnosticsDrawer(); return; }
        _drawerRows.Children.Clear();
        if (_panel == DashboardPanel.LightMode)
        {
            _drawerTitle.Text = "轻量模式";
            var refresh = Action(_lightBusy ? "处理中…" : "刷新状态", async (_, _) => await QueryLightModeAsync());
            refresh.IsEnabled = !_lightBusy; _drawerRows.Children.Add(refresh);
            if (_lightSupported == false)
                Row("当前 Agent 不支持", "请使用支持轻量模式的 Agent", "未发送模式查询或修改请求", Muted);
            else if (_lightMode is { } mode && mode.InstanceId == _state.InstanceId)
            {
                var paused = string.Join(" / ", mode.PausedGroups.Select(group => group == GroupIds.Gpu ? "GPU" : group == GroupIds.Sensors ? "温度" : group));
                var modeStatus = paused.Length > 0 ? "已暂停 " + paused : mode.Enabled ? "当前没有可选硬件组" : "未启用轻量模式";
                Row("可选硬件采集", modeStatus, mode.ImpactDescription, mode.Enabled ? Paint("#77D9C8") : Muted);
                var toggle = Action(mode.Enabled ? "恢复原设置" : "开启轻量模式", async (_, _) => await QueryLightModeAsync(!mode.Enabled));
                toggle.IsEnabled = !_lightBusy && mode.Supported && (!mode.Enabled || mode.CanRestore) && _state.Status == DesktopConnectionStatus.Connected;
                _drawerRows.Children.Add(toggle);
                _drawerRows.Children.Add(Text("仅本次 Agent 会话有效\nCPU / 内存 / 网络 / 磁盘继续采集；不修改其他程序或系统设置。", 11, Muted));
            }
            else Row("等待模式状态", "连接后刷新可用状态", "不会自动开启", Muted);
            if (_lightError is not null) Row("操作状态", _lightError, "", Warning);
        }
    }
    private Task QueryDiagnosticsAsync()
    {
        if (_queryBusy || _exportBusy || _stopTask is not null) return _queryTask ?? Task.CompletedTask;
        return _queryTask = QueryDiagnosticsCoreAsync();
    }
    private Task QueryLightModeAsync(bool? enabled = null)
    {
        if (_lightBusy || _stopTask is not null) return Task.CompletedTask;
        return _lightTask = QueryLightModeCoreAsync(enabled);
    }
    private async Task QueryLightModeCoreAsync(bool? enabled)
    {
        _lightBusy = true; _lightError = null;
        if (_panel == DashboardPanel.LightMode) PopulateDrawer();
        var queryToken = _stopping.Token;
        try
        {
            await using var client = await NamedPipeAgentClient.ConnectAsync(_endpoint, TimeSpan.FromSeconds(3), queryToken);
            if (client.InstanceId != _state.InstanceId)
            { _lightError = "Agent 实例已变化 · 请等待重连后刷新"; return; }
            _lightSupported = client.Capabilities.Endpoints.ContainsKey("lightMode");
            if (_lightSupported != true) { _lightMode = null; return; }
            if (enabled is not null && (_lightMode is null || _lightMode.InstanceId != client.InstanceId))
            { _lightError = "模式状态已过期 · 请先刷新"; return; }
            var mode = enabled is { } desired ? await client.SetLightModeAsync(desired, queryToken) : await client.GetLightModeAsync(queryToken);
            if (mode.InstanceId != client.InstanceId || client.InstanceId != _state.InstanceId ||
                _state.Status != DesktopConnectionStatus.Connected)
            { _lightMode = null; _lightSupported = null; _lightError = "连接实例已变化 · 请刷新实际状态"; return; }
            _lightMode = mode;
        }
        catch (OperationCanceledException) { _lightMode = null; if (_stopTask is null) _lightError = "连接已变化 · 请刷新后重试"; }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or IpcProtocolException or IpcRemoteException)
        { _lightMode = null; _lightError = "操作未确认 · 请刷新实际状态后重试"; }
        finally
        {
            _lightBusy = false;
            if (_stopTask is null && _panel == DashboardPanel.LightMode) PopulateDrawer();
        }
    }
    private async Task QueryDiagnosticsCoreAsync()
    {
        if (_queryBusy || _stopTask is not null) return; _queryBusy = true; _diagnosticQueryError = null;
        var queryToken = _stopping.Token;
        var connection = CaptureExportConnection();
        var seconds = SelectedDiagnosticSeconds();
        if (_panel == DashboardPanel.Diagnostics) PopulateDrawer();
        try
        {
            await using var client = await NamedPipeAgentClient.ConnectAsync(_endpoint, TimeSpan.FromSeconds(3), queryToken);
            EnsureExportConnection(connection, client.InstanceId);
            var query = CreateDiagnosticRange(seconds);
            var response = await client.QueryDiagnosticsAsync(query, queryToken);
            EnsureExportConnection(connection, response.InstanceId);
            EnsureDiagnosticRangeResponse(query, response);
            _diagnostics = response;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (ExportConnectionChangedException) { _diagnosticQueryError = "连接已变化 · 查询未确认，请刷新"; }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or IpcProtocolException or IpcRemoteException)
        { _diagnosticQueryError = "诊断暂不可用 · 等待连接后重试"; }
        finally { _queryBusy = false; UpdateExportControls(); if (_stopTask is null && _panel == DashboardPanel.Diagnostics) PopulateDrawer(); }
    }
    private void Row(string title, string status, string detail, Brush color, Panel? target = null)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; row.Children.Add(Text(title, 12, Primary, FontWeights.SemiBold));
        row.Children.Add(Text(status, 11, color)); if (detail.Length > 0) row.Children.Add(Text(detail, 10, Muted)); (target ?? _drawerRows).Children.Add(row);
    }
    private void DiagnosticRow(DiagnosticEventContract item, Panel target)
    {
        var current = _state.Status == DesktopConnectionStatus.Connected && _diagnostics?.InstanceId == _state.InstanceId &&
            item.InstanceId == _state.InstanceId && item.ObservationSequence is > 0;
        var status = !current ? "历史记录" : item.State == DiagnosticStates.Resolved ? "已恢复" : "活动告警";
        var color = current && item.State == DiagnosticStates.Active ? Warning : Muted;
        var content = new StackPanel { Width = 246, Margin = new Thickness(0, 3, 0, 5) };
        content.Children.Add(Text(RuleName(item.RuleId) + (_diagnosticEventId == item.EventId ? "  ⌄" : "  ›"), 12, Primary, FontWeights.SemiBold));
        content.Children.Add(Text($"{status} · {item.LastSeenUtc.ToLocalTime():MM-dd HH:mm:ss}", 10, color));
        var button = Action(content, (_, _) => { _diagnosticEventId = _diagnosticEventId == item.EventId ? null : item.EventId; PopulateDrawer(); });
        button.ToolTip = "查看依据、可能原因与建议";
        MarkSelected(button, _diagnosticEventId == item.EventId); target.Children.Add(button);
        if (_diagnosticEventId != item.EventId) return;
        var explanation = DiagnosticExplainer.Explain(item,
            _state.Status == DesktopConnectionStatus.Connected && _state.LatestSnapshot?.InstanceId == _state.InstanceId ? _state.LatestSnapshot : null);
        Row("现象", explanation.Phenomenon, "", Primary, target);
        Row("依据", explanation.Evidence, "", Muted, target);
        Row("可能原因", explanation.PossibleCauses, "", Muted, target);
        Row("可以怎么做", explanation.Suggestions, "仅建议，不会直接修改系统", Paint("#77D9C8"), target);
        if (!string.IsNullOrWhiteSpace(explanation.Limitation)) Row("数据限制", explanation.Limitation, "", Warning, target);
    }
    private void PopulateQualityDrawer()
    {
        _drawerTitle.Text = "采集质量";
        if (!_drawerRows.Children.Contains(_qualityRowsRoot))
        { _drawerRows.Children.Clear(); _drawerRows.Children.Add(_qualityRowsRoot); }
        var groups = SnapshotPresentation.Groups(_state); var visible = groups.Select(group => group.GroupId).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _qualityRows.Keys.Where(key => !visible.Contains(key)).ToArray())
        { _qualityRowsRoot.Children.Remove(_qualityRows[key].Root); _qualityRows.Remove(key); }
        foreach (var item in groups)
        {
            if (!_qualityRows.TryGetValue(item.GroupId, out var row))
            { row = new(this, item.GroupId); _qualityRows.Add(item.GroupId, row); _qualityRowsRoot.Children.Add(row.Root); }
            var selected = _qualityGroupId == item.GroupId;
            row.Name.Text = item.Name + (selected ? "  ⌄" : "  ›"); row.Status.Text = item.Status; row.Status.Foreground = item.IsHealthy ? Muted : Warning;
            row.Button.ToolTip = item.Detail + "\n查看已尝试的方法与具体帮助";
            AutomationProperties.SetName(row.Button, item.Name + " · " + item.Status + " · 查看采集帮助"); MarkSelected(row.Button, selected);
            row.Help.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (!selected) continue;
            var group = _state.LatestSnapshot?.Groups.GetValueOrDefault(item.GroupId);
            var help = CollectionGuidance.Explain(item.GroupId, group);
            var lines = new List<string>();
            if (_state.LatestSnapshot is not null && (_state.Status != DesktopConnectionStatus.Connected || _state.LatestSnapshot.InstanceId != _state.InstanceId))
                lines.Add("历史观测 · 连接后再确认当前状态");
            lines.Add(help.Status); lines.Add("已尝试：" + help.Attempts);
            if (help.Suggestions.Count > 0) lines.Add("可以怎么做：\n" + string.Join("\n", help.Suggestions.Select(suggestion => "• " + suggestion)));
            if (!string.IsNullOrWhiteSpace(help.Limitation)) lines.Add("限制：" + help.Limitation);
            row.Help.Text = string.Join("\n\n", lines);
        }
    }
    private sealed class CollectionQualityView
    {
        public StackPanel Root { get; } = new();
        public TextBlock Name { get; } = Text("", 12, Primary, FontWeights.SemiBold);
        public TextBlock Status { get; } = Text("", 11, Muted);
        public TextBlock Help { get; } = Text("", 11, Muted);
        public Button Button { get; }
        public CollectionQualityView(MainWindow owner, string groupId)
        {
            var content = new StackPanel { Width = 246, Margin = new Thickness(0, 3, 0, 4) }; content.Children.Add(Name); content.Children.Add(Status);
            Button = Action(content, (_, _) => { owner._qualityGroupId = owner._qualityGroupId == groupId ? null : groupId; owner.PopulateDrawer(); });
            Root.Children.Add(Button); Help.Margin = new Thickness(9, 4, 9, 12); Root.Children.Add(Help);
        }
    }
    private void PopulateProcessDrawer()
    {
        var view = _processView ??= new ProcessDrawerView(this);
        if (!_drawerRows.Children.Contains(view.Root))
        { _drawerRows.Children.Clear(); _drawerRows.Children.Add(view.Root); }
        var watched = _watchedProcesses.Present(_state);
        view.Count.Text = $"本次运行的关注列表  {watched.Count}/{WatchedProcessList.MaxEntries}";
        view.Empty.Visibility = watched.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        view.Message.Text = _watchMessage ?? ""; view.Message.Visibility = _watchMessage is null ? Visibility.Collapsed : Visibility.Visible;
        var watchKeys = new HashSet<WatchedProcess>();
        for (var index = 0; index < watched.Count; index++)
        {
            var item = watched[index]; watchKeys.Add(item.Entry);
            if (!view.Watched.TryGetValue(item.Entry, out var row))
            {
                row = new ProcessDrawerRow(this, item.Entry, true); view.Watched.Add(item.Entry, row); view.WatchGrid.Children.Add(row.Root);
            }
            row.Update(item.Process, item.Status, item.CanSelect, true);
            MarkSelected(row.Select, item.CanSelect && _processTrend.Identity == item.Entry.Identity && _processTrend.AgentInstanceId == item.Entry.AgentInstanceId);
            Grid.SetRow(row.Root, index);
            KeyboardNavigation.SetTabIndex(row.Root, index);
        }
        foreach (var key in view.Watched.Keys.Where(key => !watchKeys.Contains(key)).ToArray())
        { view.WatchGrid.Children.Remove(view.Watched[key].Root); view.Watched.Remove(key); }
        if (_processTrend.Identity is { } identity)
        {
            _drawerTitle.Text = "进程详情"; view.Ranking.Visibility = Visibility.Collapsed; view.Details.Visibility = Visibility.Visible;
            var process = CurrentSelectedProcess(); string created;
            try { created = DateTime.FromFileTimeUtc(identity.CreationTimeTicks).ToLocalTime().ToString("MM-dd HH:mm:ss"); }
            catch (ArgumentOutOfRangeException) { created = "创建时间未知"; }
            view.Name.Text = _processTrend.Name; view.Identity.Text = $"PID {identity.Pid} · {created}";
            view.Status.Text = _processTrend.Status; view.Memory.Text = $"私有 {Bytes(process?.PrivateBytes)} · 工作集 {Bytes(process?.WorkingSetBytes)}";
            var pinned = _watchedProcesses.Contains(_processTrend.AgentInstanceId, identity);
            view.Pin.Content = pinned ? "取消关注此实例" : "关注此实例";
            AutomationProperties.SetName(view.Pin, (pinned ? "取消关注 " : "关注 ") + _processTrend.Name + $" PID {identity.Pid}");
            view.Pin.IsEnabled = pinned || process is not null && WatchedProcessList.IsCurrentObservation(_state);
            return;
        }
        _drawerTitle.Text = "进程活动"; view.Ranking.Visibility = Visibility.Visible; view.Details.Visibility = Visibility.Collapsed;
        var quality = SnapshotPresentation.Group(_state, GroupIds.Processes); view.Quality.Text = quality.Status; view.QualityDetail.Text = quality.Detail;
        var top = SnapshotPresentation.ProcessRows(_state);
        view.NoRows.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rankKeys = new HashSet<WatchedProcess>();
        for (var index = 0; index < top.Count; index++)
        {
            var item = top[index];
            // Unidentified rows cannot be selected or pinned; their slot keys never impersonate a known instance.
            var key = new WatchedProcess(_state.LatestSnapshot?.InstanceId ?? "", item.Identity ?? new(-index - 1, 0), "");
            rankKeys.Add(key);
            if (!view.Ranked.TryGetValue(key, out var row))
            { row = new ProcessDrawerRow(this, key, false); view.Ranked.Add(key, row); view.RankGrid.Children.Add(row.Root); }
            var pinned = item.Identity is { } known && _watchedProcesses.Contains(key.AgentInstanceId, known);
            row.Update(item, "", item.Identity is not null && _state.Status == DesktopConnectionStatus.Connected &&
                _state.InstanceId == _state.LatestSnapshot?.InstanceId, pinned);
            Grid.SetRow(row.Root, index);
            KeyboardNavigation.SetTabIndex(row.Root, index);
        }
        foreach (var key in view.Ranked.Keys.Where(key => !rankKeys.Contains(key)).ToArray())
        { view.RankGrid.Children.Remove(view.Ranked[key].Root); view.Ranked.Remove(key); }
    }

    private sealed class ProcessDrawerView
    {
        public StackPanel Root { get; } = new();
        public StackPanel Ranking { get; } = new();
        public StackPanel Details { get; } = new();
        public Grid WatchGrid { get; } = new();
        public Grid RankGrid { get; } = new();
        public Dictionary<WatchedProcess, ProcessDrawerRow> Watched { get; } = [];
        public Dictionary<WatchedProcess, ProcessDrawerRow> Ranked { get; } = [];
        public TextBlock Count { get; } = Text("", 12, Primary, FontWeights.SemiBold);
        public TextBlock Empty { get; } = Text("在排行点 ☆，或在详情中关注。", 11, Muted);
        public TextBlock Message { get; } = Text("", 11, Warning);
        public TextBlock Quality { get; } = Text("", 11, Muted);
        public TextBlock QualityDetail { get; } = Text("", 10, Muted);
        public TextBlock NoRows { get; } = Text("等待新的可读进程观测", 12, Muted);
        public TextBlock Name { get; } = Text("", 12, Primary, FontWeights.SemiBold);
        public TextBlock Identity { get; } = Text("", 11, Muted);
        public TextBlock Status { get; } = Text("", 10, Muted);
        public TextBlock Memory { get; } = Text("", 11, Muted);
        public Button Pin { get; }
        public ProcessDrawerView(MainWindow owner)
        {
            for (var index = 0; index < WatchedProcessList.MaxEntries; index++)
            { WatchGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); RankGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
            Root.Children.Add(Count); var hint = Text("固定实例 · 不额外采集 · 重开 Desktop 不保留", 10, Muted);
            hint.Margin = new Thickness(0, 3, 0, 8); Root.Children.Add(hint); Root.Children.Add(Empty); Root.Children.Add(WatchGrid); Root.Children.Add(Message);
            Root.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 9, 0, 12) });
            Ranking.Children.Add(Text("CPU 占用排行 · 最多 12 项", 12, Primary, FontWeights.SemiBold)); Ranking.Children.Add(Quality); Ranking.Children.Add(QualityDetail);
            RankGrid.Margin = new Thickness(0, 10, 0, 0); Ranking.Children.Add(RankGrid); Ranking.Children.Add(NoRows); Root.Children.Add(Ranking);
            Details.Children.Add(Action("← 返回 CPU 排行", (_, _) => { owner._processTrend.Clear(); owner.RenderState(owner._state); }));
            Details.Children.Add(Name); Details.Children.Add(Identity); Details.Children.Add(Status);
            Pin = Action("关注此实例", (_, _) =>
            {
                if (owner._processTrend.Identity is { } identity)
                    owner.ToggleWatch(owner._processTrend.AgentInstanceId, new(owner._processTrend.Name, null, identity));
            });
            Details.Children.Add(Pin); Details.Children.Add(Text("内存", 12, Primary, FontWeights.SemiBold)); Details.Children.Add(Memory);
            Details.Children.Add(Text("来自当前进程快照，不新增扫描", 10, Muted));
            Details.Children.Add(Text("左侧显示所选实例 CPU 曲线\n从选中后的新观测开始，最长保留 5 分钟；缺测和隐藏期间留空。", 11, Muted)); Root.Children.Add(Details);
        }
    }

    private sealed class ProcessDrawerRow
    {
        private readonly MainWindow _owner;
        private readonly WatchedProcess _entry;
        private readonly bool _watched;
        private ProcessPresentation? _process;
        private readonly TextBlock _name = Text("", 12, Primary), _cpu = Text("", 11, Muted), _status = Text("", 10, Muted);
        private readonly Grid _bar = new() { Height = 3, Background = Line, Margin = new Thickness(0, 5, 0, 0) };
        public Grid Root { get; } = new();
        public Button Select { get; }
        private readonly Button _pin;
        public ProcessDrawerRow(MainWindow owner, WatchedProcess entry, bool watched)
        {
            _owner = owner; _entry = entry; _watched = watched;
            var content = new StackPanel { Width = 202, Margin = new Thickness(0, 2, 0, watched ? 2 : 8) }; var heading = new DockPanel();
            _cpu.MinWidth = 46; _cpu.TextAlignment = TextAlignment.Right; DockPanel.SetDock(_cpu, Dock.Right); heading.Children.Add(_cpu);
            _name.TextWrapping = TextWrapping.NoWrap; _name.TextTrimming = TextTrimming.CharacterEllipsis; heading.Children.Add(_name); content.Children.Add(heading);
            if (watched) content.Children.Add(_status);
            else
            {
                _bar.ColumnDefinitions.Add(new() { Width = new GridLength(0, GridUnitType.Star) }); _bar.ColumnDefinitions.Add(new() { Width = new GridLength(100, GridUnitType.Star) });
                _bar.Children.Add(new Border { Background = Paint("#C5DB74"), CornerRadius = new CornerRadius(1) }); content.Children.Add(_bar);
            }
            Select = Action(content, (_, _) => { if (_process is { } process) owner.SelectProcess(process); });
            _pin = Action(watched ? "×" : "☆", (_, _) =>
            {
                if (watched) { owner._watchedProcesses.Remove(entry.AgentInstanceId, entry.Identity); owner._watchMessage = null; owner.PopulateDrawer(); }
                else if (_process is { } process) owner.ToggleWatch(entry.AgentInstanceId, process);
            });
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Root.Children.Add(Select); Grid.SetColumn(_pin, 1); Root.Children.Add(_pin);
        }
        public void Update(ProcessPresentation? process, string status, bool canSelect, bool pinned)
        {
            _process = process; var name = process?.Name ?? _entry.Name; _name.Text = name; _name.ToolTip = name;
            _cpu.Text = Percent(process?.Cpu); _cpu.Foreground = process?.Cpu is null ? Muted : Paint("#C5DB74");
            _status.Text = $"PID {_entry.Identity.Pid} · {status}"; Select.IsEnabled = canSelect;
            var identityLabel = process?.Identity is null && !_watched ? "实例身份未知" : $"PID {_entry.Identity.Pid}";
            AutomationProperties.SetName(Select, name + " " + identityLabel + (_watched ? " · " + status : " · 查看详情与趋势"));
            AutomationProperties.SetName(_pin, (_watched || pinned ? "取消关注 " : "关注 ") + name + " " + identityLabel);
            Select.ToolTip = name + $"\nPID {_entry.Identity.Pid} · " + (_watched ? status + "\n切换后仅记录此实例的新观测" : "点击查看此实例详情与趋势");
            if (_watched) { _pin.ToolTip = "取消关注 " + name; return; }
            _pin.Content = pinned ? "★" : "☆"; _pin.ToolTip = pinned ? "取消关注此实例" : "关注此实例 · 仅本次运行";
            _pin.IsEnabled = process?.Identity is not null && (pinned || WatchedProcessList.IsCurrentObservation(_owner._state));
            var cpu = Math.Clamp(process?.Cpu ?? 0, 0, 100); _bar.ColumnDefinitions[0].Width = new GridLength(cpu, GridUnitType.Star); _bar.ColumnDefinitions[1].Width = new GridLength(100 - cpu, GridUnitType.Star);
            _bar.ToolTip = process?.Cpu is null ? "CPU 缺测" : $"整机归一化 CPU：{process.Cpu:0.0}%";
        }
    }
    private void ToggleWatch(string? instanceId, ProcessPresentation process)
    {
        if (process.Identity is not { } identity || instanceId is null) return;
        if (_watchedProcesses.Contains(instanceId, identity))
        { _watchedProcesses.Remove(instanceId, identity); _watchMessage = null; }
        else if (instanceId == _state.InstanceId && _watchedProcesses.TryPin(_state, process)) _watchMessage = null;
        else _watchMessage = _watchedProcesses.Entries.Count >= WatchedProcessList.MaxEntries
            ? "已关注 12 条 · 先取消一条再添加" : "当前观测不可确认 · 请等待更新";
        PopulateDrawer();
    }
    private void SelectProcess(ProcessPresentation item)
    {
        _processTrend.Select(_state, item); RenderState(_state);
    }
    private ProcessPresentation? CurrentSelectedProcess() => _processTrend.Identity is { } identity &&
        _state.Status == DesktopConnectionStatus.Connected && _state.InstanceId == _processTrend.AgentInstanceId &&
        _state.LatestSnapshot?.InstanceId == _processTrend.AgentInstanceId
        ? SnapshotPresentation.FindProcess(_state, identity) : null;
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
    private static string Bytes(long? value) => value is null ? "—" : $"{value.Value / 1048576d:0.#} MiB";
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
