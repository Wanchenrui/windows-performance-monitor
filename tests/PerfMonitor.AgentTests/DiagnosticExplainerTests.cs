using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class DiagnosticExplainerTests
{
    [TestMethod]
    public void ExplainsExistingHighCpuEvidenceAsAdviceWithoutClaimingAProblemOrAction()
    {
        var explanation = DiagnosticExplainer.Explain(Event(DiagnosticRuleIds.HighCpu));
        StringAssert.Contains(explanation.Phenomenon, "CPU");
        StringAssert.Contains(explanation.Evidence, "92");
        StringAssert.Contains(explanation.Evidence, "15 秒");
        StringAssert.Contains(explanation.Suggestions, "进程");
        StringAssert.Contains(explanation.Limitation, "正常工作负载");
        StringAssert.Contains(explanation.Limitation, "不会自动执行");
        StringAssert.Contains(explanation.Limitation, "尚未核对");
    }

    [TestMethod]
    public void MissingAndNonFiniteEvidenceStayUnknownAndUnknownRulesDoNotInventACause()
    {
        var item = Event("future.rule");
        var explanation = DiagnosticExplainer.Explain(item with
        { Evidence = [item.Evidence[0] with { Value = double.NaN }, item.Evidence[0] with { Value = null }] });
        StringAssert.Contains(explanation.Evidence, "缺少可用");
        StringAssert.Contains(explanation.PossibleCauses, "未知");
        StringAssert.Contains(explanation.Phenomenon, "没有该规则");
        Assert.IsFalse(explanation.Evidence.Contains("NaN", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolvedHistoricalEventDoesNotOverridePausedOrNewAgentCurrentState()
    {
        var item = Event(DiagnosticRuleIds.ProviderUnavailable) with
        {
            SubjectId = "provider:" + GroupIds.Gpu, State = DiagnosticStates.Resolved,
            Evidence = [Event(DiagnosticRuleIds.ProviderUnavailable).Evidence[0] with
                { Signal = "provider.availability", Value = null, Unit = null }],
        };
        var descriptor = new ProviderDescriptor(GroupIds.Gpu, "test.gpu", TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), "user", "low");
        var assembler = new SnapshotAssembler([descriptor], instanceId: item.InstanceId);
        var now = DateTimeOffset.UtcNow;
        assembler.PublishAsync(ProviderResult.Paused(descriptor),
            new ProviderExecution(descriptor, now, now, now, 0, 0, 0, 0), CancellationToken.None);
        var paused = DiagnosticExplainer.Explain(item, assembler.Read());
        StringAssert.Contains(paused.Phenomenon, "此前记录");
        StringAssert.Contains(paused.Evidence, "恢复");
        StringAssert.Contains(paused.Limitation, "主动暂停");
        StringAssert.Contains(paused.Limitation, "缺测");
        var other = new SnapshotAssembler([descriptor], instanceId: "new-session");
        StringAssert.Contains(DiagnosticExplainer.Explain(item, other.Read()).Limitation, "其他 Agent 会话");
    }

    [TestMethod]
    public void KnownRulesHaveConcreteSuggestionsAndMemoryPointsToExistingExternalEntry()
    {
        foreach (var ruleId in DiagnosticRuleIds.All)
        {
            var explanation = DiagnosticExplainer.Explain(Event(ruleId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.Suggestions));
            Assert.IsFalse(explanation.Phenomenon.Contains("没有该规则", StringComparison.Ordinal));
        }
        StringAssert.Contains(DiagnosticExplainer.Explain(Event(DiagnosticRuleIds.MemoryPressure)).Suggestions,
            "系统任务管理器");
    }

    private static DiagnosticEventContract Event(string ruleId)
    {
        var now = DateTimeOffset.UtcNow;
        return new DiagnosticEventContract
        {
            ContractVersion = ContractVersions.V1, ProductVersion = ProductVersions.Agent,
            EventId = "existing-event", InstanceId = "session", RuleId = ruleId, RuleVersion = "1",
            State = DiagnosticStates.Active, Severity = DiagnosticSeverities.Warning, SubjectId = "system",
            FirstSeenUtc = now.AddSeconds(-15), LastSeenUtc = now, ObservationSequence = 10,
            Hysteresis = new DiagnosticHysteresisContract { ActivateWhen = "CPU >= 90%", RecoverWhen = "恢复：CPU <= 70%" },
            Debounce = new DiagnosticDebounceContract { ActivateSeconds = 15, RecoverSeconds = 5 },
            EvidenceWindow = new DiagnosticEvidenceWindowContract { FromUtc = now.AddSeconds(-15), ToUtc = now, SampleCount = 1 },
            Evidence = [new DiagnosticEvidenceContract { ObservedAtUtc = now, SubjectId = "system",
                Signal = MetricIds.SystemCpuUtilization, Value = 92, Unit = "percent", Condition = "CPU >= 90%" }],
        };
    }
}
