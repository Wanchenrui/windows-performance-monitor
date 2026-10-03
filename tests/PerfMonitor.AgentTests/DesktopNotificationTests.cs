using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Desktop;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DesktopNotificationTests
{
    private const string Instance = "notification-agent-a";

    [TestMethod]
    public async Task DefaultOffAndMissingTrayNeverQueryAndEnablingEstablishesBaseline()
    {
        await using var fixture = new Fixture();
        fixture.Connect();
        await fixture.Poll();
        Assert.IsFalse(fixture.Notifications.Current.Enabled);
        Assert.AreEqual(0, fixture.Queries);
        fixture.Notifications.SetEnabled(true);
        await fixture.Poll();
        Assert.AreEqual(0, fixture.Queries);
        fixture.Notifications.SetTrayAvailable(true);
        fixture.Events = [fixture.Event(1, "baseline")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Queries);
        Assert.AreEqual(0, fixture.Shown.Count);
        fixture.Events = [fixture.Event(2, "new")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        fixture.Notifications.SetEnabled(false);
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Queries);
    }

    [TestMethod]
    public async Task NewWarningsAndCriticalEventsMergeIntoOneRequestAndDeduplicateSubjects()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "cpu"),
            fixture.Event(2, "memory") with { Severity = DiagnosticSeverities.Critical },
            fixture.Event(3, "info") with { Severity = DiagnosticSeverities.Info }];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        Assert.AreEqual(2, fixture.Shown[0].Events.Count);
        fixture.Events = [fixture.Event(4, "cpu"), fixture.Event(5, "memory")];
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        await fixture.Poll();
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task LatestObservationWinsAndResolvedEventSuppressesEarlierActive()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(3, "cpu") with { State = DiagnosticStates.Resolved },
            fixture.Event(1, "cpu"), fixture.Event(2, "memory")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        Assert.AreEqual("memory", fixture.Shown[0].Events.Single().SubjectId);
    }

    [TestMethod]
    public async Task OldInstanceUnknownRuleMissingSequenceAndOutOfRangeCannotNotify()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "old") with { InstanceId = "old-agent" },
            fixture.Event(2, "rule") with { RuleId = "unknown.rule" },
            fixture.Event(3, "sequence") with { ObservationSequence = null },
            fixture.Event(4, "future") with { LastSeenUtc = fixture.Time.GetUtcNow().AddMinutes(1) },
            fixture.Event(5, "past") with { LastSeenUtc = fixture.Time.GetUtcNow().AddMinutes(-6) },
            fixture.Event(6, "state") with { State = "unknown" },
            fixture.Event(7, "severity") with { Severity = "unknown" }];
        await fixture.Poll();
        Assert.AreEqual(0, fixture.Shown.Count);
        Assert.AreEqual(200, fixture.LastQuery!.MaxEvents);
        Assert.AreEqual(TimeSpan.FromMinutes(5).TotalMilliseconds,
            (double)(fixture.LastQuery.ToEpochMs!.Value - fixture.LastQuery.FromEpochMs!.Value));
    }

    [TestMethod]
    public async Task MonotonicDoNotDisturbIgnoresUtcChangesAndDiscardsEventsOnResume()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Notifications.SetDoNotDisturb(TimeSpan.FromMinutes(15));
        fixture.Time.SetUtcNow(fixture.Time.GetUtcNow().AddDays(1));
        Assert.IsTrue(fixture.Notifications.Current.DoNotDisturb);
        fixture.Events = [fixture.Event(1, "during")];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromMinutes(15));
        fixture.Events = [fixture.Event(2, "unpolled-during")];
        await fixture.Poll();
        Assert.IsFalse(fixture.Notifications.Current.DoNotDisturb);
        Assert.AreEqual(0, fixture.Shown.Count);
        fixture.Events = [fixture.Event(3, "new-after-baseline")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task IndefiniteDoNotDisturbRequiresManualEndAndSixtyMinuteOptionExpires()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Notifications.SetDoNotDisturb(null);
        fixture.Time.Advance(TimeSpan.FromDays(7));
        Assert.IsTrue(fixture.Notifications.Current.DoNotDisturb);
        Assert.IsNull(fixture.Notifications.Current.DoNotDisturbRemaining);
        fixture.Notifications.EndDoNotDisturb();
        fixture.Events = [fixture.Event(1, "manual-resume-baseline")];
        await fixture.Poll();
        Assert.AreEqual(0, fixture.Shown.Count);
        fixture.Notifications.SetDoNotDisturb(TimeSpan.FromMinutes(60));
        fixture.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.IsTrue(fixture.Notifications.Current.DoNotDisturb);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.IsFalse(fixture.Notifications.Current.DoNotDisturb);
    }

    [TestMethod]
    public async Task RateLimitedEventsAreConsumedWithoutResumeBacklog()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "first")];
        await fixture.Poll();
        fixture.Events = [fixture.Event(2, "limited")];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        fixture.Events = [fixture.Event(3, "unpolled-limited")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        fixture.Events = [fixture.Event(4, "after-resume")];
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task ContinuousPollingAllowsRecoveredSubjectToReactivateAfterCooldown()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "cpu")];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Events = [fixture.Event(2, "limited")];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Events = [fixture.Event(3, "cpu") with { State = DiagnosticStates.Resolved }];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Events = [fixture.Event(4, "cpu"), fixture.Event(2, "limited")];
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Shown.Count);
        Assert.AreEqual("cpu", fixture.Shown[1].Events.Single().SubjectId);
    }

    [TestMethod]
    public async Task ResolvedThenActiveInOneBatchStartsNewEpisodeAfterCooldown()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "cpu")];
        await fixture.Poll();
        for (var i = 0; i < 3; i++)
        { fixture.Time.Advance(TimeSpan.FromSeconds(30)); await fixture.Poll(); }
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        fixture.Events = [fixture.Event(3, "cpu"),
            fixture.Event(2, "cpu") with { State = DiagnosticStates.Resolved }];
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Shown.Count);
        Assert.AreEqual(3L, fixture.Shown[1].Events.Single().ObservationSequence);
    }

    [TestMethod]
    public async Task LongPollingGapRebaselinesInsteadOfReplayingUnobservedEvents()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        fixture.Events = [fixture.Event(1, "resume-baseline")];
        await fixture.Poll();
        Assert.AreEqual(0, fixture.Shown.Count);
        fixture.Events = [fixture.Event(2, "after-resume")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task ReconnectionAndInstanceSwitchAlwaysEstablishFreshBaseline()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "a")];
        await fixture.Poll();
        fixture.Notifications.UpdateConnection(new(DesktopConnectionStatus.Reconnecting, Instance, 0, null, "io"));
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Queries);
        fixture.Connect();
        fixture.Events = [fixture.Event(2, "reconnected")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        fixture.CurrentInstance = "notification-agent-b";
        fixture.Connect();
        fixture.Events = [fixture.Event(1, "b-baseline")];
        await fixture.Poll();
        fixture.Events = [fixture.Event(2, "b-new")];
        await fixture.Poll();
        Assert.AreEqual(2, fixture.Shown.Count);
        Assert.AreEqual(fixture.CurrentInstance, fixture.Shown[1].InstanceId);
    }

    [TestMethod]
    public async Task FailedTruncatedAndMismatchedQueriesReportErrorAndRebaseline()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        foreach (var failure in new[] { "failure", "truncated", "query", "instance", "count" })
        {
            fixture.Failure = failure;
            fixture.Events = [fixture.Event(++fixture.Sequence, failure)];
            await fixture.Poll();
            Assert.IsNotNull(fixture.Notifications.Current.QueryError);
            fixture.Failure = null;
            await fixture.Poll();
            Assert.IsNull(fixture.Notifications.Current.QueryError);
            Assert.AreEqual(0, fixture.Shown.Count);
        }
        fixture.Events = [fixture.Event(++fixture.Sequence, "confirmed-new")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task QueryTimeoutIsBoundedEvenWhenInjectedQueryIgnoresCancellation()
    {
        var time = new NotificationTimeProvider();
        var pending = new TaskCompletionSource<DiagnosticsContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var notifications = new DesktopDiagnosticNotifications((_, _, _) =>
        { started.SetResult(); return pending.Task; }, (_, _) => Task.FromResult(true), time);
        Activate(notifications);
        var poll = notifications.PollOnceAsync();
        await started.Task;
        time.Advance(DesktopDiagnosticNotifications.QueryTimeout);
        await poll;
        Assert.AreEqual("notification_query_timeout", notifications.Current.QueryError);
        Assert.IsFalse(pending.Task.IsCompleted);
    }

    [TestMethod]
    public async Task DisableConnectionChangeAndStopCancelLateQueryResponses()
    {
        foreach (var action in new[] { "disable", "connection", "stop" })
        {
            await using var fixture = new Fixture();
            await fixture.Enable();
            var pending = new TaskCompletionSource<DiagnosticsContract>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Deferred = pending;
            fixture.Events = [fixture.Event(1, "late")];
            var poll = fixture.Poll();
            if (action == "disable") fixture.Notifications.SetEnabled(false);
            if (action == "connection") fixture.Notifications.UpdateConnection(
                new(DesktopConnectionStatus.Reconnecting, Instance, 0, null, null));
            if (action == "stop") await fixture.Notifications.StopAsync();
            pending.SetResult(fixture.Response(fixture.LastQuery!));
            await poll;
            Assert.AreEqual(0, fixture.Shown.Count);
        }
    }

    [TestMethod]
    public async Task ClosingCancelsQueuedDisplayBeforeItRequestsBalloon()
    {
        var queryEvents = new List<DiagnosticEventContract>();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = 0;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var notifications = new DesktopDiagnosticNotifications(
            (instance, query, _) => Task.FromResult(new DiagnosticsContract
            {
                ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
                InstanceId = instance, Query = query, EventCount = queryEvents.Count, Events = queryEvents.ToArray(),
            }), async (_, token) =>
            {
                queued.SetResult();
                await release.Task;
                if (token.IsCancellationRequested) return false;
                displayed++; return true;
            }, time);
        Activate(notifications);
        await notifications.PollOnceAsync();
        queryEvents.Add(CreateEvent(time.GetUtcNow(), Instance, 1, "queued"));
        var poll = notifications.PollOnceAsync();
        await queued.Task;
        await notifications.StopAsync();
        release.SetResult();
        await poll;
        Assert.AreEqual(0, displayed);
    }

    [TestMethod]
    public async Task UnavailableDisplayIsConsumedAndNeverRetriedAsDelivered()
    {
        await using var fixture = new Fixture();
        fixture.AcceptDisplay = false;
        await fixture.Enable();
        fixture.Events = [fixture.Event(1, "rejected")];
        await fixture.Poll();
        fixture.Time.Advance(TimeSpan.FromMinutes(3));
        await fixture.Poll();
        await fixture.Poll();
        Assert.AreEqual(1, fixture.DisplayAttempts);
        Assert.AreEqual(0, fixture.Shown.Count);
    }

    [TestMethod]
    public async Task ExpectedTrayFailureDoesNotStopSubsequentNotificationPolling()
    {
        await using var fixture = new Fixture();
        await fixture.Enable();
        fixture.DisplayFailure = true;
        fixture.Events = [fixture.Event(1, "tray-failed")];
        await fixture.Poll();
        Assert.AreEqual("notification_display_unavailable", fixture.Notifications.Current.QueryError);
        fixture.DisplayFailure = false;
        for (var i = 0; i < 4; i++)
        { fixture.Time.Advance(TimeSpan.FromSeconds(30)); await fixture.Poll(); }
        fixture.Events = [fixture.Event(2, "next")];
        await fixture.Poll();
        Assert.AreEqual(1, fixture.Shown.Count);
        Assert.IsNull(fixture.Notifications.Current.QueryError);
    }

    private static void Activate(DesktopDiagnosticNotifications notifications)
    {
        notifications.UpdateConnection(new(DesktopConnectionStatus.Connected, Instance, 0, null, null));
        notifications.SetTrayAvailable(true); notifications.SetEnabled(true);
    }

    private static DiagnosticEventContract CreateEvent(DateTimeOffset time, string instance, long sequence, string subject) => new()
    {
        ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
        EventId = $"{instance}-{sequence}", InstanceId = instance, RuleId = DiagnosticRuleIds.HighCpu,
        RuleVersion = "1", Severity = DiagnosticSeverities.Warning, State = DiagnosticStates.Active,
        SubjectId = subject, ObservationSequence = sequence,
        Hysteresis = new() { ActivateWhen = "high", RecoverWhen = "low" }, Debounce = new(),
        EvidenceWindow = new() { FromUtc = time, ToUtc = time, SampleCount = 1 },
        FirstSeenUtc = time, LastSeenUtc = time, Confidence = 1, Evidence = [],
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));
        public DesktopDiagnosticNotifications Notifications { get; }
        public List<DesktopDiagnosticNotification> Shown { get; } = [];
        public DiagnosticEventContract[] Events { get; set; } = [];
        public string CurrentInstance { get; set; } = Instance;
        public int Queries { get; private set; }
        public int DisplayAttempts { get; private set; }
        public bool AcceptDisplay { get; set; } = true;
        public bool DisplayFailure { get; set; }
        public long Sequence { get; set; }
        public string? Failure { get; set; }
        public DiagnosticQueryContract? LastQuery { get; private set; }
        public TaskCompletionSource<DiagnosticsContract>? Deferred { get; set; }

        public Fixture()
        {
            Notifications = new((_, query, _) =>
            {
                Queries++; LastQuery = query;
                if (Failure == "failure") throw new IOException("test query unavailable");
                return Deferred?.Task ?? Task.FromResult(Response(query));
            }, (notification, token) =>
            {
                token.ThrowIfCancellationRequested(); DisplayAttempts++;
                if (DisplayFailure) throw new ExternalException("test tray unavailable");
                if (AcceptDisplay) Shown.Add(notification);
                return Task.FromResult(AcceptDisplay);
            }, Time);
        }

        public DiagnosticsContract Response(DiagnosticQueryContract query) => new()
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
            InstanceId = Failure == "instance" ? "other-agent" : CurrentInstance,
            Query = Failure == "query" ? query with { MaxEvents = query.MaxEvents - 1 } : query,
            EventCount = Events.Length + (Failure == "count" ? 1 : 0),
            Events = Events, Truncated = Failure == "truncated",
        };

        public void Connect() => Notifications.UpdateConnection(
            new(DesktopConnectionStatus.Connected, CurrentInstance, 0, null, null));
        public async Task Enable()
        {
            Connect(); Notifications.SetTrayAvailable(true); Notifications.SetEnabled(true);
            await Poll();
        }
        public Task Poll() => Notifications.PollOnceAsync();
        public DiagnosticEventContract Event(long sequence, string subject) =>
            CreateEvent(Time.GetUtcNow(), CurrentInstance, sequence, subject);
        public ValueTask DisposeAsync() => Notifications.DisposeAsync();
    }

    private sealed class NotificationTimeProvider : TimeProvider
    {
        private readonly List<NotificationTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new NotificationTimer(this, callback, state, dueTime);
            _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire(_ticks);
        }
        private sealed class NotificationTimer(NotificationTimeProvider owner, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private long _deadline = owner._ticks + due.Ticks;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { _deadline = owner._ticks + dueTime.Ticks; return !_disposed; }
            public void Fire(long now)
            { if (!_disposed && now >= _deadline) { _disposed = true; callback(state); } }
            public void Dispose() { _disposed = true; owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
