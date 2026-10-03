using System.Text.Json;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Agent;

/// <summary>One bounded owner of this Agent's optional hardware period multiplier.</summary>
internal sealed class AgentAdaptiveSchedulingController : IDisposable
{
    private readonly object _gate = new();
    private readonly ProviderSamplingMode _samplingMode;
    private readonly SnapshotAssembler _assembler;
    private readonly ProviderDescriptor[] _hardware;
    private readonly TimeProvider _timeProvider;
    private ITimer? _timer;
    private int? _originalMultiplier;
    private int _appliedMultiplier;
    private long? _expiresTimestamp;
    private DateTimeOffset? _expiresAtUtc;
    private string _preference = AdaptiveSchedulingPreferences.Responsiveness;
    private string _decision = AdaptiveSchedulingDecisionCodes.Disabled;
    private string _reason = "自动调度默认关闭。选择偏好与期限后，可减少本软件的可选硬件请求。";
    private long? _lastCpuSequence;
    private double? _lastCpuElapsed;
    private long? _lastPowerSequence;
    private double? _lastPowerElapsed;
    private double? _pressureStarted;
    private double? _recoveryStarted;
    private double? _lastSnapshotElapsed;
    private double? _cpu;
    private double? _memory;
    private string? _powerSource;
    private bool? _batterySaver;
    private bool _powerReduced;
    private bool _disposed;

    public AgentAdaptiveSchedulingController(ProviderSamplingMode samplingMode,
        SnapshotAssembler assembler, IEnumerable<ProviderDescriptor> descriptors,
        TimeProvider? timeProvider = null)
    {
        _samplingMode = samplingMode;
        _assembler = assembler;
        _hardware = descriptors.Where(item => item.GroupId is GroupIds.Gpu or GroupIds.Sensors).ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AdaptiveSchedulingContract Read()
    {
        lock (_gate)
        {
            CheckLease();
            return BuildStatus();
        }
    }

    public AdaptiveSchedulingContract Set(AdaptiveSchedulingRequestContract request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(request.InstanceId, _assembler.InstanceId) ||
            !AdaptiveSchedulingPreferences.All.Contains(request.Preference, StringComparer.Ordinal) ||
            !AdaptiveSchedulingLimits.DurationSeconds.Contains(request.DurationSeconds))
            throw new ArgumentException("Invalid current-session scheduling request.", nameof(request));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            CheckLease();
            if (!request.Enabled)
            {
                if (_originalMultiplier is not null)
                    EndLease(AdaptiveSchedulingDecisionCodes.Restored, "自动调度已关闭，已恢复开启前的硬件请求周期。" );
                return BuildStatus();
            }
            if (_originalMultiplier is null)
            {
                _originalMultiplier = _samplingMode.HardwarePeriodMultiplier;
                _appliedMultiplier = _originalMultiplier.Value;
                var duration = TimeSpan.FromSeconds(request.DurationSeconds);
                _expiresTimestamp = checked(_timeProvider.GetTimestamp() + SchedulerMath.ToTimestampTicks(_timeProvider, duration));
                _expiresAtUtc = _timeProvider.GetUtcNow() + duration;
                _timer = _timeProvider.CreateTimer(_ => OnLeaseExpired(), null, duration, Timeout.InfiniteTimeSpan);
                _preference = request.Preference;
                ResetEvidence();
                _decision = AdaptiveSchedulingDecisionCodes.Observing;
                _reason = "已启用限时自动调度，等待足够的新鲜观测。到期由 Agent 恢复原周期。";
            }
            else if (_preference != request.Preference)
            {
                if (!ApplyMultiplier(_originalMultiplier.Value)) return BuildStatus();
                _preference = request.Preference;
                ResetEvidence();
                _decision = AdaptiveSchedulingDecisionCodes.Observing;
                _reason = "偏好已切换，重新观察；保持原到期时间，不续期。";
            }
            ObserveCore(_assembler.Read());
            return BuildStatus();
        }
    }

    public void Observe(AgentSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckLease();
            if (_originalMultiplier is not null) ObserveCore(snapshot);
        }
    }

    private void ObserveCore(AgentSnapshot snapshot)
    {
        if (_hardware.Length == 0)
        {
            _decision = AdaptiveSchedulingDecisionCodes.NoHardware;
            _reason = "本 Agent 未注册 GPU / 温度采集，当前开关没有实际硬件采集效果。";
            return;
        }
        if (snapshot.InstanceId != _assembler.InstanceId || snapshot.ElapsedSeconds is not { } elapsed ||
            !double.IsFinite(elapsed) || elapsed < 0 ||
            _lastSnapshotElapsed is { } last && (elapsed < last || elapsed - last > 3))
        {
            ResetEvidence();
            Unknown("观测会话、单调时间或连续性不足，已恢复原周期并重新观察。" );
            if (snapshot.InstanceId == _assembler.InstanceId && snapshot.ElapsedSeconds is { } known && double.IsFinite(known) && known >= 0)
                _lastSnapshotElapsed = known;
            return;
        }
        _lastSnapshotElapsed = elapsed;
        _cpu = TryScalar(snapshot, GroupIds.SystemCpu, MetricIds.SystemCpuUtilization, out var cpuGroup)
            is { } cpu && cpu is >= 0 and <= 100 ? cpu : null;
        _memory = TryScalar(snapshot, GroupIds.Memory, MetricIds.MemoryUtilization, out _)
            is { } memory && memory is >= 0 and <= 100 ? memory : null;
        var powerGroup = FreshGroup(snapshot, GroupIds.Power);
        var powerData = powerGroup?.ReadOnlyData;
        _powerSource = ReadString(powerData, "powerSource") is "ac" or "battery" ? ReadString(powerData, "powerSource") : null;
        _batterySaver = ReadBoolean(powerData, "batterySaver");
        if (_preference == AdaptiveSchedulingPreferences.EnergySaving &&
            (_powerSource == "battery" || _batterySaver == true))
        {
            if (ApplyMultiplier(Math.Max(_originalMultiplier!.Value, 12)))
            {
                _pressureStarted = _recoveryStarted = null;
                _powerReduced = true;
                _decision = AdaptiveSchedulingDecisionCodes.Reduced;
                _reason = _powerSource == "battery"
                    ? "新鲜电源观测显示正在使用电池，GPU / 温度请求采用节能周期。"
                    : "新鲜电源观测显示系统省电已开启，GPU / 温度请求采用节能周期。";
            }
            return;
        }
        if (_preference == AdaptiveSchedulingPreferences.EnergySaving && _powerReduced &&
            _powerSource == "ac" && _batterySaver == false)
        {
            if (!IsNewObservation(powerGroup!, ref _lastPowerSequence, ref _lastPowerElapsed, 15)) return;
            _recoveryStarted ??= powerGroup!.ObservedElapsedSeconds;
            if (powerGroup!.ObservedElapsedSeconds!.Value - _recoveryStarted!.Value >= 20)
            {
                if (ApplyMultiplier(_originalMultiplier!.Value))
                {
                    _recoveryStarted = null;
                    _powerReduced = false;
                    _decision = AdaptiveSchedulingDecisionCodes.Observing;
                    _reason = "交流供电且系统省电关闭已持续 20 秒，已恢复原周期。";
                }
            }
            else
            {
                _decision = AdaptiveSchedulingDecisionCodes.Recovering;
                _reason = "已观测到交流供电且省电关闭，等待连续 20 秒后恢复原周期。";
            }
            return;
        }
        if (_preference == AdaptiveSchedulingPreferences.EnergySaving && _powerReduced)
        {
            Unknown("电源观测缺测、陈旧或状态未知，已退出电源减频并恢复原周期；CPU 压力需要重新连续观察。" );
            return;
        }
        if (_cpu is null || cpuGroup is null)
        {
            Unknown("CPU 观测缺测、陈旧或无效，无法判断持续压力；已恢复原周期。" );
            return;
        }
        if (!IsNewObservation(cpuGroup, ref _lastCpuSequence, ref _lastCpuElapsed, 3)) return;
        var observed = cpuGroup.ObservedElapsedSeconds!.Value;
        var (activate, activateSeconds, recover, recoverSeconds, multiplier) = _preference switch
        {
            AdaptiveSchedulingPreferences.Responsiveness => (70.0, 10.0, 55.0, 20.0, 3),
            AdaptiveSchedulingPreferences.Throughput => (85.0, 15.0, 70.0, 30.0, 6),
            _ => (80.0, 15.0, 65.0, 30.0, 6),
        };
        if (_cpu >= activate)
        {
            _recoveryStarted = null;
            _pressureStarted ??= observed;
            if (observed - _pressureStarted.Value >= activateSeconds)
            {
                if (ApplyMultiplier(Math.Max(_originalMultiplier!.Value, multiplier)))
                {
                    _decision = AdaptiveSchedulingDecisionCodes.Reduced;
                    _reason = $"新鲜 CPU ≥{activate:0}% 已持续 {activateSeconds:0} 秒，减少可选硬件请求。";
                }
            }
            else
            {
                _decision = AdaptiveSchedulingDecisionCodes.Debouncing;
                _reason = $"CPU ≥{activate:0}%，等待连续 {activateSeconds:0} 秒的新观测后减频。";
            }
        }
        else if (_cpu <= recover)
        {
            _pressureStarted = null;
            if (_appliedMultiplier == _originalMultiplier)
            {
                _recoveryStarted = null;
                _decision = AdaptiveSchedulingDecisionCodes.Observing;
                _reason = $"CPU 当前为 {_cpu:0.#}%，保持原周期并继续观察。";
            }
            else
            {
                _recoveryStarted ??= observed;
                if (observed - _recoveryStarted.Value >= recoverSeconds)
                {
                    if (ApplyMultiplier(_originalMultiplier!.Value))
                    {
                        _recoveryStarted = null;
                        _decision = AdaptiveSchedulingDecisionCodes.Observing;
                        _reason = $"新鲜 CPU ≤{recover:0}% 已持续 {recoverSeconds:0} 秒，已恢复原周期。";
                    }
                }
                else
                {
                    _decision = AdaptiveSchedulingDecisionCodes.Recovering;
                    _reason = $"CPU 已降至恢复门限，等待连续 {recoverSeconds:0} 秒的新观测。";
                }
            }
        }
        else
        {
            _pressureStarted = _recoveryStarted = null;
            _decision = _appliedMultiplier == _originalMultiplier
                ? AdaptiveSchedulingDecisionCodes.Observing : AdaptiveSchedulingDecisionCodes.Reduced;
            _reason = $"CPU 当前为 {_cpu:0.#}%，处于滞环区间，保持当前请求周期。";
        }
    }

    private bool IsNewObservation(SnapshotGroup group, ref long? lastSequence,
        ref double? lastElapsed, double maximumGap)
    {
        var sequence = group.ObservationSequence!.Value;
        var observed = group.ObservedElapsedSeconds!.Value;
        if (lastSequence is { } previousSequence && sequence <= previousSequence)
        {
            if (sequence < previousSequence) _pressureStarted = _recoveryStarted = null;
            return false;
        }
        if (lastElapsed is { } previous && (observed <= previous || observed - previous > maximumGap))
        {
            _pressureStarted = _recoveryStarted = null;
            if (maximumGap == 3 && !ApplyMultiplier(_originalMultiplier!.Value)) return false;
        }
        lastSequence = sequence;
        lastElapsed = observed;
        return true;
    }

    private void Unknown(string reason)
    {
        _pressureStarted = _recoveryStarted = null;
        _powerReduced = false;
        _lastCpuSequence = _lastPowerSequence = null;
        _lastCpuElapsed = _lastPowerElapsed = null;
        if (ApplyMultiplier(_originalMultiplier!.Value))
        {
            _decision = AdaptiveSchedulingDecisionCodes.InsufficientData;
            _reason = reason;
        }
    }

    private bool ApplyMultiplier(int multiplier)
    {
        if (_samplingMode.TryChangeHardwarePeriodMultiplier(_appliedMultiplier, multiplier))
        {
            _appliedMultiplier = multiplier;
            return true;
        }
        AbandonConflict();
        return false;
    }

    private void CheckLease()
    {
        if (_originalMultiplier is null) return;
        if (_samplingMode.HardwarePeriodMultiplier != _appliedMultiplier) AbandonConflict();
        else if (_expiresTimestamp is { } deadline && _timeProvider.GetTimestamp() >= deadline)
            EndLease(AdaptiveSchedulingDecisionCodes.Expired, "限时自动调度已到期，已恢复开启前的硬件请求周期。" );
    }

    private void EndLease(string decision, string reason)
    {
        if (_originalMultiplier is { } original &&
            !_samplingMode.TryChangeHardwarePeriodMultiplier(_appliedMultiplier, original))
        {
            AbandonConflict();
            return;
        }
        ClearLease();
        _decision = decision;
        _reason = reason;
    }

    private void AbandonConflict()
    {
        ClearLease();
        _decision = AdaptiveSchedulingDecisionCodes.Conflict;
        _reason = "硬件周期已被其他控制更改，自动调度已停止；保留当前设置，未覆盖新值。";
    }

    private void ClearLease()
    {
        _originalMultiplier = null;
        _expiresTimestamp = null;
        _expiresAtUtc = null;
        _timer?.Dispose();
        _timer = null;
        ResetEvidence();
    }

    private void ResetEvidence()
    {
        _lastCpuSequence = _lastPowerSequence = null;
        _lastCpuElapsed = _lastPowerElapsed = null;
        _pressureStarted = _recoveryStarted = _lastSnapshotElapsed = null;
        _cpu = _memory = null;
        _powerSource = null;
        _batterySaver = null;
        _powerReduced = false;
    }

    private void OnLeaseExpired()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CheckLease();
            if (_originalMultiplier is not null && _expiresTimestamp is { } deadline)
                _timer?.Change(_timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), deadline), Timeout.InfiniteTimeSpan);
        }
    }

    private AdaptiveSchedulingContract BuildStatus()
    {
        var enabled = _originalMultiplier is not null;
        var paused = enabled && _hardware.Length > 0 && _samplingMode.OptionalHardwarePaused;
        return new AdaptiveSchedulingContract
        {
            Supported = true,
            Enabled = enabled,
            CanRestore = enabled,
            InstanceId = _assembler.InstanceId,
            Preference = _preference,
            DecisionCode = paused ? AdaptiveSchedulingDecisionCodes.ManualPaused : _decision,
            Reason = paused ? "手动轻量模式正在暂停 GPU / 温度请求；自动调度只保存周期，手动恢复后按当前周期采集。" : _reason,
            ImpactDescription = _hardware.Length == 0
                ? "本 Agent 未注册 GPU / 温度采集，开关没有实际硬件采集效果；仅本次 Agent 会话有效。"
                : "仅调整本软件 GPU / 温度请求周期，基础与进程采集保持原周期。硬件观测会更稀疏；交互、任务吞吐、温度及续航改善尚未验证。",
            RemainingSeconds = enabled && _expiresTimestamp is { } deadline
                ? Math.Max(0, _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), deadline).TotalSeconds) : null,
            ExpiresAtUtc = _expiresAtUtc,
            Groups = Array.AsReadOnly(_hardware.Select(descriptor => new AdaptiveSchedulingGroupContract
            {
                GroupId = descriptor.GroupId,
                BasePeriodMs = checked((int)Math.Round(descriptor.DefaultPeriod.TotalMilliseconds)),
                EffectivePeriodMs = checked((int)Math.Round(_samplingMode.GetEffectivePeriod(descriptor).TotalMilliseconds)),
                Paused = _samplingMode.IsPaused(descriptor.GroupId),
            }).ToArray()),
            CpuPercent = _cpu,
            MemoryPercent = _memory,
            PowerSource = _powerSource,
            BatterySaver = _batterySaver,
            Recommendations = Recommendations(),
        };
    }

    private IReadOnlyList<string> Recommendations() => _preference switch
    {
        AdaptiveSchedulingPreferences.Responsiveness =>
        ["打开 CPU 排行，查看高占用进程实例；确认其用途后，在应用内暂停暂时不需要的后台任务。",
            "如交互仍不流畅，观察该前台任务的响应或帧时间；当前 CPU 压力不能证明某个后台进程造成卡顿。"],
        AdaptiveSchedulingPreferences.Throughput =>
        ["编译、渲染或批处理时，记录同一任务的完成时间，并检查 CPU 排行中的并发任务。",
            "在任务应用内减少无关并发工作，再对比完成时间；高 CPU 也可能表示任务正在有效工作。"],
        _ => ["使用电池时，可在 Windows 电源设置中选择省电，并在应用内暂停非必要后台工作。",
            "以同等任务完成量比较续航或能耗；减少本软件硬件请求本身不能证明省电收益。"],
    };

    private static SnapshotGroup? FreshGroup(AgentSnapshot snapshot, string groupId)
    {
        if (!snapshot.Groups.TryGetValue(groupId, out var group) || group.Freshness != FreshnessStates.Fresh ||
            group.Availability is not (AvailabilityStates.Available or AvailabilityStates.Partial) ||
            group.ObservationSequence is not > 0 || group.ObservedElapsedSeconds is not { } observed ||
            !double.IsFinite(observed) || observed < 0 || snapshot.ElapsedSeconds is not { } elapsed || observed > elapsed)
            return null;
        return group;
    }

    private static double? TryScalar(AgentSnapshot snapshot, string groupId, string metricId, out SnapshotGroup? group)
    {
        group = FreshGroup(snapshot, groupId);
        if (group?.ReadOnlyData is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object ||
            !metrics.TryGetProperty(metricId, out var metric) || metric.ValueKind != JsonValueKind.Object ||
            !metric.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.String || unit.GetString() != Units.Percent ||
            !metric.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number)) return null;
        return number;
    }

    private static string? ReadString(JsonElement? data, string property) =>
        data is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool? ReadBoolean(JsonElement? data, string property) =>
        data is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var field) &&
        field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_originalMultiplier is not null)
                EndLease(AdaptiveSchedulingDecisionCodes.Restored, "Agent 调度已停止，已恢复原硬件周期。" );
            _disposed = true;
        }
    }
}
