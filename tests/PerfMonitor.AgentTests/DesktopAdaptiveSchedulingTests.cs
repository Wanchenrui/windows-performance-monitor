using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Desktop;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopAdaptiveSchedulingTests
{
    [TestMethod]
    public async Task UnsupportedAgentNeverReceivesGetOrSetRequests()
    {
        var client = new FakeConnection { Supported = false };
        var session = Session(client);
        await session.QueryAsync();
        Assert.IsFalse(session.Current.Supported);
        Assert.IsFalse(session.Current.Actual!.Enabled);
        await session.SetAsync(true, AdaptiveSchedulingPreferences.EnergySaving, 900);
        Assert.AreEqual(0, client.QueryRequests); Assert.AreEqual(0, client.SetRequests);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task ConfirmedEnableSwitchAndDisableSendAllPreferencesWithCurrentInstance()
    {
        var client = new FakeConnection(); var session = Session(client);
        await session.QueryAsync(); Assert.IsFalse(session.Current.Actual!.Enabled);
        foreach (var preference in AdaptiveSchedulingPreferences.All)
        {
            await session.SetAsync(true, preference, 900);
            Assert.AreEqual(preference, session.Current.Actual!.Preference);
            Assert.AreEqual(client.InstanceId, client.LastRequest!.InstanceId);
            Assert.AreEqual(900, client.LastRequest.DurationSeconds);
        }
        await session.SetAsync(false, AdaptiveSchedulingPreferences.EnergySaving, 900);
        Assert.IsFalse(session.Current.Actual!.Enabled);
        Assert.AreEqual(4, client.SetRequests);
        await session.StopAsync(); await session.StopAsync();
    }

    [TestMethod]
    public async Task OperationGatePreventsOverlappingRequestsAndStopCancelsPendingQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeConnection
        {
            Query = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(); },
        };
        var session = Session(client); var pending = session.QueryAsync(); await entered.Task;
        Assert.IsTrue(session.Current.Busy);
        Assert.AreSame(pending, session.QueryAsync());
        Assert.AreSame(pending, session.SetAsync(true, AdaptiveSchedulingPreferences.Throughput, 1800));
        await session.StopAsync(); await pending;
        Assert.IsNull(session.Current.Actual); Assert.IsFalse(session.Current.Busy);
        Assert.AreEqual(1, client.QueryRequests); Assert.AreEqual(0, client.SetRequests); Assert.AreEqual(1, client.Disposals);
    }

    [TestMethod]
    public async Task RawConnectionChangeClearsActualAndRejectsLateResponse()
    {
        var client = new FakeConnection(); var session = Session(client); await session.QueryAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<AdaptiveSchedulingContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Set = (_, _) => { entered.SetResult(); return delayed.Task; };
        var pending = session.SetAsync(true, AdaptiveSchedulingPreferences.EnergySaving, 3600); await entered.Task;
        session.UpdateConnection(new(DesktopConnectionStatus.Reconnecting, client.InstanceId, 0, null, null));
        Assert.IsNull(session.Current.Actual);
        delayed.SetResult(Response(enabled: true)); await pending;
        Assert.IsNull(session.Current.Actual);
        StringAssert.Contains(session.Current.Error!, "连接已变化");
        await session.StopAsync();
    }

    [TestMethod]
    public async Task TotalTimeoutClearsUnconfirmedState()
    {
        var client = new FakeConnection();
        var session = new DesktopAdaptiveSchedulingSession(_ => Task.FromResult<IDesktopAdaptiveSchedulingConnection>(client), operationTimeout: TimeSpan.FromMilliseconds(60));
        session.UpdateConnection(Connected()); await session.QueryAsync();
        client.Query = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(); };
        await session.QueryAsync();
        Assert.IsNull(session.Current.Actual); Assert.IsFalse(session.Current.Busy);
        StringAssert.Contains(session.Current.Error!, "超时");
        await session.StopAsync();
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("preference")]
    [DataRow("remaining_nan")]
    [DataRow("remaining_negative")]
    [DataRow("enabled_no_lease")]
    [DataRow("unknown_group")]
    [DataRow("duplicate_group")]
    [DataRow("period_zero")]
    public async Task InvalidResponseCannotBecomeConfirmedActualState(string invalid)
    {
        var valid = Response(enabled: true);
        var response = invalid switch
        {
            "instance" => valid with { InstanceId = "other-instance" },
            "preference" => valid with { Preference = "unknown" },
            "remaining_nan" => valid with { RemainingSeconds = double.NaN },
            "remaining_negative" => valid with { RemainingSeconds = -1 },
            "enabled_no_lease" => valid with { RemainingSeconds = null, ExpiresAtUtc = null },
            "unknown_group" => valid with { Groups = [valid.Groups[0] with { GroupId = GroupIds.SystemCpu }] },
            "duplicate_group" => valid with { Groups = [valid.Groups[0], valid.Groups[0]] },
            "period_zero" => valid with { Groups = [valid.Groups[0] with { EffectivePeriodMs = 0 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        var client = new FakeConnection { Response = response }; var session = Session(client);
        await session.QueryAsync(); Assert.IsNull(session.Current.Actual);
        StringAssert.Contains(session.Current.Error!, "未确认");
        await session.StopAsync();
    }

    [STATestMethod]
    public void DrawerKeepsControlsAndDraftWhileShowingActualPreferenceAndPeriods()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var client = new FakeConnection { Response = Response(enabled: true) with { Preference = AdaptiveSchedulingPreferences.EnergySaving } };
        var session = Session(client);
        var endpoint = PipeEndpoint.ForCurrentUser() with { PipeName = $"PerfMonitor.adaptive-ui-test.{Guid.NewGuid():N}" };
        var window = new MainWindow(endpoint, session) { ShowActivated = false };
        try
        {
            Wait(session.QueryAsync()); Populate(window);
            var buttons = Elements(window).OfType<Button>().ToArray();
            var energy = buttons.Single(button => Equals(button.Content, "节能"));
            var throughput = buttons.Single(button => Equals(button.Content, "任务吞吐"));
            Assert.AreEqual("已选中", AutomationProperties.GetItemStatus(energy));
            Assert.IsTrue(Elements(window).OfType<TextBlock>().Any(block => block.Text == "已启用 · 节能"));
            throughput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(session.QueryAsync()); Populate(window); Populate(window);
            Assert.AreSame(throughput, Elements(window).OfType<Button>().Single(button => Equals(button.Content, "任务吞吐")));
            Assert.AreEqual("已选中", AutomationProperties.GetItemStatus(throughput));
            Assert.IsTrue(Elements(window).OfType<TextBlock>().Any(block => block.Text == "待应用偏好 · 任务吞吐"));
            Assert.IsTrue(Elements(window).OfType<TextBlock>().Any(block => block.Text.Contains("GPU  2 秒 → 8 秒", StringComparison.Ordinal)));
            Assert.IsTrue(Elements(window).OfType<TextBlock>().Any(block => block.Text.Contains("手动暂停优先", StringComparison.Ordinal)));
            foreach (var label in new[] { "15 分钟", "30 分钟", "60 分钟" })
            {
                var duration = buttons.Single(button => Equals(button.Content, label));
                Assert.IsFalse(duration.IsEnabled, "An active lease cannot be silently extended.");
                Assert.AreEqual("未选中", AutomationProperties.GetItemStatus(duration), "Remaining time does not establish the original lease duration.");
            }
            Wait(session.SetAsync(true, AdaptiveSchedulingPreferences.Throughput, 1800)); Populate(window);
            Assert.IsFalse(buttons.Single(button => Equals(button.Content, "切换偏好（不续期）")).IsEnabled);
            Assert.IsFalse(window.IsVisible);
        }
        finally { Wait(window.StopSessionAsync()); window.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static DesktopConnectionState Connected() => new(DesktopConnectionStatus.Connected, "adaptive-test", 0, null, null);
    private static DesktopAdaptiveSchedulingSession Session(FakeConnection client)
    {
        var session = new DesktopAdaptiveSchedulingSession(_ => Task.FromResult<IDesktopAdaptiveSchedulingConnection>(client));
        session.UpdateConnection(Connected()); return session;
    }
    private static AdaptiveSchedulingContract Response(bool enabled = false) => new()
    {
        Supported = true, Enabled = enabled, CanRestore = enabled, InstanceId = "adaptive-test",
        Preference = AdaptiveSchedulingPreferences.Responsiveness,
        DecisionCode = enabled ? AdaptiveSchedulingDecisionCodes.Reduced : AdaptiveSchedulingDecisionCodes.Disabled,
        Reason = enabled ? "当前繁忙，已降低可选硬件请求频率。" : "智能调度未启用。",
        ImpactDescription = "仅改变本软件查询周期，性能收益未实测。",
        RemainingSeconds = enabled ? 900 : null, ExpiresAtUtc = enabled ? DateTimeOffset.UtcNow.AddMinutes(15) : null,
        Groups = [new() { GroupId = GroupIds.Gpu, BasePeriodMs = 2000, EffectivePeriodMs = 8000 }, new() { GroupId = GroupIds.Sensors, BasePeriodMs = 5000, EffectivePeriodMs = 15000, Paused = true }],
    };
    private static void Populate(MainWindow window) => typeof(MainWindow).GetMethod("PopulateAdaptiveSchedulingDrawer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Elements(child)) yield return item;
    }
    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(5);
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        Assert.IsTrue(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private sealed class FakeConnection : IDesktopAdaptiveSchedulingConnection
    {
        public string InstanceId => "adaptive-test";
        public bool Supported { get; init; } = true;
        public AdaptiveSchedulingContract Response { get; set; } = DesktopAdaptiveSchedulingTests.Response();
        public Func<CancellationToken, Task<AdaptiveSchedulingContract>>? Query { get; set; }
        public Func<AdaptiveSchedulingRequestContract, CancellationToken, Task<AdaptiveSchedulingContract>>? Set { get; set; }
        public AdaptiveSchedulingRequestContract? LastRequest { get; private set; }
        public int QueryRequests { get; private set; }
        public int SetRequests { get; private set; }
        public int Disposals { get; private set; }
        public Task<AdaptiveSchedulingContract> QueryAsync(CancellationToken token)
        { QueryRequests++; return Query?.Invoke(token) ?? Task.FromResult(Response); }
        public Task<AdaptiveSchedulingContract> SetAsync(AdaptiveSchedulingRequestContract request, CancellationToken token)
        {
            SetRequests++; LastRequest = request;
            if (Set is not null) return Set(request, token);
            Response = DesktopAdaptiveSchedulingTests.Response(request.Enabled) with { Preference = request.Preference };
            return Task.FromResult(Response);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
