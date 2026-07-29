using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Agent;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class AgentOptionsTests
{
    [TestMethod]
    public void ParsesBoundedOnceMode()
    {
        var options = AgentOptions.Parse(
            [
                "--once",
                "--warmup-seconds",
                "2.5",
                "--max-concurrency",
                "2",
            ]);

        Assert.IsTrue(options.Once);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), options.Warmup);
        Assert.AreEqual(2, options.MaxConcurrency);
    }

    [TestMethod]
    public void RejectsConflictingLifetimeModes()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => AgentOptions.Parse(
                ["--once", "--duration-seconds", "1"]));
    }
}
