using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;
using PerfMonitor.Core;
using PerfMonitor.Diagnostics;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class CollectionGuidanceTests
{
    [TestMethod]
    public void MissingGroupAndLegacyPayloadDoNotInventConfigurationOrFallbackAttempts()
    {
        var missing = CollectionGuidance.Explain(GroupIds.Gpu, null);
        StringAssert.Contains(missing.Status, "尚无法判断原因");
        Assert.IsFalse(missing.Status.Contains("未配置", StringComparison.Ordinal));
        var legacy = CollectionGuidance.Explain(GroupIds.Gpu, Group(new JsonObject(), AvailabilityStates.Unavailable));
        StringAssert.Contains(legacy.Attempts, "不能确认");
        Assert.IsFalse(legacy.Attempts.Contains("Windows GPU Engine", StringComparison.Ordinal));
        Assert.IsFalse(legacy.Attempts.Contains("LibreHardwareMonitor", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PausedGroupOverridesFailureEvidenceAndSuggestsRestoreOnly()
    {
        var help = CollectionGuidance.Explain(GroupIds.Sensors,
            Group(new JsonObject(), AvailabilityStates.Error, [new(StableErrorCodes.AccessDenied, null)]) with { CollectionState = "paused" });
        StringAssert.Contains(help.Status, "暂停");
        StringAssert.Contains(help.Suggestions.Single(), "恢复原设置");
        Assert.IsFalse(help.Status.Contains("被拒", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NoBatteryIsNotATemperatureOrPowerFault()
    {
        var data = new JsonObject { ["batteryPresent"] = false, ["powerSource"] = "ac", ["readout"] = Readout(SourceIds.Power, "available") };
        var help = CollectionGuidance.Explain(GroupIds.Power, Group(data));
        StringAssert.Contains(help.Status, "不适用");
        Assert.AreEqual(0, help.Suggestions.Count);
        StringAssert.Contains(help.Limitation, "不提供整机功耗");
        StringAssert.Contains(help.Attempts, "GetSystemPowerStatus");
        Assert.IsFalse(help.Attempts.Contains("SystemBatteryState", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PowerFallbackAndCooldownAreExplainedOnlyWhenRecorded()
    {
        var readout = Readout(SourceIds.Power, "unknown");
        readout["secondary"] = SourceIds.PowerBatteryState; readout["secondaryStatus"] = StableErrorCodes.ProviderFailure; readout["retryAfterSeconds"] = 12.5;
        var help = CollectionGuidance.Explain(GroupIds.Power, Group(new JsonObject { ["readout"] = readout }, AvailabilityStates.Partial));
        StringAssert.Contains(help.Attempts, "SystemBatteryState");
        StringAssert.Contains(help.Attempts, "12.5");
        StringAssert.Contains(help.Attempts, "不保证恢复");
        Assert.IsTrue(help.Suggestions.Any(suggestion => suggestion.Contains("Windows 设置", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WorkerMissingDeniedAndBudgetWaitingDoNotClaimHardwareReadSucceeded()
    {
        var missing = CollectionGuidance.Explain(GroupIds.Sensors, Group(null, AvailabilityStates.NotSupported) with { CollectionState = "worker_missing" });
        StringAssert.Contains(missing.Attempts, "尚未执行硬件读取");
        Assert.IsFalse(missing.Attempts.Contains("LibreHardwareMonitor", StringComparison.Ordinal));
        var denied = CollectionGuidance.Explain(GroupIds.Sensors, Group(null, AvailabilityStates.PermissionDenied) with { CollectionState = "worker_access_denied" });
        StringAssert.Contains(denied.Status, "Worker 启动被拒");
        StringAssert.Contains(denied.Status, "不能据此确认硬件权限");
        var waiting = CollectionGuidance.Explain(GroupIds.Sensors, Group(null, AvailabilityStates.Unavailable) with { CollectionState = "worker_backoff" });
        StringAssert.Contains(waiting.Attempts, "本轮未重新启动");
        StringAssert.Contains(waiting.Limitation, "启动预算");
    }

    [TestMethod]
    public void GpuEngineLoadNeverClaimsPhysicalDeviceOrTemperatureCoverage()
    {
        var readout = Readout("hardware_worker", "worker_missing");
        readout["secondary"] = SourceIds.WindowsGpuEngine; readout["secondaryStatus"] = "available";
        var data = new JsonObject
        {
            ["readout"] = readout,
            ["metrics"] = new JsonObject { [MetricIds.GpuLoadMaxPercent] = MetricJson.Value(37d, Units.Percent, SourceIds.WindowsGpuEngine) },
        };
        var help = CollectionGuidance.Explain(GroupIds.Gpu, Group(data, AvailabilityStates.Partial) with { CollectionState = "worker_missing" });
        StringAssert.Contains(help.Status, "负载部分可用");
        StringAssert.Contains(help.Attempts, "Windows GPU Engine");
        StringAssert.Contains(help.Limitation, "不代表某块物理显卡");
        StringAssert.Contains(help.Limitation, "不提供温度");
    }

    [TestMethod]
    public void UnreportedTemperatureAndOldObservationsRemainUnknownWithoutDriverPromise()
    {
        var help = CollectionGuidance.Explain(GroupIds.Sensors, Group(new JsonObject(), AvailabilityStates.NotSupported) with
        { CollectionState = "hardware_unreported", Freshness = FreshnessStates.Stale });
        StringAssert.Contains(help.Status, "不能证明没有硬件");
        StringAssert.Contains(help.Status, "尚不可确认");
        StringAssert.Contains(help.Limitation, "未实现通用备用温度");
        StringAssert.Contains(help.Limitation, "更新驱动也不保证");
        Assert.IsTrue(help.Suggestions.Any(suggestion => suggestion.Contains("UEFI/BIOS", StringComparison.Ordinal)));
    }

    private static JsonObject Readout(string primary, string status) => new() { ["primary"] = primary, ["primaryStatus"] = status };
    private static SnapshotGroup Group(JsonObject? data, string availability = AvailabilityStates.Available, IReadOnlyList<ProviderError>? errors = null) =>
        new("test", DateTimeOffset.UtcNow, availability, FreshnessStates.Fresh, ProviderCoverage.Complete, errors ?? [], data);
}
