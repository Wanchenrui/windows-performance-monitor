using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Desktop;
using PerfMonitor.Ipc.NamedPipes;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopLifecycleIntegrationTests
{
    [STATestMethod]
    public void MissingTrayHudCloseButtonExitsInsteadOfLosingTheWindow()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var endpoint = PipeEndpoint.ForCurrentUser() with { PipeName = $"PerfMonitor.no-tray-test.{Guid.NewGuid():N}" };
        var window = new MainWindow(endpoint)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        var shutdowns = 0;
        using var controller = new DesktopResidentController(window,
            window.StopSessionAsync, () => shutdowns++, window.SetResidentStatus,
            (_, _) => null, (_, _) => new Hotkey(false, DesktopHotkey.AlreadyRegisteredError), () => { });
        try
        {
            window.Show();
            Assert.IsTrue(Elements(window).OfType<TextBlock>().Any(block => block.Text.Contains("托盘不可用", StringComparison.Ordinal)));
            var close = Elements(window).OfType<Button>().Single(button =>
                button.Content is string text && text.StartsWith("收起", StringComparison.Ordinal));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => shutdowns == 1);
            Assert.IsFalse(window.IsVisible);
        }
        finally
        {
            Wait(window.StopSessionAsync());
            controller.Dispose();
            window.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [STATestMethod]
    public void RealWindowRetainsOneSubscriptionRestoresLatestAndExitsWithoutStoppingAgent()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var endpoint = PipeEndpoint.ForCurrentUser() with
        {
            PipeName = $"PerfMonitor.desktop-lifecycle-test.{Guid.NewGuid():N}",
        };
        var service = new LifecycleService();
        var hub = new SnapshotSubscriptionHub();
        var server = new NamedPipeAgentServer(endpoint, service, hub);
        var window = new MainWindow(endpoint)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        var shutdowns = 0;
        using var controller = new DesktopResidentController(window,
            window.StopSessionAsync, () => shutdowns++, window.SetResidentStatus,
            (_, _) => new Tray(), (_, _) => new Hotkey(), () => { });
        try
        {
            server.Start();
            window.Show();
            PumpUntil(() => hub.SubscriberCount == 1 && HasText(window, "10%"));
            var initialReads = service.SnapshotReads;
            controller.Hide();
            service.Latest = Snapshot(service.InstanceId, 2, 99, 58);
            Assert.IsTrue(hub.TryPublish(service.Latest));
            Assert.IsFalse(window.IsVisible);
            controller.Show();
            PumpUntil(() => HasText(window, "99%"));
            Assert.AreEqual(1, hub.SubscriberCount);
            Assert.AreEqual(initialReads, service.SnapshotReads, "Showing must reuse the existing subscription.");

            window.SelectResource(ResourceView.Memory);
            Assert.AreEqual(1000d, window.Width);
            Assert.AreEqual(680d, window.Height);
            controller.Hide();
            controller.Show();
            Assert.AreEqual(1000d, window.Width);
            Assert.AreEqual(680d, window.Height);
            Assert.IsTrue(HasText(window, "58%"));

            window.OpenPanel(DashboardPanel.Diagnostics);
            PumpUntil(() => service.DiagnosticsEntered);
            var exit = controller.ExitAsync();
            PumpUntil(() => exit.IsCompleted);
            exit.GetAwaiter().GetResult();
            Assert.AreSame(window.StopSessionAsync(), window.StopSessionAsync());
            PumpUntil(() => hub.SubscriberCount == 0);
            Assert.AreEqual(1, shutdowns);
            Assert.IsFalse(server.Completion.IsCompleted, "Desktop exit must leave the Agent running.");

            var connect = NamedPipeAgentClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);
            PumpUntil(() => connect.IsCompleted);
            var client = connect.GetAwaiter().GetResult();
            try
            {
                var request = client.GetSnapshotAsync(CancellationToken.None);
                PumpUntil(() => request.IsCompleted);
                Assert.AreEqual(2L, request.GetAwaiter().GetResult().Sequence);
            }
            finally { Wait(client.DisposeAsync().AsTask()); }
        }
        finally
        {
            service.ReleaseDiagnostics();
            Wait(window.StopSessionAsync());
            controller.Dispose();
            window.Close();
            Wait(server.DisposeAsync().AsTask());
            Wait(hub.DisposeAsync().AsTask());
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static bool HasText(DependencyObject root, string expected)
    {
        if (root is TextBlock block && block.Text == expected) return true;
        return LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Any(child => HasText(child, expected));
    }

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Elements(child)) yield return descendant;
    }

    private static void Wait(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> completed)
    {
        if (completed()) return;
        var started = Stopwatch.GetTimestamp();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (completed() || Stopwatch.GetElapsedTime(started).TotalSeconds >= 5) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(completed(), "The real Desktop/pipe lifecycle did not complete within the deadline.");
    }

    private static AgentSnapshot Snapshot(string instance, long sequence, double cpu, double memory)
    {
        var utc = DateTimeOffset.UtcNow;
        SnapshotGroup Group(string provider, string metric, double value) =>
            new(provider, utc, AvailabilityStates.Available, FreshnessStates.Fresh,
                ProviderCoverage.Complete, [], new JsonObject
                {
                    ["sampleReady"] = true,
                    ["metrics"] = new JsonObject { [metric] = new JsonObject { ["value"] = value } },
                }) { ObservationSequence = sequence, ObservedElapsedSeconds = sequence };
        return new(ContractVersions.V1, ProductVersions.Agent, instance, sequence,
            null, null, utc, 0, new(AvailabilityStates.Available, FreshnessStates.Fresh), new(3600, 3600),
            new Dictionary<string, SnapshotGroup>
            {
                [GroupIds.SystemCpu] = Group(ProviderIds.SystemCpu, MetricIds.SystemCpuUtilization, cpu),
                [GroupIds.Memory] = Group(ProviderIds.Memory, MetricIds.MemoryUtilization, memory),
            }) { ElapsedSeconds = sequence, DeliverySequence = sequence };
    }

    private sealed class Tray : IDesktopTray
    {
        public void Update(bool windowVisible, string? status) { }
        public void Dispose() { }
    }

    private sealed class Hotkey(bool registered = true, int error = 0) : IDesktopHotkey
    {
        public bool IsRegistered => registered;
        public int ErrorCode => error;
        public void Dispose() { }
    }

    private sealed class LifecycleService : IAgentIpcService
    {
        private readonly TaskCompletionSource _diagnostics = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AgentSnapshot _latest;
        private int _reads, _diagnosticsEntered;
        public LifecycleService() => _latest = Snapshot(InstanceId, 1, 10, 34);
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        public int SnapshotReads => Volatile.Read(ref _reads);
        public bool DiagnosticsEntered => Volatile.Read(ref _diagnosticsEntered) == 1;
        public AgentSnapshot Latest { get => Volatile.Read(ref _latest); set => Volatile.Write(ref _latest, value); }
        public AgentSnapshot ReadLatestSnapshot() { Interlocked.Increment(ref _reads); return Latest; }
        public void ReleaseDiagnostics() => _diagnostics.TrySetResult();
        public CapabilitiesContract ReadCapabilities() => new()
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent, InstanceId = InstanceId,
            Groups = [], StableErrorCodes = [], Endpoints = new Dictionary<string, string>(),
            History = new() { MetricIds = [], DefaultMaxPoints = 10, MaxPoints = 10, RamPointLimit = 10, Aggregations = [] },
            Diagnostics = new() { DefaultMaxEvents = 10, MaxEvents = 10, ActionsSupported = false, Rules = [] },
        };
        public HealthContract ReadHealth() => throw new NotSupportedException();
        public ValueTask<HistoryContract> QueryHistoryAsync(HistoryQueryContract query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<ActionResultContract> ExecuteActionAsync(UserActionRequestContract request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async ValueTask<DiagnosticsContract> QueryDiagnosticsAsync(DiagnosticQueryContract query, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _diagnosticsEntered, 1);
            await _diagnostics.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new()
            {
                ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
                InstanceId = InstanceId, Query = query, Events = [], EventCount = 0, Truncated = false,
            };
        }
    }
}
