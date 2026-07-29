using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Collectors.Windows;
using PerfMonitor.Contracts;
using PerfMonitor.Core;

namespace PerfMonitor.AgentTests;

[TestClass]
public sealed class WindowsProviderSmokeTests
{
    [TestMethod]
    [Timeout(15000)]
    public async Task DefaultProvidersReturnContractGroupsWithoutElevation()
    {
        var providers = WindowsProviderFactory.CreateDefault();
        var time = TimeProvider.System;
        var groups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            var result = await provider.CollectAsync(
                new ProviderContext(
                    time,
                    time.GetUtcNow(),
                    time.GetTimestamp(),
                    Math.Max(1, Environment.ProcessorCount)),
                CancellationToken.None);
            groups.Add(result.GroupId);
            Assert.AreEqual(
                provider.Descriptor.ProviderId,
                result.ProviderId);
            Assert.IsNotNull(result.ObservedAtUtc);
        }

        CollectionAssert.IsSubsetOf(
            new[]
            {
                GroupIds.SystemCpu,
                GroupIds.Memory,
                GroupIds.Volumes,
                GroupIds.Uptime,
                GroupIds.Processes,
                GroupIds.Self,
            },
            groups.ToArray());
    }
}
