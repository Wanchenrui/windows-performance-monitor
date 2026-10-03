using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PerfMonitor.Actions;
using PerfMonitor.Contracts;

namespace PerfMonitor.Broker;

public sealed record ProcessActionGuard(
    int Pid,
    long ExpectedCreationTimeTicks,
    string ExpectedOwnerSid,
    IReadOnlyList<string> DeniedProcessNames,
    IReadOnlyList<int> ProtectedPids);

public sealed record WindowsProcessActionState(
    int Pid,
    long CreationTimeTicks,
    string OwnerSid,
    string ProcessName,
    bool IsCritical,
    bool IsRunning,
    string Priority);

public interface IWindowsActionApi
{
    WindowsProcessActionState InspectProcess(
        ProcessActionGuard guard);

    WindowsProcessActionState SetProcessPriority(
        ProcessActionGuard guard,
        string priority);

    WindowsProcessActionState TerminateProcess(
        ProcessActionGuard guard);

    string StartBrokerSelfCheck();

    string ReadActivePowerProfileId();

    string ApplyPowerProfile(string powerProfileId);
}

public sealed class WindowsPrivilegedActionExecutor :
    IPrivilegedActionExecutor
{
    private readonly IWindowsActionApi _api;
    private readonly int _brokerPid;

    public WindowsPrivilegedActionExecutor(
        IWindowsActionApi? api = null,
        int? brokerPid = null)
    {
        _api = api ?? new NativeWindowsActionApi();
        _brokerPid = brokerPid ?? Environment.ProcessId;
    }

    public ValueTask<ActionStateContract> CaptureBeforeAsync(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionContractValidation.ValidateAction(action);
        var state = action.ActionType switch
        {
            ActionTypes.SetProcessPriority or
            ActionTypes.TerminateProcess =>
                CaptureProcess(action, caller, policy),
            ActionTypes.StartApprovedDiagnostic =>
                new ActionStateContract
                {
                    DiagnosticId = action.DiagnosticId,
                },
            ActionTypes.ApplyApprovedPowerProfile =>
                new ActionStateContract
                {
                    PowerProfileId =
                        _api.ReadActivePowerProfileId(),
                },
            _ => throw new ActionExecutorException(
                ActionErrorCodes.ActionNotSupported),
        };
        return ValueTask.FromResult(state);
    }

    public ValueTask<ActionStateContract> ExecuteAsync(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy,
        ActionStateContract before,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActionStateContract after;
        switch (action.ActionType)
        {
            case ActionTypes.SetProcessPriority:
            {
                var state = _api.SetProcessPriority(
                    Guard(action, caller, policy),
                    action.Priority!);
                ValidateProcessState(
                    state,
                    Guard(action, caller, policy),
                    allowExited: false);
                after = ToContract(state);
                break;
            }

            case ActionTypes.TerminateProcess:
            {
                var state = _api.TerminateProcess(
                    Guard(action, caller, policy));
                ValidateProcessState(
                    state,
                    Guard(action, caller, policy),
                    allowExited: true);
                if (state.IsRunning)
                {
                    throw new ActionExecutorException(
                        ActionErrorCodes.ExecutorFailed,
                        mayHaveMutated: true);
                }

                after = ToContract(state);
                break;
            }

            case ActionTypes.StartApprovedDiagnostic:
                if (!StringComparer.Ordinal.Equals(
                    action.DiagnosticId,
                    ApprovedDiagnosticIds.BrokerSelfCheck))
                {
                    throw new ActionExecutorException(
                        ActionErrorCodes.ActionNotSupported);
                }

                after = new ActionStateContract
                {
                    DiagnosticId = action.DiagnosticId,
                    DiagnosticRunId =
                        _api.StartBrokerSelfCheck(),
                };
                break;

            case ActionTypes.ApplyApprovedPowerProfile:
            {
                var profileId = _api.ApplyPowerProfile(
                    action.PowerProfileId!);
                if (!StringComparer.Ordinal.Equals(
                    profileId,
                    action.PowerProfileId))
                {
                    throw new ActionExecutorException(
                        ActionErrorCodes.ExecutorFailed,
                        mayHaveMutated: true);
                }

                after = new ActionStateContract
                {
                    PowerProfileId = profileId,
                };
                break;
            }

            default:
                throw new ActionExecutorException(
                    ActionErrorCodes.ActionNotSupported);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(after);
    }

    private ActionStateContract CaptureProcess(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy)
    {
        var guard = Guard(action, caller, policy);
        var state = _api.InspectProcess(guard);
        ValidateProcessState(
            state,
            guard,
            allowExited: false);
        return ToContract(state);
    }

    private ProcessActionGuard Guard(
        ActionRequestContract action,
        BrokerCallerIdentity caller,
        BrokerMachinePolicy policy) =>
        new(
            action.Pid!.Value,
            action.CreationTimeTicks!.Value,
            caller.Sid,
            policy.DeniedProcessNames,
            [0, 4, _brokerPid, caller.ClientPid]);

    internal static void ValidateProcessState(
        WindowsProcessActionState state,
        ProcessActionGuard guard,
        bool allowExited)
    {
        if (state.Pid != guard.Pid ||
            state.CreationTimeTicks !=
                guard.ExpectedCreationTimeTicks)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.TargetIdentityChanged);
        }

        if (!StringComparer.Ordinal.Equals(
            state.OwnerSid,
            guard.ExpectedOwnerSid))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.TargetOwnerMismatch);
        }

        if (guard.ProtectedPids.Contains(state.Pid) ||
            state.IsCritical ||
            guard.DeniedProcessNames.Contains(
                state.ProcessName,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.TargetProtected);
        }

        if (!allowExited && !state.IsRunning)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.TargetNotFound);
        }

        if (!ActionPriorities.All.Contains(
            state.Priority,
            StringComparer.Ordinal))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed);
        }
    }

    private static ActionStateContract ToContract(
        WindowsProcessActionState state) =>
        new()
        {
            Pid = state.Pid,
            CreationTimeTicks = state.CreationTimeTicks,
            IsRunning = state.IsRunning,
            Priority = state.Priority,
        };
}

internal sealed class NativeWindowsActionApi : IWindowsActionApi
{
    private static readonly IReadOnlyDictionary<string, Guid>
        PowerProfiles = new Dictionary<string, Guid>(
            StringComparer.Ordinal)
        {
            [ApprovedPowerProfileIds.Balanced] =
                new("381b4222-f694-41f0-9685-ff5bb260df2e"),
            [ApprovedPowerProfileIds.PowerSaver] =
                new("a1841308-3541-4fab-bc81-f71556f20b4a"),
            [ApprovedPowerProfileIds.HighPerformance] =
                new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
        };

    public WindowsProcessActionState InspectProcess(
        ProcessActionGuard guard)
    {
        using var process = OpenProcess(
            guard.Pid,
            ProcessAccess.QueryLimitedInformation);
        var state = ReadProcessState(process, guard.Pid);
        WindowsPrivilegedActionExecutor.ValidateProcessState(
            state,
            guard,
            allowExited: false);
        return state;
    }

    public WindowsProcessActionState SetProcessPriority(
        ProcessActionGuard guard,
        string priority)
    {
        using var process = OpenProcess(
            guard.Pid,
            ProcessAccess.QueryLimitedInformation |
            ProcessAccess.SetInformation);
        var before = ReadProcessState(process, guard.Pid);
        WindowsPrivilegedActionExecutor.ValidateProcessState(
            before,
            guard,
            allowExited: false);
        if (!NativeMethods.SetPriorityClass(
            process,
            PriorityToNative(priority)))
        {
            throw Win32Failure(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: false);
        }

        var after = ReadProcessState(process, guard.Pid);
        if (!StringComparer.Ordinal.Equals(
            after.Priority,
            priority))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: true);
        }

        return after;
    }

    public WindowsProcessActionState TerminateProcess(
        ProcessActionGuard guard)
    {
        using var process = OpenProcess(
            guard.Pid,
            ProcessAccess.QueryLimitedInformation |
            ProcessAccess.Terminate |
            ProcessAccess.Synchronize);
        var before = ReadProcessState(process, guard.Pid);
        WindowsPrivilegedActionExecutor.ValidateProcessState(
            before,
            guard,
            allowExited: false);
        if (!NativeMethods.TerminateProcess(process, 1))
        {
            throw Win32Failure(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: false);
        }

        var wait = NativeMethods.WaitForSingleObject(
            process,
            milliseconds: 5_000);
        if (wait != NativeMethods.WaitObject0)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: true);
        }

        return before with
        {
            IsRunning = false,
        };
    }

    public string StartBrokerSelfCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ActionNotSupported);
        }

        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        if (identity.User is null)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed);
        }

        return Guid.NewGuid().ToString("N");
    }

    public string ReadActivePowerProfileId()
    {
        var result = NativeMethods.PowerGetActiveScheme(
            nint.Zero,
            out var pointer);
        if (result != 0 || pointer == nint.Zero)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed);
        }

        try
        {
            var guid = Marshal.PtrToStructure<Guid>(pointer);
            return PowerProfiles.FirstOrDefault(
                pair => pair.Value == guid).Key ?? "custom";
        }
        finally
        {
            _ = NativeMethods.LocalFree(pointer);
        }
    }

    public string ApplyPowerProfile(string powerProfileId)
    {
        if (!PowerProfiles.TryGetValue(
            powerProfileId,
            out var profile))
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ActionNotSupported);
        }

        var result = NativeMethods.PowerSetActiveScheme(
            nint.Zero,
            ref profile);
        if (result != 0)
        {
            throw new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: false);
        }

        return ReadActivePowerProfileId();
    }

    private static SafeProcessHandle OpenProcess(
        int pid,
        ProcessAccess access)
    {
        var handle = NativeMethods.OpenProcess(
            access,
            inheritHandle: false,
            pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            var error = Marshal.GetLastWin32Error();
            throw new ActionExecutorException(
                error == NativeMethods.ErrorInvalidParameter
                    ? ActionErrorCodes.TargetNotFound
                    : ActionErrorCodes.TargetProtected);
        }

        return handle;
    }

    private static WindowsProcessActionState ReadProcessState(
        SafeProcessHandle process,
        int pid)
    {
        if (!NativeMethods.GetProcessTimes(
            process,
            out var creation,
            out _,
            out _,
            out _))
        {
            throw Win32Failure(
                ActionErrorCodes.TargetNotFound,
                mayHaveMutated: false);
        }

        if (!NativeMethods.OpenProcessToken(
            process,
            NativeMethods.TokenQuery,
            out var token))
        {
            throw Win32Failure(
                ActionErrorCodes.TargetProtected,
                mayHaveMutated: false);
        }

        string ownerSid;
        using (token)
        using (var identity = new WindowsIdentity(
            token.DangerousGetHandle()))
        {
            ownerSid = identity.User?.Value ??
                throw new ActionExecutorException(
                    ActionErrorCodes.TargetProtected);
        }

        var priority = NativeMethods.GetPriorityClass(process);
        if (priority == 0)
        {
            throw Win32Failure(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: false);
        }

        if (!NativeMethods.IsProcessCritical(
            process,
            out var critical))
        {
            throw Win32Failure(
                ActionErrorCodes.TargetProtected,
                mayHaveMutated: false);
        }

        var length = 32_768;
        var path = new char[length];
        if (!NativeMethods.QueryFullProcessImageName(
            process,
            flags: 0,
            path,
            ref length))
        {
            throw Win32Failure(
                ActionErrorCodes.TargetProtected,
                mayHaveMutated: false);
        }

        return new WindowsProcessActionState(
            pid,
            checked((long)creation.ToUInt64()),
            ownerSid,
            Path.GetFileNameWithoutExtension(
                new string(path, 0, length)),
            critical,
            IsRunning(process),
            NativePriority(priority));
    }

    private static bool IsRunning(SafeProcessHandle process)
    {
        var wait = NativeMethods.WaitForSingleObject(
            process,
            milliseconds: 0);
        return wait switch
        {
            NativeMethods.WaitTimeout => true,
            NativeMethods.WaitObject0 => false,
            _ => throw Win32Failure(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: false),
        };
    }

    private static uint PriorityToNative(string priority) =>
        priority switch
        {
            ActionPriorities.Idle =>
                NativeMethods.IdlePriorityClass,
            ActionPriorities.BelowNormal =>
                NativeMethods.BelowNormalPriorityClass,
            ActionPriorities.Normal =>
                NativeMethods.NormalPriorityClass,
            ActionPriorities.AboveNormal =>
                NativeMethods.AboveNormalPriorityClass,
            _ => throw new ActionExecutorException(
                ActionErrorCodes.ActionNotSupported),
        };

    private static string NativePriority(uint priority) =>
        priority switch
        {
            NativeMethods.IdlePriorityClass =>
                ActionPriorities.Idle,
            NativeMethods.BelowNormalPriorityClass =>
                ActionPriorities.BelowNormal,
            NativeMethods.NormalPriorityClass =>
                ActionPriorities.Normal,
            NativeMethods.AboveNormalPriorityClass =>
                ActionPriorities.AboveNormal,
            _ => throw new ActionExecutorException(
                ActionErrorCodes.TargetProtected),
        };

    private static ActionExecutorException Win32Failure(
        string defaultError,
        bool mayHaveMutated)
    {
        var error = Marshal.GetLastWin32Error();
        return new ActionExecutorException(
            error == NativeMethods.ErrorAccessDenied
                ? ActionErrorCodes.TargetProtected
                : defaultError,
            mayHaveMutated);
    }
}

[Flags]
internal enum ProcessAccess : uint
{
    Terminate = 0x0001,
    SetInformation = 0x0200,
    QueryLimitedInformation = 0x1000,
    Synchronize = 0x00100000,
}

internal static class NativeMethods
{
    internal const uint TokenQuery = 0x0008;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidParameter = 87;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint IdlePriorityClass = 0x00000040;
    internal const uint BelowNormalPriorityClass = 0x00004000;
    internal const uint NormalPriorityClass = 0x00000020;
    internal const uint AboveNormalPriorityClass = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly ulong ToUInt64() =>
            ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        ProcessAccess desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport(
        "advapi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    internal static extern uint GetPriorityClass(
        SafeProcessHandle process);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(
        SafeProcessHandle process,
        uint priorityClass);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(
        SafeProcessHandle process,
        uint exitCode);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    internal static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref int size);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessCritical(
        SafeProcessHandle process,
        [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(
        nint userRootPowerKey,
        out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(
        nint userRootPowerKey,
        ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    internal static extern nint LocalFree(nint memory);
}
