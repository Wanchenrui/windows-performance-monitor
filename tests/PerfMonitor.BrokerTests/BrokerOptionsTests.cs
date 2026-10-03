using PerfMonitor.Actions;
using PerfMonitor.Broker;
using PerfMonitor.Contracts;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class BrokerOptionsTests
{
    [TestMethod]
    public void ModeIsRequiredAndExclusive()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => BrokerOptions.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(
            () => BrokerOptions.Parse(
                ["--console", "--service"]));
    }

    [TestMethod]
    public void ConsoleModeAllowsBoundedTestOverrides()
    {
        var data = Path.Combine(
            Path.GetTempPath(),
            "perf-monitor-broker-options");
        var policy = Path.Combine(data, "test-policy.json");
        var options = BrokerOptions.Parse(
        [
            "--console",
            "--data-directory",
            data,
            "--policy",
            policy,
            "--pipe-name",
            "PerfMonitor.Broker.Test.Options",
            "--duration-seconds",
            "10",
        ]);

        Assert.AreEqual(BrokerRunMode.Console, options.Mode);
        Assert.AreEqual(
            Path.GetFullPath(data),
            options.DataDirectory);
        Assert.AreEqual(
            Path.GetFullPath(policy),
            options.PolicyPath);
        Assert.AreEqual(
            TimeSpan.FromSeconds(10),
            options.Duration);
    }

    [TestMethod]
    public void ServiceModeUsesOnlyFixedMachineLocations()
    {
        var options = BrokerOptions.Parse(["--service"]);
        var programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);

        Assert.AreEqual(BrokerRunMode.Service, options.Mode);
        Assert.IsTrue(
            options.DataDirectory.StartsWith(
                Path.GetFullPath(programData),
                StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(
            BrokerPipeEndpoint.DefaultPipeName,
            options.PipeName);
        Assert.IsNull(options.Duration);
        Assert.ThrowsExactly<ArgumentException>(
            () => BrokerOptions.Parse(
                [
                    "--service",
                    "--pipe-name",
                    "untrusted",
                ]));
    }

    [TestMethod]
    public void ConsoleDurationAndPipeNameAreBounded()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrokerOptions.Parse(
                [
                    "--console",
                    "--duration-seconds",
                    "0",
                ]));
        Assert.ThrowsExactly<ArgumentException>(
            () => BrokerOptions.Parse(
                [
                    "--console",
                    "--pipe-name",
                    @"bad\pipe",
                ]));
    }

    [TestMethod]
    public void ServicePolicyRequiresSignedProtectedAgent()
    {
        var programFiles = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "program-files-policy-root"));
        var agentPath = Path.Combine(
            programFiles,
            "PerfMonitor",
            "agent",
            "perf-monitor-agent.exe");
        var policy = new BrokerMachinePolicy
        {
            PolicyVersion = "service-policy-v1",
            DryRunOnly = false,
            EnabledActionTypes =
            [
                ActionTypes.StartApprovedDiagnostic,
            ],
            AllowedCallerSids =
            [
                "S-1-5-21-1000",
            ],
            ApprovedClientImages =
            [
                new ApprovedClientImagePolicy
                {
                    Path = agentPath,
                    Sha256 = new string('A', 64),
                    SignerSubject =
                        "CN=PerfMonitor Test Publisher",
                    SignerCertificateSha256 =
                        new string('B', 64),
                },
            ],
            AllowedDiagnosticIds =
            [
                ApprovedDiagnosticIds.BrokerSelfCheck,
            ],
        }.Validate();

        var validated = BrokerServicePolicy.Validate(
            policy,
            programFiles);
        Assert.AreEqual(
            policy.PolicyVersion,
            validated.PolicyVersion);
        Assert.AreEqual(
            agentPath,
            validated.ApprovedClientImages.Single().Path,
            ignoreCase: true);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BrokerServicePolicy.Validate(
                policy with
                {
                    RequireTrustedClientSignature = false,
                },
                programFiles));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            BrokerServicePolicy.Validate(
                policy with
                {
                    ApprovedClientImages =
                    [
                        policy.ApprovedClientImages.Single()
                            with
                            {
                                Path = Path.Combine(
                                    programFiles,
                                    "unprotected",
                                    "perf-monitor-agent.exe"),
                            },
                    ],
                },
                programFiles));
    }
}
