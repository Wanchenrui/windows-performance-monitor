using PerfMonitor.Actions;
using PerfMonitor.Contracts;

namespace PerfMonitor.ActionsTests;

[TestClass]
public sealed class ActionCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private const string CallerSid = "S-1-5-21-1000";
    private const string ImageHash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void DefaultPoliciesDenyEveryAction()
    {
        Assert.IsTrue(AgentActionPolicy.Default.DryRunOnly);
        Assert.IsEmpty(
            AgentActionPolicy.Default.EnabledActionTypes);
        Assert.IsTrue(BrokerMachinePolicy.Default.DryRunOnly);
        Assert.IsEmpty(
            BrokerMachinePolicy.Default.EnabledActionTypes);

        var decision = ActionPolicyEvaluator.Evaluate(
            AgentActionPolicy.Default,
            TerminateAction());
        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(
            ActionErrorCodes.ActionPolicyDenied,
            decision.ErrorCode);
    }

    [TestMethod]
    public async Task DryRunCapturesStateWithoutMutation()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var coordinator = Coordinator(audit, executor);

        var result = await coordinator.ExecuteAsync(
            Request("dry-run-1", dryRun: true),
            Caller(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.DryRun, result.Status);
        Assert.AreEqual(1, executor.CaptureCount);
        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.AreEqual(result.Before, result.After);
        Assert.AreEqual(1, audit.CompletedCount);
    }

    [TestMethod]
    public async Task ForcedDryRunCannotBeOverriddenByRequest()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var coordinator = Coordinator(
            audit,
            executor,
            forceDryRunOnly: true);

        var result = await coordinator.ExecuteAsync(
            Request("forced-dry-run", dryRun: false),
            Caller(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.DryRun, result.Status);
        Assert.AreEqual(0, executor.ExecuteCount);
    }

    [TestMethod]
    [DataRow(ActionTypes.SetProcessPriority)]
    [DataRow(ActionTypes.TerminateProcess)]
    [DataRow(ActionTypes.StartApprovedDiagnostic)]
    [DataRow(ActionTypes.ApplyApprovedPowerProfile)]
    public async Task ForcedDryRunNeverMutatesWhitelistedAction(
        string actionType)
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var policy = new BrokerMachinePolicy
        {
            PolicyVersion = "all-dry-run-v1",
            DryRunOnly = false,
            RequireApprovedClientImage = false,
            EnabledActionTypes = ActionTypes.All,
            AllowedCallerSids = [CallerSid],
            AllowedPriorities = ActionPriorities.All,
            AllowedDiagnosticIds = ApprovedDiagnosticIds.All,
            AllowedPowerProfileIds =
                ApprovedPowerProfileIds.All,
        }.Validate();
        var coordinator = new BrokerActionCoordinator(
            policy,
            audit,
            executor,
            forceDryRunOnly: true,
            new FixedTimeProvider(Now));
        var request = new ActionExecutionRequestContract
        {
            IdempotencyKey = $"all-dry-run-{actionType}",
            DeadlineUtc = Now.AddSeconds(10),
            DryRun = false,
            AgentPolicyVersion = "test-agent-v1",
            Action = ActionFor(actionType),
        };

        var result = await coordinator.ExecuteAsync(
            request,
            Caller(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.DryRun, result.Status);
        Assert.AreEqual(1, executor.CaptureCount);
        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.AreEqual(result.Before, result.After);
    }

    [TestMethod]
    public async Task SameCallerAndKeyExecutesAtMostOnce()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var coordinator = Coordinator(audit, executor);
        var firstRequest = Request("same-key", dryRun: false);

        var first = await coordinator.ExecuteAsync(
            firstRequest,
            Caller(),
            Now,
            CancellationToken.None);
        var retry = await coordinator.ExecuteAsync(
            firstRequest with
            {
                DeadlineUtc = Now.AddSeconds(10),
            },
            Caller(),
            Now.AddSeconds(1),
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.Succeeded, first.Status);
        Assert.AreEqual(first, retry);
        Assert.AreEqual(1, executor.ExecuteCount);
        Assert.AreEqual(1, executor.CaptureCount);
    }

    [TestMethod]
    public async Task ReusingKeyForDifferentActionConflicts()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var coordinator = Coordinator(audit, executor);
        var firstRequest = Request("conflict-key", dryRun: false);
        await coordinator.ExecuteAsync(
            firstRequest,
            Caller(),
            Now,
            CancellationToken.None);

        var conflict = await coordinator.ExecuteAsync(
            firstRequest with
            {
                DeadlineUtc = Now.AddSeconds(10),
                Action = firstRequest.Action with
                {
                    Pid = 9002,
                },
            },
            Caller(),
            Now.AddSeconds(1),
            CancellationToken.None);

        Assert.AreEqual(
            ActionStatuses.IdempotencyConflict,
            conflict.Status);
        Assert.AreEqual(
            ActionErrorCodes.IdempotencyConflict,
            conflict.ErrorCode);
        Assert.AreEqual(1, executor.ExecuteCount);
    }

    [TestMethod]
    public async Task RecoveredPendingIsNeverReplayed()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var request = Request("crash-key", dryRun: false);
        await audit.ReserveAsync(
            new ActionAuditRequest(
                CallerSid,
                5000,
                ImageHash,
                ActionRequestHash.Compute(request),
                "test-machine-v1",
                Now,
                request),
            CancellationToken.None);
        await audit.RecoverPendingAsync(
            Now.AddMinutes(1),
            CancellationToken.None);

        var result = await Coordinator(
            audit,
            executor).ExecuteAsync(
                request with
                {
                    DeadlineUtc = Now.AddMinutes(1).AddSeconds(10),
                },
                Caller(),
                Now.AddMinutes(1),
                CancellationToken.None);

        Assert.AreEqual(
            ActionStatuses.Indeterminate,
            result.Status);
        Assert.AreEqual(
            ActionErrorCodes.IdempotencyIndeterminate,
            result.ErrorCode);
        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.AreEqual(0, executor.CaptureCount);
    }

    [TestMethod]
    public async Task PossibleMutationFailureIsIndeterminate()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor
        {
            ExecuteError = new ActionExecutorException(
                ActionErrorCodes.ExecutorFailed,
                mayHaveMutated: true),
        };

        var result = await Coordinator(
            audit,
            executor).ExecuteAsync(
                Request("unknown-result", dryRun: false),
                Caller(),
                Now,
                CancellationToken.None);

        Assert.AreEqual(
            ActionStatuses.Indeterminate,
            result.Status);
        Assert.AreEqual(
            ActionErrorCodes.IdempotencyIndeterminate,
            result.ErrorCode);
        Assert.AreEqual(1, executor.ExecuteCount);
    }

    [TestMethod]
    public async Task CallerOrImageDenialNeverCallsExecutor()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var coordinator = Coordinator(audit, executor);

        var wrongCaller = Caller() with
        {
            Sid = "S-1-5-21-2000",
        };
        var result = await coordinator.ExecuteAsync(
            Request("wrong-caller", dryRun: false),
            wrongCaller,
            Now,
            CancellationToken.None);

        Assert.AreEqual(ActionStatuses.Denied, result.Status);
        Assert.AreEqual(
            ActionErrorCodes.CallerIdentityDenied,
            result.ErrorCode);
        Assert.AreEqual(0, executor.CaptureCount);
        Assert.AreEqual(0, executor.ExecuteCount);
    }

    [TestMethod]
    public async Task UnsupportedPriorityIsRejectedBeforeAudit()
    {
        await using var audit = new FakeAuditStore();
        var executor = new FakeExecutor();
        var request = Request("bad-priority", dryRun: false) with
        {
            Action = new ActionRequestContract
            {
                ActionType = ActionTypes.SetProcessPriority,
                Pid = 9001,
                CreationTimeTicks = 123456789,
                Priority = "real_time",
            },
        };

        var result = await Coordinator(
            audit,
            executor).ExecuteAsync(
                request,
                Caller(),
                Now,
                CancellationToken.None);

        Assert.AreEqual(ActionStatuses.Rejected, result.Status);
        Assert.AreEqual(
            ActionErrorCodes.InvalidRequest,
            result.ErrorCode);
        Assert.AreEqual(0, audit.ReservationCount);
        Assert.AreEqual(0, executor.ExecuteCount);
    }

    [TestMethod]
    public void RequestHashExcludesDeadlineButIncludesDryRun()
    {
        var first = Request("hash-key", dryRun: true);
        var later = first with
        {
            DeadlineUtc = first.DeadlineUtc.AddSeconds(1),
        };
        var executable = first with
        {
            DryRun = false,
        };

        Assert.AreEqual(
            ActionRequestHash.Compute(first),
            ActionRequestHash.Compute(later));
        Assert.AreNotEqual(
            ActionRequestHash.Compute(first),
            ActionRequestHash.Compute(executable));
    }

    private static BrokerActionCoordinator Coordinator(
        FakeAuditStore audit,
        FakeExecutor executor,
        bool forceDryRunOnly = false) =>
        new(
            EnabledPolicy(),
            audit,
            executor,
            forceDryRunOnly,
            new FixedTimeProvider(Now));

    private static BrokerMachinePolicy EnabledPolicy() =>
        new BrokerMachinePolicy
        {
            PolicyVersion = "test-machine-v1",
            DryRunOnly = false,
            RequireApprovedClientImage = true,
            EnabledActionTypes =
            [
                ActionTypes.TerminateProcess,
            ],
            AllowedCallerSids = [CallerSid],
            ApprovedClientImages =
            [
                new ApprovedClientImagePolicy
                {
                    Path = Path.GetFullPath(
                        Path.Combine(
                            "test-root",
                            "perf-monitor-agent.exe")),
                    Sha256 = ImageHash,
                },
            ],
        }.Validate();

    private static BrokerCallerIdentity Caller() =>
        new(
            CallerSid,
            5000,
            Path.GetFullPath(
                Path.Combine(
                    "test-root",
                    "perf-monitor-agent.exe")),
            ImageHash);

    private static ActionExecutionRequestContract Request(
        string key,
        bool dryRun) =>
        new()
        {
            IdempotencyKey = key,
            DeadlineUtc = Now.AddSeconds(10),
            DryRun = dryRun,
            AgentPolicyVersion = "test-agent-v1",
            Action = TerminateAction(),
        };

    private static ActionRequestContract TerminateAction() =>
        new()
        {
            ActionType = ActionTypes.TerminateProcess,
            Pid = 9001,
            CreationTimeTicks = 123456789,
        };

    private static ActionRequestContract ActionFor(
        string actionType) =>
        actionType switch
        {
            ActionTypes.SetProcessPriority => new()
            {
                ActionType = actionType,
                Pid = 9001,
                CreationTimeTicks = 123456789,
                Priority = ActionPriorities.BelowNormal,
            },
            ActionTypes.TerminateProcess => TerminateAction(),
            ActionTypes.StartApprovedDiagnostic => new()
            {
                ActionType = actionType,
                DiagnosticId =
                    ApprovedDiagnosticIds.BrokerSelfCheck,
            },
            ActionTypes.ApplyApprovedPowerProfile => new()
            {
                ActionType = actionType,
                PowerProfileId =
                    ApprovedPowerProfileIds.Balanced,
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(actionType)),
        };

    private sealed class FakeExecutor : IPrivilegedActionExecutor
    {
        public int CaptureCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public ActionExecutorException? ExecuteError { get; init; }

        public ValueTask<ActionStateContract> CaptureBeforeAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return ValueTask.FromResult(new ActionStateContract
            {
                Pid = action.Pid,
                CreationTimeTicks = action.CreationTimeTicks,
                IsRunning = true,
                Priority = ActionPriorities.Normal,
            });
        }

        public ValueTask<ActionStateContract> ExecuteAsync(
            ActionRequestContract action,
            BrokerCallerIdentity caller,
            BrokerMachinePolicy policy,
            ActionStateContract before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            if (ExecuteError is not null)
            {
                throw ExecuteError;
            }

            return ValueTask.FromResult(before with
            {
                IsRunning = false,
            });
        }
    }

    private sealed class FakeAuditStore : IActionAuditStore
    {
        private readonly Dictionary<string, Entry> _entries =
            new(StringComparer.Ordinal);

        public int ReservationCount { get; private set; }
        public int CompletedCount { get; private set; }

        public ValueTask<ActionAuditReservation> ReserveAsync(
            ActionAuditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReservationCount++;
            var key = Key(
                request.CallerSid,
                request.Request.IdempotencyKey);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(
                    request.RequestSha256,
                    Guid.NewGuid().ToString("N"),
                    Result: null,
                    Indeterminate: false);
                _entries.Add(key, entry);
                return ValueTask.FromResult(
                    new ActionAuditReservation(
                        ActionAuditReservationKind.New,
                        entry.ActionId,
                        null));
            }

            if (!StringComparer.Ordinal.Equals(
                entry.RequestHash,
                request.RequestSha256))
            {
                return ValueTask.FromResult(
                    new ActionAuditReservation(
                        ActionAuditReservationKind.Conflict,
                        entry.ActionId,
                        null));
            }

            if (entry.Indeterminate || entry.Result is null)
            {
                return ValueTask.FromResult(
                    new ActionAuditReservation(
                        ActionAuditReservationKind.Indeterminate,
                        entry.ActionId,
                        entry.Result));
            }

            return ValueTask.FromResult(
                new ActionAuditReservation(
                    ActionAuditReservationKind.Replay,
                    entry.ActionId,
                    entry.Result));
        }

        public ValueTask CompleteAsync(
            string callerSid,
            string idempotencyKey,
            ActionResultContract result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(callerSid, idempotencyKey);
            var entry = _entries[key];
            _entries[key] = entry with
            {
                Result = result,
                Indeterminate =
                    result.Status == ActionStatuses.Indeterminate,
            };
            CompletedCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkIndeterminateAsync(
            string callerSid,
            string idempotencyKey,
            DateTimeOffset completedAtUtc,
            string errorCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(callerSid, idempotencyKey);
            var entry = _entries[key];
            _entries[key] = entry with
            {
                Result = IndeterminateResult(
                    entry.ActionId,
                    idempotencyKey,
                    completedAtUtc,
                    errorCode),
                Indeterminate = true,
            };
            return ValueTask.CompletedTask;
        }

        public ValueTask RecoverPendingAsync(
            DateTimeOffset recoveredAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pair in _entries.ToArray())
            {
                if (pair.Value.Result is not null)
                {
                    continue;
                }

                _entries[pair.Key] = pair.Value with
                {
                    Result = IndeterminateResult(
                        pair.Value.ActionId,
                        pair.Key[(pair.Key.IndexOf('|') + 1)..],
                        recoveredAtUtc,
                        ActionErrorCodes.IdempotencyIndeterminate),
                    Indeterminate = true,
                };
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _entries.Clear();
            return ValueTask.CompletedTask;
        }

        private static string Key(string sid, string key) =>
            $"{sid}|{key}";

        private static ActionResultContract IndeterminateResult(
            string actionId,
            string idempotencyKey,
            DateTimeOffset completedAtUtc,
            string errorCode) =>
            new()
            {
                ActionId = actionId,
                IdempotencyKey = idempotencyKey,
                Status = ActionStatuses.Indeterminate,
                ErrorCode = errorCode,
                ReceivedAtUtc = completedAtUtc,
                CompletedAtUtc = completedAtUtc,
            };

        private sealed record Entry(
            string RequestHash,
            string ActionId,
            ActionResultContract? Result,
            bool Indeterminate);
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
