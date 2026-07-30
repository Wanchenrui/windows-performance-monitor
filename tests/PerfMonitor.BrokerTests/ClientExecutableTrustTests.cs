using PerfMonitor.Broker;

namespace PerfMonitor.BrokerTests;

[TestClass]
public sealed class ClientExecutableTrustTests
{
    [TestMethod]
    public void ProtectedPathIsExactAndNotPrefixBased()
    {
        var programFiles = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "program-files-path-root"));
        var expected = Path.Combine(
            programFiles,
            "PerfMonitor",
            "agent",
            "perf-monitor-agent.exe");

        Assert.IsTrue(
            WindowsBrokerClientExecutableTrustVerifier
                .IsExpectedAgentPath(
                    expected.ToUpperInvariant(),
                    programFiles));
        Assert.IsFalse(
            WindowsBrokerClientExecutableTrustVerifier
                .IsExpectedAgentPath(
                    expected + ".attacker",
                    programFiles));
        Assert.IsFalse(
            WindowsBrokerClientExecutableTrustVerifier
                .IsExpectedAgentPath(
                    Path.Combine(
                        programFiles,
                        "PerfMonitor",
                        "broker",
                        "perf-monitor-agent.exe"),
                    programFiles));
    }

    [TestMethod]
    [Timeout(30000)]
    public void AuthenticodeTrustBindsVerifiedSignerAndContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Authenticode trust is Windows-only.");
        }

        using var directory = new TemporaryDirectory();
        var verifier =
            new WindowsBrokerClientExecutableTrustVerifier(
                directory.Path);
        var trustedSource = FindTrustedWindowsExecutable(
            verifier);
        var sourceTrust = verifier.Verify(trustedSource);

        Assert.IsTrue(sourceTrust.SignatureTrusted);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(
                sourceTrust.SignerSubject));
        Assert.IsTrue(
            IsUpperSha256(
                sourceTrust.SignerCertificateSha256));
        Assert.IsTrue(IsUpperSha256(sourceTrust.Sha256));
        Assert.IsFalse(sourceTrust.IsProtectedInstallPath);

        var copiedPath = directory.File(
            "signed-system-binary.exe");
        File.Copy(trustedSource, copiedPath);
        var copiedTrust = verifier.Verify(copiedPath);
        Assert.IsTrue(copiedTrust.SignatureTrusted);
        Assert.AreEqual(
            sourceTrust.SignerCertificateSha256,
            copiedTrust.SignerCertificateSha256);

        TamperSignedContent(copiedPath);
        var tamperedTrust = verifier.Verify(copiedPath);
        Assert.IsFalse(tamperedTrust.SignatureTrusted);
        Assert.AreNotEqual(
            copiedTrust.Sha256,
            tamperedTrust.Sha256);
    }

    private static string FindTrustedWindowsExecutable(
        WindowsBrokerClientExecutableTrustVerifier verifier)
    {
        var candidates = new[]
        {
            FindOnPath("git.exe"),
            FindOnPath("dotnet.exe"),
            FindOnPath("python.exe"),
            Environment.ProcessPath,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "Git",
                "cmd",
                "git.exe"),
            Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            Path.Combine(
                Environment.SystemDirectory,
                "where.exe"),
            Path.Combine(
                Environment.SystemDirectory,
                "whoami.exe"),
        }.Where(static candidate =>
            !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var trust = verifier.Verify(candidate);
            if (trust.SignatureTrusted)
            {
                return candidate;
            }

            failures.Add(
                $"{candidate}=0x{trust.AuthenticodeStatus:X8}");
        }

        throw new AssertFailedException(
            "No trusted signed Windows executable was available: " +
            string.Join(", ", failures));
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(
                    directory,
                    fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
            }
        }

        return null;
    }

    private static void TamperSignedContent(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var offset = Math.Min(
            1_024L,
            Math.Max(1L, stream.Length / 3));
        stream.Position = offset;
        var original = stream.ReadByte();
        if (original < 0)
        {
            throw new AssertFailedException(
                "Signed test executable was unexpectedly empty.");
        }

        stream.Position = offset;
        stream.WriteByte(checked((byte)(original ^ 0x5A)));
        stream.Flush(flushToDisk: true);
    }

    private static bool IsUpperSha256(string? value) =>
        value?.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or
                >= 'A' and <= 'F');

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"perf-monitor-client-trust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) =>
            System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
