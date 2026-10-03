using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class AdaptiveSchedulingTests
{
    [TestMethod]
    [DataRow(AdaptiveSchedulingPreferences.Responsiveness, 70.0, 10, 3, 55.0, 20)]
    [DataRow(AdaptiveSchedulingPreferences.Throughput, 85.0, 15, 6, 70.0, 30)]
    [DataRow(AdaptiveSchedulingPreferences.EnergySaving, 80.0, 15, 6, 65.0, 30)]
    public void PreferencesApplyDistinctContinuousCpuRulesAndRecover(string preference,
        double high, int duration, int multiplier, double low, int recovery)
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(preference);
        fixture.CpuFor(duration, high);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(1, high);
        Assert.AreEqual(multiplier, fixture.Mode.HardwarePeriodMultiplier);
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.Reduced, fixture.Controller.Read().DecisionCode);
        fixture.CpuFor(recovery, low);
        Assert.AreEqual(multiplier, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(1, low);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        Assert.IsTrue(fixture.Controller.Read().Enabled);
    }

    [TestMethod]
    public void BatteryAndSaverUseEnergyPeriodAndMainsNeedsNewContinuousEvidence()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving);
        fixture.Power("battery", false);
        Assert.AreEqual(12, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Power("ac", true);
        Assert.AreEqual(12, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Power("ac", false);
        for (var index = 0; index < 20; index++)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            fixture.Controller.Observe(fixture.Assembler.Read());
            if ((index + 1) % 5 == 0) fixture.Power("ac", false);
        }
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        Assert.AreEqual("ac", fixture.Controller.Read().PowerSource);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("stale")]
    public void LostPowerEvidenceImmediatelyRestoresBeforeCpuPolicyCanTakeOver(string failure)
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving);
        fixture.Power("battery", false);
        fixture.CpuFor(1, 90);
        Assert.AreEqual(12, fixture.Mode.HardwarePeriodMultiplier);
        if (failure == "unknown") fixture.Power("unknown", null);
        else
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(16));
            fixture.Cpu(90);
        }
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        Assert.IsNull(fixture.Controller.Read().PowerSource);
        fixture.CpuFor(15, 90);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(1, 90);
        Assert.AreEqual(6, fixture.Mode.HardwarePeriodMultiplier);
    }

    [TestMethod]
    public void DuplicateUnknownAndNonFiniteCpuCannotAccumulateOrKeepPressure()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.Responsiveness);
        fixture.CpuFor(11, 90);
        Assert.AreEqual(3, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Cpu(null);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        Assert.IsNull(fixture.Controller.Read().CpuPercent);
        fixture.Cpu(double.NaN);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Cpu(90);
        var held = fixture.Assembler.Read();
        for (var index = 0; index < 30; index++)
            fixture.Controller.Observe(held with { ElapsedSeconds = held.ElapsedSeconds + index * 0.05 });
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Time.Advance(TimeSpan.FromSeconds(4));
        fixture.Controller.Observe(fixture.Assembler.Read());
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.InsufficientData, fixture.Controller.Read().DecisionCode);
    }

    [TestMethod]
    public void MonotonicGapAndWrongSessionResetEvidenceWhileUtcRollbackDoesNot()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.Responsiveness);
        fixture.CpuFor(9, 90);
        fixture.Time.MoveUtc(TimeSpan.FromDays(-1));
        fixture.CpuFor(2, 90);
        Assert.AreEqual(3, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Controller.Observe(fixture.Assembler.Read() with { InstanceId = "other-agent" });
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(9, 90);
        fixture.Time.Advance(TimeSpan.FromSeconds(4));
        fixture.Cpu(90);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(10, 90);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        fixture.CpuFor(1, 90);
        Assert.AreEqual(3, fixture.Mode.HardwarePeriodMultiplier);
    }

    [TestMethod]
    public void RepeatedEnableAndPreferenceChangeKeepOriginalDeadlineAndValue()
    {
        using var fixture = new SchedulingFixture(originalMultiplier: 3);
        fixture.Enable(AdaptiveSchedulingPreferences.Throughput, 900);
        fixture.CpuFor(16, 90);
        Assert.AreEqual(6, fixture.Mode.HardwarePeriodMultiplier);
        var before = fixture.Controller.Read();
        fixture.Enable(AdaptiveSchedulingPreferences.Throughput, 3600);
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving, 3600);
        Assert.AreEqual(before.ExpiresAtUtc, fixture.Controller.Read().ExpiresAtUtc);
        fixture.Power("battery", false);
        Assert.AreEqual(12, fixture.Mode.HardwarePeriodMultiplier);
        fixture.Disable();
        Assert.AreEqual(3, fixture.Mode.HardwarePeriodMultiplier);
        Assert.IsFalse(fixture.Controller.Read().Enabled);
    }

    [TestMethod]
    public void LeaseTimerRestoresWithoutDesktopReadsOrObservations()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving, 900);
        fixture.Power("battery", false);
        fixture.Time.MoveUtc(TimeSpan.FromDays(-3));
        fixture.Time.Advance(TimeSpan.FromSeconds(900));
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier, "The timer restores before any status query.");
        var status = fixture.Controller.Read();
        Assert.IsFalse(status.Enabled || status.CanRestore);
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.Expired, status.DecisionCode);
    }

    [TestMethod]
    public void ExternalChangeIsNeverOverwrittenAtExpiryOrExplicitRestore()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving, 900);
        fixture.Power("battery", false);
        Assert.IsTrue(fixture.Mode.TryChangeHardwarePeriodMultiplier(12, 6));
        Assert.IsFalse(fixture.Mode.TryChangeHardwarePeriodMultiplier(12, 1));
        fixture.Time.Advance(TimeSpan.FromSeconds(900));
        fixture.Disable();
        Assert.AreEqual(6, fixture.Mode.HardwarePeriodMultiplier);
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.Conflict, fixture.Controller.Read().DecisionCode);
    }

    [TestMethod]
    public void ManualPauseAlwaysWinsAndLeaseDoesNotRestoreThePauseOwner()
    {
        using var fixture = new SchedulingFixture();
        var light = new AgentLightModeController(fixture.Mode, fixture.Assembler, fixture.Descriptors);
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving, 900);
        fixture.Power("battery", false);
        light.Set(true, CancellationToken.None);
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.ManualPaused, fixture.Controller.Read().DecisionCode);
        Assert.IsTrue(fixture.Controller.Read().Groups.All(group => group.Paused));
        fixture.Time.Advance(TimeSpan.FromSeconds(900));
        Assert.IsTrue(fixture.Mode.OptionalHardwarePaused);
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
        light.Set(false, CancellationToken.None);
        Assert.IsFalse(fixture.Mode.OptionalHardwarePaused);
    }

    [TestMethod]
    public void MissingHardwareAndInvalidRequestAreExplicitAndLeaveSettingsAlone()
    {
        using var fixture = new SchedulingFixture(withHardware: false);
        Assert.IsFalse(fixture.Controller.Read().Enabled);
        fixture.Enable(AdaptiveSchedulingPreferences.Responsiveness);
        var status = fixture.Controller.Read();
        Assert.IsTrue(status.Supported);
        Assert.AreEqual(AdaptiveSchedulingDecisionCodes.NoHardware, status.DecisionCode);
        Assert.HasCount(0, status.Groups);
        StringAssert.Contains(status.ImpactDescription, "没有实际硬件采集效果");
        foreach (var request in new[]
        {
            fixture.Request() with { InstanceId = "different" },
            fixture.Request() with { Preference = "unknown" },
            fixture.Request() with { DurationSeconds = 0 },
            fixture.Request() with { DurationSeconds = 3601 },
        })
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Controller.Set(request, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => fixture.Controller.Set(fixture.Request(), cancelled.Token));
        Assert.AreEqual(1, fixture.Mode.HardwarePeriodMultiplier);
    }

    [TestMethod]
    public void RegisteredBasePeriodsAndEveryBasicGroupRemainAccurate()
    {
        using var fixture = new SchedulingFixture();
        fixture.Enable(AdaptiveSchedulingPreferences.EnergySaving);
        fixture.Power("battery", false);
        var status = fixture.Controller.Read();
        Assert.AreEqual(2000, status.Groups.Single(item => item.GroupId == GroupIds.Gpu).BasePeriodMs);
        Assert.AreEqual(24000, status.Groups.Single(item => item.GroupId == GroupIds.Gpu).EffectivePeriodMs);
        Assert.AreEqual(36000, status.Groups.Single(item => item.GroupId == GroupIds.Sensors).EffectivePeriodMs);
        foreach (var group in new[] { GroupIds.SystemCpu, GroupIds.Memory, GroupIds.Network, GroupIds.DiskIo, GroupIds.Processes })
        {
            var descriptor = SchedulingFixture.Descriptor(group, 2);
            Assert.AreEqual(descriptor.DefaultPeriod, fixture.Mode.GetEffectivePeriod(descriptor));
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Mode.HardwarePeriodMultiplier = 2);
    }

    [TestMethod]
    public void SlowFastSlowCannotReviveOldObservationAndSamplerKeepsActualPeriod()
    {
        var clock = new AdaptiveTimeProvider();
        var descriptor = SchedulingFixture.Descriptor(GroupIds.Gpu, 1);
        var mode = new ProviderSamplingMode();
        var assembler = new SnapshotAssembler([descriptor], clock) { SamplingMode = mode };
        Publish(assembler, descriptor, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(FreshnessStates.Stale, assembler.Read().Groups[GroupIds.Gpu].Freshness);
        mode.HardwarePeriodMultiplier = 6;
        Assert.AreEqual(FreshnessStates.Stale, assembler.Read().Groups[GroupIds.Gpu].Freshness);
        Publish(assembler, descriptor, clock, 6);
        var slow = assembler.Read();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(FreshnessStates.Fresh, assembler.Read().Groups[GroupIds.Gpu].Freshness);
        mode.HardwarePeriodMultiplier = 1;
        var tightened = assembler.Read();
        Assert.AreEqual(FreshnessStates.Stale, tightened.Groups[GroupIds.Gpu].Freshness);
        mode.HardwarePeriodMultiplier = 6;
        var slowAgain = assembler.Read();
        Assert.AreEqual(FreshnessStates.Stale, slowAgain.Groups[GroupIds.Gpu].Freshness);
        Assert.AreEqual(slow.Groups[GroupIds.Gpu].ObservationSequence, slowAgain.Groups[GroupIds.Gpu].ObservationSequence);
        Assert.AreEqual(slow.Groups[GroupIds.Gpu].ObservedElapsedSeconds, slowAgain.Groups[GroupIds.Gpu].ObservedElapsedSeconds);
        Assert.AreEqual(6.0, slowAgain.Groups[GroupIds.Sampler].Data!["metrics"]![MetricIds.SamplerIntervalSeconds]!["value"]!.GetValue<double>());
        Publish(assembler, descriptor, clock, 12); // Actual expected interval includes failure backoff.
        Assert.AreEqual(12.0, assembler.Read().Groups[GroupIds.Sampler].Data!["metrics"]![MetricIds.SamplerIntervalSeconds]!["value"]!.GetValue<double>());
        var json = AgentJson.Serialize(assembler.Read());
        using var wire = JsonDocument.Parse(json);
        Assert.IsFalse(wire.RootElement.GetProperty("groups").GetProperty(GroupIds.Gpu).TryGetProperty("observationPeriodBudgetSeconds", out _));
        Assert.IsFalse(wire.RootElement.GetProperty("groups").GetProperty(GroupIds.Gpu).TryGetProperty("expectedPeriodSeconds", out _));
    }

    private static void Publish(SnapshotAssembler assembler, ProviderDescriptor descriptor, AdaptiveTimeProvider clock, double period)
    {
        var now = clock.GetUtcNow();
        assembler.PublishAsync(DelegateProvider.Available(descriptor, now),
            new ProviderExecution(descriptor, now, now, now, 0, 0, 0, 0)
            {
                StartedTimestamp = clock.GetTimestamp(), CompletedTimestamp = clock.GetTimestamp(), ExpectedPeriodSeconds = period,
            }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class SchedulingFixture : IDisposable
    {
        public AdaptiveTimeProvider Time { get; } = new();
        public ProviderSamplingMode Mode { get; } = new();
        public SnapshotAssembler Assembler { get; }
        public AgentAdaptiveSchedulingController Controller { get; }
        public ProviderDescriptor[] Descriptors { get; }
        public SchedulingFixture(int originalMultiplier = 1, bool withHardware = true)
        {
            Descriptors = [Descriptor(GroupIds.SystemCpu, 1), Descriptor(GroupIds.Memory, 1), Descriptor(GroupIds.Power, 5),
                .. withHardware ? new[] { Descriptor(GroupIds.Gpu, 2), Descriptor(GroupIds.Sensors, 3) } : []];
            Mode.HardwarePeriodMultiplier = originalMultiplier;
            Assembler = new SnapshotAssembler(Descriptors, Time) { SamplingMode = Mode };
            Controller = new AgentAdaptiveSchedulingController(Mode, Assembler, Descriptors, Time);
        }
        public static ProviderDescriptor Descriptor(string group, double seconds) =>
            new(group, "fake." + group, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1), "user", "low");
        public AdaptiveSchedulingRequestContract Request(string preference = AdaptiveSchedulingPreferences.Responsiveness,
            bool enabled = true, int duration = 1800) => new()
        { InstanceId = Assembler.InstanceId, Preference = preference, Enabled = enabled, DurationSeconds = duration };
        public void Enable(string preference, int duration = 1800) => Controller.Set(Request(preference, duration: duration), CancellationToken.None);
        public void Disable() => Controller.Set(Request(enabled: false), CancellationToken.None);
        public void CpuFor(int seconds, double? value)
        { for (var index = 0; index < seconds; index++) { Time.Advance(TimeSpan.FromSeconds(1)); Cpu(value); } }
        public void Cpu(double? value)
        {
            PublishData(GroupIds.SystemCpu, new JsonObject { ["metrics"] = new JsonObject
            { [MetricIds.SystemCpuUtilization] = MetricJson.Value(value, Units.Percent, "fake.cpu") } });
            Controller.Observe(Assembler.Read());
        }
        public void Power(string source, bool? saver)
        {
            PublishData(GroupIds.Power, new JsonObject { ["powerSource"] = source,
                ["batterySaver"] = saver is { } value ? JsonValue.Create(value) : null });
            Controller.Observe(Assembler.Read());
        }
        private void PublishData(string groupId, JsonObject data)
        {
            var descriptor = Descriptors.Single(item => item.GroupId == groupId);
            var now = Time.GetUtcNow();
            Assembler.PublishAsync(new ProviderResult(groupId, descriptor.ProviderId, now, AvailabilityStates.Available,
                ProviderCoverage.Complete, [], data), new ProviderExecution(descriptor, now, now, now, 0, 0, 0, 0)
            { StartedTimestamp = Time.GetTimestamp(), CompletedTimestamp = Time.GetTimestamp() }, CancellationToken.None).GetAwaiter().GetResult();
        }
        public void Dispose() => Controller.Dispose();
    }
}

internal sealed class AdaptiveTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<AdaptiveTimer> _timers = [];
    private long _ticks;
    private DateTimeOffset _utc = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Volatile.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
    public void MoveUtc(TimeSpan difference) { lock (_gate) _utc += difference; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new AdaptiveTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }
    public void Advance(TimeSpan duration)
    {
        AdaptiveTimer[] timers;
        lock (_gate) { _ticks += duration.Ticks; _utc += duration; timers = _timers.ToArray(); }
        foreach (var timer in timers) timer.Fire();
    }
    public bool HasTimerAt(double seconds) { lock (_gate) return _timers.Any(timer => timer.DueTicks == TimeSpan.FromSeconds(seconds).Ticks); }
    private sealed class AdaptiveTimer : ITimer
    {
        private readonly AdaptiveTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private bool _disposed;
        public long DueTicks { get; private set; }
        public AdaptiveTimer(AdaptiveTimeProvider owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        { _owner = owner; _callback = callback; _state = state; Change(due, period); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (_disposed) return false;
                _period = period;
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_owner.GetTimestamp() + dueTime.Ticks);
                return true;
            }
        }
        public void Fire()
        {
            lock (_owner._gate)
            {
                if (_disposed || _owner.GetTimestamp() < DueTicks) return;
                DueTicks = _period == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_owner.GetTimestamp() + _period.Ticks);
            }
            _callback(_state);
        }
        public void Dispose() { lock (_owner._gate) { _disposed = true; _owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
