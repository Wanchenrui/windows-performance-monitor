using PerfMonitor.Actions;
using PerfMonitor.Broker;
using PerfMonitor.Contracts;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class WindowsActionExecutorTests
{
    private const string CallerSid = "S-1-5-21-1000";
    private const int ClientPid = 4000;
    private const int BrokerPid = 5000;
    private const int TargetPid = 6000;
    private const long CreationTicks = 133000000000000000;

    [TestMethod]
    public async Task CreationTimeMismatchRejectsPidReuse()
    {
        var api = new FakeWindowsActionApi
        {
            Process = Process() with
            {
                CreationTimeTicks = CreationTicks + 1,
            },
        };
        var executor = Executor(api);

        var exception =
            await Assert.ThrowsExactlyAsync<ActionExecutorException>(
                async () =>
                    await executor.CaptureBeforeAsync(
                        TerminateAction(),
                        Caller(),
                        Policy(),
                        CancellationToken.None));
        Assert.AreEqual(
            ActionErrorCodes.TargetIdentityChanged,
            exception.ErrorCode);
        Assert.AreEqual(0, api.TerminateCount);
    }

    [TestMethod]
    public async Task OwnerMismatchRejectsOtherUsersProcess()
    {
        var api = new FakeWindowsActionApi
        {
            Process = Process() with
            {
                OwnerSid = "S-1-5-21-2000",
            },
        };
        var executor = Executor(api);

        var exception =
            await Assert.ThrowsExactlyAsync<ActionExecutorException>(
                async () =>
                    await executor.CaptureBeforeAsync(
                        TerminateAction(),
                        Caller(),
                        Policy(),
                        CancellationToken.None));
        Assert.AreEqual(
            ActionErrorCodes.TargetOwnerMismatch,
            exception.ErrorCode);
        Assert.AreEqual(0, api.TerminateCount);
    }

    [TestMethod]
    public async Task CriticalAndProductProcessesAreProtected()
    {
        foreach (var state in new[]
        {
            Process() with
            {
                IsCritical = true,
            },
            Process() with
            {
                ProcessName = "perf-monitor-agent",
            },
            Process() with
            {
                Pid = ClientPid,
            },
            Process() with
            {
                Pid = BrokerPid,
            },
        })
        {
            var api = new FakeWindowsActionApi
            {
                Process = state,
            };
            var executor = Executor(api);
            var action = TerminateAction() with
            {
                Pid = state.Pid,
            };

            var exception =
                await Assert.ThrowsExactlyAsync<
                    ActionExecutorException>(
                    async () =>
                        await executor.CaptureBeforeAsync(
                            action,
                            Caller(),
                            Policy(),
                            CancellationToken.None));
            Assert.AreEqual(
                ActionErrorCodes.TargetProtected,
                exception.ErrorCode);
            Assert.AreEqual(0, api.TerminateCount);
        }
    }

    [TestMethod]
    public async Task TerminationPassesExactGuardAndReturnsExited()
    {
        var api = new FakeWindowsActionApi
        {
            Process = Process(),
        };
        var executor = Executor(api);
        var action = TerminateAction();
        var before = await executor.CaptureBeforeAsync(
            action,
            Caller(),
            Policy(),
            CancellationToken.None);

        var after = await executor.ExecuteAsync(
            action,
            Caller(),
            Policy(),
            before,
            CancellationToken.None);

        Assert.AreEqual(1, api.TerminateCount);
        Assert.IsFalse(after.IsRunning);
        Assert.AreEqual(CreationTicks, after.CreationTimeTicks);
        Assert.AreEqual(CallerSid, api.LastGuard?.ExpectedOwnerSid);
        Assert.Contains(
            BrokerPid,
            api.LastGuard!.ProtectedPids);
        Assert.Contains(
            ClientPid,
            api.LastGuard.ProtectedPids);
    }

    [TestMethod]
    public async Task SetPriorityOnlyUsesBoundedEnum()
    {
        var api = new FakeWindowsActionApi
        {
            Process = Process(),
        };
        var executor = Executor(api);
        var action = new ActionRequestContract
        {
            ActionType = ActionTypes.SetProcessPriority,
            Pid = TargetPid,
            CreationTimeTicks = CreationTicks,
            Priority = ActionPriorities.BelowNormal,
        };
        var before = await executor.CaptureBeforeAsync(
            action,
            Caller(),
            Policy(),
            CancellationToken.None);

        var after = await executor.ExecuteAsync(
            action,
            Caller(),
            Policy(),
            before,
            CancellationToken.None);

        Assert.AreEqual(1, api.SetPriorityCount);
        Assert.AreEqual(
            ActionPriorities.BelowNormal,
            after.Priority);
    }

    [TestMethod]
    public async Task DiagnosticAndPowerUseCompiledIdsOnly()
    {
        var api = new FakeWindowsActionApi
        {
            Process = Process(),
        };
        var executor = Executor(api);
        var policy = Policy() with
        {
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
                ActionTypes.ApplyApprovedPowerProfile,
            ],
            AllowedDiagnosticIds =
            [
                ApprovedDiagnosticIds.BrokerSelfCheck,
            ],
            AllowedPowerProfileIds =
            [
                ApprovedPowerProfileIds.Balanced,
            ],
        };
        policy = policy.Validate();
        var diagnostic = new ActionRequestContract
        {
            ActionType = ActionTypes.StartApprovedDiagnostic,
            DiagnosticId =
                ApprovedDiagnosticIds.BrokerSelfCheck,
        };
        var diagnosticBefore =
            await executor.CaptureBeforeAsync(
                diagnostic,
                Caller(),
                policy,
                CancellationToken.None);
        var diagnosticAfter = await executor.ExecuteAsync(
            diagnostic,
            Caller(),
            policy,
            diagnosticBefore,
            CancellationToken.None);
        Assert.AreEqual(
            "self-check-run",
            diagnosticAfter.DiagnosticRunId);

        var power = new ActionRequestContract
        {
            ActionType =
                ActionTypes.ApplyApprovedPowerProfile,
            PowerProfileId =
                ApprovedPowerProfileIds.Balanced,
        };
        var powerBefore = await executor.CaptureBeforeAsync(
            power,
            Caller(),
            policy,
            CancellationToken.None);
        var powerAfter = await executor.ExecuteAsync(
            power,
            Caller(),
            policy,
            powerBefore,
            CancellationToken.None);
        Assert.AreEqual(
            ApprovedPowerProfileIds.Balanced,
            powerAfter.PowerProfileId);
        Assert.AreEqual(1, api.PowerApplyCount);
    }

    private static WindowsPrivilegedActionExecutor Executor(
        FakeWindowsActionApi api) =>
        new(api, BrokerPid);

    private static BrokerCallerIdentity Caller() =>
        new(
            CallerSid,
            ClientPid,
            Path.GetFullPath("perf-monitor-agent.exe"),
            new string('A', 64));

    private static BrokerMachinePolicy Policy() =>
        new BrokerMachinePolicy
        {
            PolicyVersion = "test-v1",
            DryRunOnly = false,
            RequireApprovedClientImage = false,
            RequireTrustedClientSignature = false,
            RequireProtectedClientPath = false,
            EnabledActionTypes =
            [
                ActionTypes.TerminateProcess,
                ActionTypes.SetProcessPriority,
            ],
            AllowedCallerSids = [CallerSid],
            AllowedPriorities =
            [
                ActionPriorities.BelowNormal,
            ],
        }.Validate();

    private static ActionRequestContract TerminateAction() =>
        new()
        {
            ActionType = ActionTypes.TerminateProcess,
            Pid = TargetPid,
            CreationTimeTicks = CreationTicks,
        };

    private static WindowsProcessActionState Process() =>
        new(
            TargetPid,
            CreationTicks,
            CallerSid,
            "test-target",
            IsCritical: false,
            IsRunning: true,
            ActionPriorities.Normal);

    private sealed class FakeWindowsActionApi :
        IWindowsActionApi
    {
        public required WindowsProcessActionState Process
        {
            get;
            init;
        }

        public int SetPriorityCount { get; private set; }
        public int TerminateCount { get; private set; }
        public int PowerApplyCount { get; private set; }
        public ProcessActionGuard? LastGuard { get; private set; }

        public WindowsProcessActionState InspectProcess(
            ProcessActionGuard guard)
        {
            LastGuard = guard;
            return Process;
        }

        public WindowsProcessActionState SetProcessPriority(
            ProcessActionGuard guard,
            string priority)
        {
            LastGuard = guard;
            SetPriorityCount++;
            return Process with
            {
                Priority = priority,
            };
        }

        public WindowsProcessActionState TerminateProcess(
            ProcessActionGuard guard)
        {
            LastGuard = guard;
            TerminateCount++;
            return Process with
            {
                IsRunning = false,
            };
        }

        public string StartBrokerSelfCheck() =>
            "self-check-run";

        public string ReadActivePowerProfileId() =>
            ApprovedPowerProfileIds.PowerSaver;

        public string ApplyPowerProfile(string powerProfileId)
        {
            PowerApplyCount++;
            return powerProfileId;
        }
    }
}
