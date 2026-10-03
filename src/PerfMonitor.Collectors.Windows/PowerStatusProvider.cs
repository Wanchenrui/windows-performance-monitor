using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.Collectors.Windows;

internal sealed record PowerStatusRead(
    byte AcLineStatus,
    byte BatteryFlag,
    byte BatteryLifePercent,
    byte SystemStatusFlag,
    uint BatteryLifeTime,
    uint BatteryFullLifeTime);

internal interface IPowerStatusSource
{
    PowerStatusRead Read();
}

internal sealed class SystemPowerStatusSource : IPowerStatusSource
{
    public PowerStatusRead Read()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error());
        }

        return new PowerStatusRead(
            status.AcLineStatus,
            status.BatteryFlag,
            status.BatteryLifePercent,
            status.SystemStatusFlag,
            status.BatteryLifeTime,
            status.BatteryFullLifeTime);
    }
}

public sealed class PowerStatusProvider : IMetricProvider
{
    private const byte AcOffline = 0;
    private const byte AcOnline = 1;
    private const byte UnknownByte = byte.MaxValue;
    private const byte BatteryCharging = 8;
    private const byte NoSystemBattery = 128;
    private const uint UnknownSeconds = uint.MaxValue;
    private readonly IPowerStatusSource _source;
    private readonly IBatteryStateSource? _fallback;
    private long? _fallbackFailedAt;
    private string? _fallbackFailureCode;

    public PowerStatusProvider()
        : this(new SystemPowerStatusSource(), new SystemBatteryStateSource())
    {
    }

    internal PowerStatusProvider(IPowerStatusSource source, IBatteryStateSource? fallback = null)
    {
        _source = source;
        _fallback = fallback;
    }

    public ProviderDescriptor Descriptor { get; } = new(
        GroupIds.Power,
        ProviderIds.Power,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(500),
        "user",
        "low");

    public ValueTask<ProviderResult> CollectAsync(
        ProviderContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = new PowerStatusRead(UnknownByte, UnknownByte, UnknownByte,
            UnknownByte, UnknownSeconds, UnknownSeconds);
        Exception? primaryFailure = null;
        try { status = _source.Read(); }
        catch (Exception exception) when (IsReadFailure(exception)) { primaryFailure = exception; }
        var batteryPresent = BatteryPresent(
            status.BatteryFlag);
        bool? charging = batteryPresent == true
            ? (status.BatteryFlag & BatteryCharging) != 0
            : null;
        var batterySaver = status.SystemStatusFlag switch
        {
            0 => false,
            1 => true,
            _ => (bool?)null,
        };
        var powerSource = PowerSource(status.AcLineStatus);
        var percent = BatteryPercent(status.BatteryLifePercent, batteryPresent);
        var seconds = BatterySeconds(status.BatteryLifeTime, batteryPresent);
        var secondsSource = SourceIds.Power;
        var needsFallback = primaryFailure is not null || powerSource == "unknown" ||
            batteryPresent is null || batteryPresent == true && percent is null;
        var errors = new List<ProviderError>();
        if (primaryFailure is not null) errors.Add(new(FailureCode(primaryFailure), null));
        var readout = new JsonObject
        {
            ["primary"] = SourceIds.Power,
            ["primaryStatus"] = primaryFailure is null
                ? needsFallback ? "unknown" : "available"
                : FailureCode(primaryFailure),
        };
        if (needsFallback && _fallback is not null)
        {
            var remaining = RetryRemaining(context);
            if (remaining > 0)
            {
                readout["secondary"] = SourceIds.PowerBatteryState;
                readout["secondaryStatus"] = _fallbackFailureCode;
                readout["retryAfterSeconds"] = remaining;
                errors.Add(new(_fallbackFailureCode!, null));
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                readout["secondary"] = SourceIds.PowerBatteryState;
                try
                {
                    var battery = _fallback.Read();
                    if (batteryPresent is { } present && present != battery.BatteryPresent)
                        throw new InvalidDataException("Battery presence changed between the two queries.");
                    if (powerSource == "unknown")
                        powerSource = battery.AcOnline ? "ac" : battery.BatteryPresent ? "battery" : "unknown";
                    batteryPresent ??= battery.BatteryPresent;
                    if (batteryPresent == true)
                    {
                        charging ??= battery.Charging;
                        percent = BatteryPercent(status.BatteryLifePercent, batteryPresent);
                        seconds ??= BatterySeconds(status.BatteryLifeTime, batteryPresent);
                        if (seconds is null && battery.Discharging && battery.EstimatedTime != UnknownSeconds)
                        {
                            seconds = battery.EstimatedTime;
                            secondsSource = SourceIds.PowerBatteryState;
                        }
                    }
                    _fallbackFailedAt = null;
                    _fallbackFailureCode = null;
                    readout["secondaryStatus"] = "available";
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    _fallbackFailedAt = context.Timestamp;
                    _fallbackFailureCode = FailureCode(exception);
                    errors.Add(new(_fallbackFailureCode, null));
                    readout["secondaryStatus"] = _fallbackFailureCode;
                    readout["retryAfterSeconds"] = 30;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var anyState = powerSource != "unknown" || batteryPresent is not null || batterySaver is not null;
        var complete = powerSource != "unknown" && batteryPresent is not null &&
            (batteryPresent == false || percent is not null) && primaryFailure is null;
        var data = new JsonObject
        {
            ["powerSource"] = powerSource,
            ["batteryPresent"] =
                NullableBoolean(batteryPresent),
            ["charging"] = NullableBoolean(charging),
            ["batterySaver"] =
                NullableBoolean(batterySaver),
            ["chargeStatus"] = ChargeStatus(
                status.BatteryFlag,
                batteryPresent) is "unknown" && charging is { } fallbackCharging
                    ? fallbackCharging ? "charging" : "not_charging"
                    : ChargeStatus(status.BatteryFlag, batteryPresent),
            ["readout"] = readout,
            ["metrics"] = new JsonObject
            {
                [MetricIds.BatteryChargePercent] =
                    MetricJson.Value(
                        percent,
                        Units.Percent,
                        SourceIds.Power),
                [MetricIds.BatteryLifeRemainingSeconds] =
                    MetricJson.Value(
                        seconds,
                        Units.Second,
                        secondsSource),
                [MetricIds.BatteryFullLifeSeconds] =
                    MetricJson.Value(
                        BatterySeconds(
                            status.BatteryFullLifeTime,
                            batteryPresent),
                        Units.Second,
                        SourceIds.Power),
            },
        };
        return ValueTask.FromResult(
            new ProviderResult(
                Descriptor.GroupId,
                Descriptor.ProviderId,
                context.UtcNow,
                complete ? AvailabilityStates.Available : anyState ? AvailabilityStates.Partial :
                    errors.Any(error => error.ErrorCode == StableErrorCodes.AccessDenied)
                        ? AvailabilityStates.PermissionDenied : AvailabilityStates.Unavailable,
                complete ? ProviderCoverage.Complete : ProviderCoverage.Limited,
                errors.Distinct().ToArray(),
                data));
    }

    private double RetryRemaining(ProviderContext context) => _fallbackFailedAt is { } failed
        ? Math.Max(0, 30 - context.TimeProvider.GetElapsedTime(failed, context.Timestamp).TotalSeconds)
        : 0;

    private static bool IsReadFailure(Exception exception) => exception is
        UnauthorizedAccessException or Win32Exception or NotSupportedException or IOException;

    private static string FailureCode(Exception exception) => exception is Win32Exception { NativeErrorCode: 5 }
        ? StableErrorCodes.AccessDenied : ExceptionClassifier.StableCode(exception);

    private static string PowerSource(byte acLineStatus) =>
        acLineStatus switch
        {
            AcOffline => "battery",
            AcOnline => "ac",
            _ => "unknown",
        };

    private static bool? BatteryPresent(byte batteryFlag)
    {
        if (batteryFlag == UnknownByte)
        {
            return null;
        }

        return (batteryFlag & NoSystemBattery) == 0;
    }

    private static string ChargeStatus(
        byte batteryFlag,
        bool? batteryPresent)
    {
        if (batteryPresent == false)
        {
            return "not_present";
        }
        if (batteryPresent is null)
        {
            return "unknown";
        }
        if (batteryFlag == UnknownByte) return "unknown";
        if ((batteryFlag & BatteryCharging) != 0)
        {
            return "charging";
        }
        if ((batteryFlag & 4) != 0)
        {
            return "critical";
        }
        if ((batteryFlag & 2) != 0)
        {
            return "low";
        }
        if ((batteryFlag & 1) != 0)
        {
            return "high";
        }

        return "normal";
    }

    private static double? BatteryPercent(
        byte percent,
        bool? batteryPresent) =>
        batteryPresent == true && percent <= 100
            ? percent
            : null;

    private static long? BatterySeconds(
        uint seconds,
        bool? batteryPresent) =>
        batteryPresent == true && seconds != UnknownSeconds
            ? seconds
            : null;

    private static JsonNode? NullableBoolean(bool? value) =>
        value is null ? null : JsonValue.Create(value.Value);
}
