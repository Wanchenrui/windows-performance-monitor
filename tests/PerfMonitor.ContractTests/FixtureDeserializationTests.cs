using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PerfMonitor.Contracts;

namespace PerfMonitor.ContractTests;

[TestClass]
public sealed class FixtureDeserializationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string FixturesDirectory = Path.Combine(
        RepositoryRoot,
        "contracts",
        "v1",
        "fixtures");

    [TestMethod]
    [DataRow("snapshot-normal.json", "snapshot")]
    [DataRow("snapshot-partial.json", "snapshot")]
    [DataRow("snapshot-stale.json", "snapshot")]
    [DataRow("snapshot-permission-denied.json", "snapshot")]
    [DataRow("scenario-process-churn.json", "scenario")]
    [DataRow("history-normal.json", "history")]
    [DataRow("capabilities-normal.json", "capabilities")]
    [DataRow("health-normal.json", "health")]
    [DataRow("diagnostics-normal.json", "diagnostics")]
    public void PythonGoldenFixtureDeserializes(string fixtureName, string kind)
    {
        var json = File.ReadAllText(Path.Combine(FixturesDirectory, fixtureName));

        object? contract = kind switch
        {
            "snapshot" => JsonSerializer.Deserialize<SnapshotContract>(
                json,
                ContractJson.Options),
            "scenario" => JsonSerializer.Deserialize<ContractScenario>(
                json,
                ContractJson.Options),
            "history" => JsonSerializer.Deserialize<HistoryContract>(
                json,
                ContractJson.Options),
            "capabilities" => JsonSerializer.Deserialize<CapabilitiesContract>(
                json,
                ContractJson.Options),
            "health" => JsonSerializer.Deserialize<HealthContract>(
                json,
                ContractJson.Options),
            "diagnostics" =>
                JsonSerializer.Deserialize<DiagnosticsContract>(
                    json,
                    ContractJson.Options),
            _ => throw new AssertFailedException($"Unknown fixture kind: {kind}"),
        };

        Assert.IsNotNull(contract);
    }

    [TestMethod]
    public void UnknownAdditiveFieldsDoNotBreakOlderClient()
    {
        var path = Path.Combine(FixturesDirectory, "snapshot-normal.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["futureEnvelopeField"] = 1;
        root["groups"]!["systemCpu"]!["futureGroupField"] = true;

        var snapshot = JsonSerializer.Deserialize<SnapshotContract>(
            root.ToJsonString(),
            ContractJson.Options);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("1.0", snapshot.ContractVersion);
        Assert.IsTrue(snapshot.Groups.ContainsKey("systemCpu"));
    }

    [TestMethod]
    public void ProcessPidReuseKeepsCreationTimeInIdentity()
    {
        var path = Path.Combine(
            FixturesDirectory,
            "scenario-process-churn.json");
        var scenario = JsonSerializer.Deserialize<ContractScenario>(
            File.ReadAllText(path),
            ContractJson.Options);
        Assert.IsNotNull(scenario);

        var before = scenario.Snapshots[0].Groups["processes"].Data;
        var after = scenario.Snapshots[1].Groups["processes"].Data;
        var beforeIdentity = before[0].GetProperty("identity");
        var afterIdentity = after[0].GetProperty("identity");

        Assert.AreEqual(
            beforeIdentity.GetProperty("pid").GetInt32(),
            afterIdentity.GetProperty("pid").GetInt32());
        Assert.AreNotEqual(
            beforeIdentity.GetProperty("creationTimeTicks").GetInt64(),
            afterIdentity.GetProperty("creationTimeTicks").GetInt64());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "contracts", "v1")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root containing contracts/v1.");
    }
}
