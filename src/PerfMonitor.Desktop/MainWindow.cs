using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.Desktop;

public sealed class MainWindow : Window
{
    private static readonly Brush WindowBrush =
        new SolidColorBrush(Color.FromRgb(15, 23, 42));
    private static readonly Brush CardBrush =
        new SolidColorBrush(Color.FromRgb(30, 41, 59));
    private static readonly Brush PrimaryTextBrush =
        new SolidColorBrush(Color.FromRgb(241, 245, 249));
    private static readonly Brush SecondaryTextBrush =
        new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private static readonly Brush AccentBrush =
        new SolidColorBrush(Color.FromRgb(56, 189, 248));

    private readonly PipeEndpoint _endpoint;
    private readonly DesktopAgentSession _session;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TextBlock _statusText = TextBlock(
        "正在连接 Agent…",
        16,
        SecondaryTextBrush);
    private readonly TextBlock _instanceText = TextBlock(
        "实例：—",
        12,
        SecondaryTextBrush);
    private readonly TextBlock _cpuText = MetricText();
    private readonly TextBlock _memoryText = MetricText();
    private readonly TextBlock _networkText = MetricText(34);
    private readonly TextBlock _diskText = MetricText(34);
    private readonly TextBlock _gpuText = MetricText(34);
    private readonly TextBlock _temperatureText = MetricText(34);
    private readonly TextBlock _powerText = TextBlock(
        "电源：—",
        13,
        SecondaryTextBrush);
    private readonly TextBlock _updatedText = TextBlock(
        "尚无实时快照",
        13,
        SecondaryTextBrush);
    private readonly TextBlock _historyText = TextBlock(
        "历史：尚未查询",
        13,
        SecondaryTextBrush);
    private readonly TextBlock _diagnosticsText = TextBlock(
        "诊断：尚未查询",
        13,
        SecondaryTextBrush);
    private Task? _sessionTask;

    public MainWindow(PipeEndpoint endpoint)
    {
        _endpoint = endpoint;
        _session = new DesktopAgentSession(endpoint);
        _session.StateChanged += OnStateChanged;

        Title = "PerfMonitor Desktop";
        Width = 980;
        Height = 900;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WindowBrush;
        Foreground = PrimaryTextBrush;
        Content = BuildContent();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private UIElement BuildContent()
    {
        var root = new Grid
        {
            Margin = new Thickness(32),
        };
        root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
            });
        root.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 28),
        };
        header.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(TextBlock(
            "电脑性能监测",
            30,
            PrimaryTextBrush,
            FontWeights.SemiBold));
        titleStack.Children.Add(_instanceText);
        header.Children.Add(titleStack);
        var statusBorder = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 9, 16, 9),
            Child = _statusText,
        };
        Grid.SetColumn(statusBorder, 1);
        header.Children.Add(statusBorder);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var cards = new Grid();
        cards.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
            });
        cards.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
            });
        cards.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
            });
        cards.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        cards.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        var cpuCard = MetricCard(
            "系统 CPU",
            _cpuText,
            "整机归一化利用率");
        cpuCard.Margin = new Thickness(0, 0, 12, 0);
        cards.Children.Add(cpuCard);
        var memoryCard = MetricCard(
            "内存",
            _memoryText,
            "物理内存利用率");
        memoryCard.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(memoryCard, 1);
        cards.Children.Add(memoryCard);
        var networkCard = MetricCard(
            "网络吞吐",
            _networkText,
            "活动非回环网卡累计 · 下载 / 上传");
        networkCard.Margin = new Thickness(0, 12, 12, 0);
        Grid.SetRow(networkCard, 1);
        cards.Children.Add(networkCard);
        var diskCard = MetricCard(
            "磁盘 I/O",
            _diskText,
            "所有物理磁盘累计 · 读取 / 写入");
        diskCard.Margin = new Thickness(12, 12, 0, 0);
        Grid.SetRow(diskCard, 1);
        Grid.SetColumn(diskCard, 1);
        cards.Children.Add(diskCard);
        var gpuCard = MetricCard(
            "GPU",
            _gpuText,
            "隔离 Worker · 最大 load / 最高温度");
        gpuCard.Margin = new Thickness(0, 12, 12, 0);
        Grid.SetRow(gpuCard, 2);
        cards.Children.Add(gpuCard);
        var temperatureCard = MetricCard(
            "硬件温度",
            _temperatureText,
            "全部可读传感器的最高温度");
        temperatureCard.Margin = new Thickness(12, 12, 0, 0);
        Grid.SetRow(temperatureCard, 2);
        Grid.SetColumn(temperatureCard, 1);
        cards.Children.Add(temperatureCard);
        Grid.SetRow(cards, 1);
        root.Children.Add(cards);

        var footer = new Grid
        {
            Margin = new Thickness(0, 28, 0, 0),
        };
        footer.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        footer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        var footerStack = new StackPanel();
        footerStack.Children.Add(_updatedText);
        footerStack.Children.Add(_powerText);
        footerStack.Children.Add(_historyText);
        footerStack.Children.Add(_diagnosticsText);
        footer.Children.Add(footerStack);
        var buttonStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };
        var diagnosticsButton = ActionButton("查询诊断");
        diagnosticsButton.Margin = new Thickness(0, 0, 10, 0);
        diagnosticsButton.Click += OnDiagnosticsClick;
        buttonStack.Children.Add(diagnosticsButton);
        var historyButton = new Button
        {
            Content = "查询最近 24 小时",
            Padding = new Thickness(18, 10, 18, 10),
            Background = AccentBrush,
            Foreground = WindowBrush,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        historyButton.Click += OnHistoryClick;
        buttonStack.Children.Add(historyButton);
        Grid.SetColumn(buttonStack, 1);
        footer.Children.Add(buttonStack);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        _sessionTask = _session.RunAsync(_stopping.Token);
    }

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        _session.StateChanged -= OnStateChanged;
        _stopping.Cancel();
        if (_sessionTask is not null)
        {
            try
            {
                await _sessionTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }

    private void OnStateChanged(DesktopConnectionState state)
    {
        _ = Dispatcher.InvokeAsync(() => RenderState(state));
    }

    private void RenderState(DesktopConnectionState state)
    {
        _statusText.Text = state.Status switch
        {
            DesktopConnectionStatus.Starting => "正在连接",
            DesktopConnectionStatus.Connected when
                state.RestartCount > 0 =>
                $"已连接 · Agent 重启 {state.RestartCount} 次",
            DesktopConnectionStatus.Connected => "已连接",
            DesktopConnectionStatus.Reconnecting =>
                "连接中断 · 正在重连",
            DesktopConnectionStatus.Stopped => "已停止",
            _ => "未知",
        };
        _instanceText.Text = state.InstanceId is null
            ? "实例：—"
            : $"实例：{state.InstanceId}";

        if (state.LatestSnapshot is null)
        {
            return;
        }

        var cpu = ReadMetric(
            state.LatestSnapshot,
            GroupIds.SystemCpu,
            MetricIds.SystemCpuUtilization);
        var memory = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Memory,
            MetricIds.MemoryUtilization);
        var networkReceive = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Network,
            MetricIds.NetworkReceiveBytesPerSecond);
        var networkSend = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Network,
            MetricIds.NetworkSendBytesPerSecond);
        var diskRead = ReadMetric(
            state.LatestSnapshot,
            GroupIds.DiskIo,
            MetricIds.DiskReadBytesPerSecond);
        var diskWrite = ReadMetric(
            state.LatestSnapshot,
            GroupIds.DiskIo,
            MetricIds.DiskWriteBytesPerSecond);
        var gpuLoad = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Gpu,
            MetricIds.GpuLoadMaxPercent);
        var gpuTemperature = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Gpu,
            MetricIds.GpuTemperatureMaxCelsius);
        var hardwareTemperature = ReadMetric(
            state.LatestSnapshot,
            GroupIds.Sensors,
            MetricIds.HardwareTemperatureMaxCelsius);
        _cpuText.Text = FormatPercent(cpu);
        _memoryText.Text = FormatPercent(memory);
        _networkText.Text =
            $"{FormatRate(networkReceive)} / {FormatRate(networkSend)}";
        _diskText.Text =
            $"{FormatRate(diskRead)} / {FormatRate(diskWrite)}";
        _gpuText.Text =
            $"{FormatPercent(gpuLoad)} / " +
            $"{FormatTemperature(gpuTemperature)}";
        _temperatureText.Text =
            FormatTemperature(hardwareTemperature);
        _powerText.Text = FormatPower(state.LatestSnapshot);
        _updatedText.Text = state.LatestSnapshot.CompletedAtUtc is null
            ? "快照正在预热"
            : $"更新：{state.LatestSnapshot.CompletedAtUtc.Value.ToLocalTime():HH:mm:ss} · 序列 {state.LatestSnapshot.Sequence}";
    }

    private async void OnHistoryClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        _historyText.Text = "历史：查询中…";
        try
        {
            await using var client =
                await NamedPipeAgentClient.ConnectAsync(
                    _endpoint,
                    TimeSpan.FromSeconds(3),
                    _stopping.Token);
            var now = DateTimeOffset.UtcNow;
            var history = await client.QueryHistoryAsync(
                new HistoryQueryContract
                {
                    MetricIds =
                    [
                        MetricIds.SystemCpuUtilization,
                        MetricIds.MemoryUtilization,
                    ],
                    FromEpochMs = now.AddHours(-24)
                        .ToUnixTimeMilliseconds(),
                    ToEpochMs = now.ToUnixTimeMilliseconds(),
                    MaxPoints = 240,
                },
                _stopping.Token);
            _historyText.Text =
                $"历史：{history.PointCount} 个时间桶 / {history.SourcePointCount} 个源样本";
        }
        catch (OperationCanceledException) when (
            _stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            IpcProtocolException or
            IpcRemoteException)
        {
            _historyText.Text = "历史：暂不可用";
        }
    }

    private async void OnDiagnosticsClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        _diagnosticsText.Text = "诊断：查询中…";
        try
        {
            await using var client =
                await NamedPipeAgentClient.ConnectAsync(
                    _endpoint,
                    TimeSpan.FromSeconds(3),
                    _stopping.Token);
            var now = DateTimeOffset.UtcNow;
            var diagnostics =
                await client.QueryDiagnosticsAsync(
                    new DiagnosticQueryContract
                    {
                        FromEpochMs = now.AddHours(-24)
                            .ToUnixTimeMilliseconds(),
                        ToEpochMs = now.ToUnixTimeMilliseconds(),
                        MaxEvents = 200,
                    },
                    _stopping.Token);
            var active = diagnostics.Events
                .GroupBy(
                    static item =>
                        (item.RuleId, item.SubjectId))
                .Select(static group => group
                    .OrderByDescending(
                        static item => item.LastSeenUtc)
                    .First())
                .Where(static item =>
                    item.State == DiagnosticStates.Active)
                .OrderByDescending(
                    static item => item.LastSeenUtc)
                .ToArray();
            _diagnosticsText.Text = active.Length == 0
                ? "诊断：无活动告警"
                : $"诊断：{active.Length} 个活动告警 · 最近 {active[0].RuleId}";
        }
        catch (OperationCanceledException) when (
            _stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or
            TimeoutException or
            IpcProtocolException or
            IpcRemoteException)
        {
            _diagnosticsText.Text = "诊断：暂不可用";
        }
    }

    private static Border MetricCard(
        string title,
        TextBlock value,
        string description)
    {
        var stack = new StackPanel();
        stack.Children.Add(TextBlock(
            title,
            16,
            SecondaryTextBrush,
            FontWeights.SemiBold));
        stack.Children.Add(value);
        stack.Children.Add(TextBlock(
            description,
            13,
            SecondaryTextBrush));
        return new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28),
            Child = stack,
        };
    }

    private static TextBlock MetricText(double size = 62) => new()
    {
        Text = "—",
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = PrimaryTextBrush,
        Margin = new Thickness(0, 26, 0, 18),
    };

    private static Button ActionButton(string content) =>
        new()
        {
            Content = content,
            Padding = new Thickness(18, 10, 18, 10),
            Background = AccentBrush,
            Foreground = WindowBrush,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

    private static TextBlock TextBlock(
        string text,
        double size,
        Brush brush,
        FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        Margin = new Thickness(0, 3, 0, 3),
    };

    private static double? ReadMetric(
        AgentSnapshot snapshot,
        string groupId,
        string metricId)
    {
        if (!snapshot.Groups.TryGetValue(groupId, out var group) ||
            group.Data is not JsonObject data ||
            data["metrics"] is not JsonObject metrics ||
            metrics[metricId] is not JsonObject metric ||
            metric["value"] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var result))
        {
            return result;
        }

        return value.TryGetValue<long>(out var integer)
            ? integer
            : null;
    }

    private static string FormatPercent(double? value) =>
        value is null
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Value:F1}%");

    private static string FormatRate(double? value)
    {
        if (value is null)
        {
            return "—";
        }

        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        var scaled = Math.Max(0, value.Value);
        var index = 0;
        while (scaled >= 1024 && index < units.Length - 1)
        {
            scaled /= 1024;
            index++;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{scaled:F1} {units[index]}");
    }

    private static string FormatTemperature(double? value) =>
        value is null
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Value:F1} °C");

    private static string FormatPower(AgentSnapshot snapshot)
    {
        if (!snapshot.Groups.TryGetValue(
                GroupIds.Power,
                out var group) ||
            group.Data is not JsonObject data)
        {
            return "电源：—";
        }

        var source = data["powerSource"]?.GetValue<string>() switch
        {
            "ac" => "交流电",
            "battery" => "电池",
            _ => "未知",
        };
        var present = data["batteryPresent"] is JsonValue presentValue &&
            presentValue.TryGetValue<bool>(out var presentResult)
            ? presentResult
            : (bool?)null;
        if (present == false)
        {
            return $"电源：{source} · 无系统电池";
        }
        if (present is null)
        {
            return $"电源：{source} · 电池状态未知";
        }

        var charge = ReadMetric(
            snapshot,
            GroupIds.Power,
            MetricIds.BatteryChargePercent);
        var charging =
            data["charging"] is JsonValue chargingValue &&
            chargingValue.TryGetValue<bool>(out var chargingResult) &&
            chargingResult;
        return charge is null
            ? $"电源：{source} · 电量未知"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"电源：{source} · 电量 {charge.Value:F0}%{(charging ? " · 充电中" : string.Empty)}");
    }
}
