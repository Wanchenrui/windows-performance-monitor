using System.Runtime.InteropServices;

namespace PerfMonitor.Collectors.Windows;

internal sealed record BatteryStateRead(bool AcOnline, bool BatteryPresent,
    bool Charging, bool Discharging, uint EstimatedTime);

internal interface IBatteryStateSource
{
    BatteryStateRead Read();
}

internal sealed class SystemBatteryStateSource : IBatteryStateSource
{
    public BatteryStateRead Read()
    {
        // SystemBatteryState (5), null input: query only, never changes a power policy.
        var status = NativeMethods.CallNtPowerInformation(5, 0, 0, out var battery,
            checked((uint)Marshal.SizeOf<NativeMethods.SystemBatteryState>()));
        if (status == 0xC0000022)
            throw new UnauthorizedAccessException("Windows denied the battery-state query.");
        if (status != 0)
            throw new NotSupportedException($"SystemBatteryState returned 0x{status:X8}.");
        if (battery.AcOnLine > 1 || battery.BatteryPresent > 1 ||
            battery.Charging > 1 || battery.Discharging > 1)
            throw new InvalidDataException("Windows returned invalid battery-state flags.");

        // MaxCapacity means capacity when new, not current full-charge capacity.
        // Do not manufacture a charge percentage or whole-computer wattage from it.
        return new(battery.AcOnLine != 0, battery.BatteryPresent != 0,
            battery.Charging != 0, battery.Discharging != 0, battery.EstimatedTime);
    }
}
