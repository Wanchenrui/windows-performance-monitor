using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Collectors.Windows;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class FormulaParityTests
{
    [TestMethod]
    public void SystemCpuMatchesWindowsBusyTimeDefinition()
    {
        var previous = new SystemCpuProvider.CpuCounters(
            Idle: 100,
            Kernel: 500,
            User: 500);
        var current = new SystemCpuProvider.CpuCounters(
            Idle: 150,
            Kernel: 650,
            User: 650);

        var actual = SystemCpuProvider.CalculateUtilizationPercent(
            previous,
            current);

        Assert.IsNotNull(actual);
        Assert.AreEqual(83.333333, actual.Value, 0.000001);
    }

    [TestMethod]
    public void MemoryMatchesTotalMinusAvailableDefinition()
    {
        var actual = MemoryProvider.CalculateUtilizationPercent(
            totalBytes: 1000,
            availableBytes: 250);

        Assert.AreEqual(75.0, actual, 0.000001);
    }

    [TestMethod]
    public void ProcessCpuMatchesPythonReferenceFormula()
    {
        var (normalized, coreEquivalent) =
            ProcessProvider.CalculateCpuPercentages(
                TimeSpan.TicksPerSecond,
                elapsedSeconds: 2,
                logicalProcessorCount: 4);

        Assert.IsNotNull(normalized);
        Assert.IsNotNull(coreEquivalent);
        Assert.AreEqual(12.5, normalized.Value, 0.000001);
        Assert.AreEqual(50.0, coreEquivalent.Value, 0.000001);
    }

    [TestMethod]
    public void PidReuseChangesProcessIdentity()
    {
        var before = new ProcessProvider.ProcessIdentity(1234, 10);
        var after = new ProcessProvider.ProcessIdentity(1234, 11);

        Assert.AreNotEqual(before, after);
    }
}
