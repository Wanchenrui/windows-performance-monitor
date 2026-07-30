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

    public PowerStatusProvider()
        : this(new SystemPowerStatusSource())
    {
    }

    internal PowerStatusProvider(IPowerStatusSource source)
    {
        _source = source;
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
        var status = _source.Read();
        var batteryPresent = BatteryPresent(
            status.BatteryFlag);
        var charging = batteryPresent == true
            ? (status.BatteryFlag & BatteryCharging) != 0
            : null;
        var batterySaver = status.SystemStatusFlag switch
        {
            0 => false,
            1 => true,
            _ => (bool?)null,
        };
        var data = new JsonObject
        {
            ["powerSource"] = PowerSource(status.AcLineStatus),
            ["batteryPresent"] =
                NullableBoolean(batteryPresent),
            ["charging"] = NullableBoolean(charging),
            ["batterySaver"] =
                NullableBoolean(batterySaver),
            ["chargeStatus"] = ChargeStatus(
                status.BatteryFlag,
                batteryPresent),
            ["metrics"] = new JsonObject
            {
                [MetricIds.BatteryChargePercent] =
                    MetricJson.Value(
                        BatteryPercent(
                            status.BatteryLifePercent,
                            batteryPresent),
                        Units.Percent,
                        SourceIds.Power),
                [MetricIds.BatteryLifeRemainingSeconds] =
                    MetricJson.Value(
                        BatterySeconds(
                            status.BatteryLifeTime,
                            batteryPresent),
                        Units.Second,
                        SourceIds.Power),
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
                AvailabilityStates.Available,
                ProviderCoverage.Complete,
                [],
                data));
    }

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
