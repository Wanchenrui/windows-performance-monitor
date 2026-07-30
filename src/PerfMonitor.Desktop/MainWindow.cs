using System.Globalization;
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
    private readonly TextBlock _updatedText = TextBlock(
        "尚无实时快照",
        13,
        SecondaryTextBrush);
    private readonly TextBlock _historyText = TextBlock(
        "历史：尚未查询",
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
        Height = 620;
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
        footerStack.Children.Add(_historyText);
        footer.Children.Add(footerStack);
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
        Grid.SetColumn(historyButton, 1);
        footer.Children.Add(historyButton);
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
        _cpuText.Text = FormatPercent(cpu);
        _memoryText.Text = FormatPercent(memory);
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

    private static TextBlock MetricText() => new()
    {
        Text = "—",
        FontSize = 62,
        FontWeight = FontWeights.SemiBold,
        Foreground = PrimaryTextBrush,
        Margin = new Thickness(0, 26, 0, 18),
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
}
