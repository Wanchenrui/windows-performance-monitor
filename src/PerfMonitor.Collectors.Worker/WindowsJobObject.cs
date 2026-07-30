using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PerfMonitor.Collectors.Worker;

internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsJobObject? TryCreateAndAssign(
        Process process,
        long maxProcessMemoryBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(
            IntPtr.Zero,
            null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var information =
            new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation =
                {
                    LimitFlags =
                        JobObjectLimitProcessMemory |
                        JobObjectLimitKillOnJobClose,
                },
                ProcessMemoryLimit = new UIntPtr(
                    checked((ulong)maxProcessMemoryBytes)),
            };
        var configured = SetInformationJobObject(
            handle,
            JobObjectInfoType.ExtendedLimitInformation,
            ref information,
            (uint)Marshal.SizeOf<
                JobObjectExtendedLimitInformation>());
        var assigned = configured &&
            AssignProcessToJobObject(
                handle,
                process.Handle);
        if (!assigned)
        {
            handle.Dispose();
            return null;
        }

        return new WindowsJobObject(handle);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation
            BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() =>
            CloseHandle(handle);
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInfoType informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle job,
        IntPtr process);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
