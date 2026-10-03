using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerfMonitor.Collectors.Windows;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly ulong ToUInt64() =>
            ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public static MemoryStatusEx Create() =>
            new()
            {
                Length = checked(
                    (uint)Marshal.SizeOf<MemoryStatusEx>()),
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFormattedCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public uint Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFormattedCounterItem
    {
        public nint Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(
        ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(
        out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(
        int informationLevel,
        nint inputBuffer,
        uint inputLength,
        out SystemBatteryState output,
        uint outputLength);

    [DllImport(
        "pdh.dll",
        EntryPoint = "PdhOpenQueryW",
        CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQuery(
        string? dataSource,
        nint userData,
        out SafePdhQueryHandle query);

    [DllImport(
        "pdh.dll",
        EntryPoint = "PdhAddEnglishCounterW",
        CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounter(
        SafePdhQueryHandle query,
        string fullCounterPath,
        nint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(
        SafePdhQueryHandle query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(
        nint counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(nint query);
}

internal sealed class SafePdhQueryHandle :
    SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        NativeMethods.PdhCloseQuery(handle) == 0;
}
