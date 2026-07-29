using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class SoakSimulationTests
{
    [TestMethod]
    [Timeout(15000)]
    public void SeventyTwoHoursOfDeadlinesRemainAbsoluteAndBounded()
    {
        const int seconds = 72 * 60 * 60;
        const long periodTicks = TimeSpan.TicksPerSecond;
        long deadline = 0;
        long totalMissed = 0;

        for (var second = 0; second < seconds; second++)
        {
            deadline = SchedulerMath.AdvanceAbsoluteDeadline(
                deadline,
                periodTicks,
                deadline,
                out var missed);
            totalMissed += missed;
        }

        Assert.AreEqual(
            seconds * periodTicks,
            deadline);
        Assert.AreEqual(0L, totalMissed);
    }
}
