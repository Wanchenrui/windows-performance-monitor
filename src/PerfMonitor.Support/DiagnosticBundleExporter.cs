using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.Support;

public sealed record DiagnosticExportOptions(
    string DataDirectory,
    string OutputPath,
    string? BuildManifestPath,
    string? ResourceEvidencePath);

public static partial class DiagnosticBundleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task ExportAsync(
        DiagnosticExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outputPath = Path.GetFullPath(options.OutputPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(outputPath),
                ".zip"))
        {
            throw new ArgumentException(
                "diagnostic_output_must_be_zip");
        }

        if (File.Exists(outputPath))
        {
            throw new IOException(
                "diagnostic_output_exists");
        }

        var dataDirectory = Path.GetFullPath(
            options.DataDirectory);
        var databasePath = Path.Combine(
            dataDirectory,
            "history-v1.db");
        var database = File.Exists(databasePath)
            ? await ReadDatabaseSummaryAsync(
                databasePath,
                cancellationToken).ConfigureAwait(false)
            : DatabaseSummary.Unavailable;
        var build = await ReadBuildSummaryAsync(
            options.BuildManifestPath,
            cancellationToken).ConfigureAwait(false);
        var resources = await ReadResourceSummaryAsync(
            options.ResourceEvidencePath,
            cancellationToken).ConfigureAwait(false);
        var bundle = new DiagnosticBundle(
            "1.0",
            DateTimeOffset.UtcNow,
            new ProductSummary(
                typeof(DiagnosticBundleExporter).Assembly
                    .GetName().Version?.ToString(3) ?? "unknown",
                "1.0",
                RuntimeInformation.FrameworkDescription),
            new PlatformSummary(
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.OSArchitecture.ToString(),
                Environment.Is64BitOperatingSystem),
            database,
            build,
            resources);
        var privacy = new PrivacyManifest(
            "1.0",
            "allowlist",
            [
                "user-name",
                "machine-name",
                "sid",
                "ip-or-mac",
                "process-name-pid-path-command-line",
                "raw-snapshot-or-database",
                "device-or-sensor-name",
                "broker-image-path-hash-or-audit-payload",
                "policy-environment-token-private-key",
            ]);
        var bundleJson = JsonSerializer.Serialize(
            bundle,
            JsonOptions);
        var privacyJson = JsonSerializer.Serialize(
            privacy,
            JsonOptions);
        RejectSensitiveText(bundleJson);
        RejectSensitiveText(privacyJson);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var temporaryPath = Path.Combine(
            outputDirectory ?? Directory.GetCurrentDirectory(),
            "." + Path.GetFileName(outputPath) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous |
                    FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(
                    stream,
                    ZipArchiveMode.Create,
                    leaveOpen: true,
                    Encoding.UTF8))
                {
                    await WriteEntryAsync(
                        archive,
                        "diagnostics.json",
                        bundleJson,
                        cancellationToken).ConfigureAwait(false);
                    await WriteEntryAsync(
                        archive,
                        "privacy-manifest.json",
                        privacyJson,
                        cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // v1.0: expose only a complete archive. The same-directory
            // rename is atomic, and overwrite remains forbidden.
            File.Move(temporaryPath, outputPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<DatabaseSummary>
        ReadDatabaseSummaryAsync(
            string databasePath,
            CancellationToken cancellationToken)
    {
        var report = await SqliteRecoveryManager.VerifyAsync(
            databasePath,
            cancellationToken).ConfigureAwait(false);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        await using var connection = new SqliteConnection(
            builder.ToString());
        await connection.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshots = await ReadLongAsync(
            connection,
            "SELECT COUNT(*) FROM snapshots_raw;",
            cancellationToken).ConfigureAwait(false);
        var diagnostics = await ReadLongAsync(
            connection,
            "SELECT COUNT(*) FROM diagnostic_events;",
            cancellationToken).ConfigureAwait(false);
        var providers = await ReadProviderSummariesAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        var diagnosticStates =
            await ReadDiagnosticSummariesAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
        return new DatabaseSummary(
            true,
            report.SchemaVersion,
            report.SizeBytes,
            report.IntegrityPassed,
            snapshots,
            diagnostics,
            providers,
            diagnosticStates);
    }

    private static async Task<IReadOnlyList<ProviderSummary>>
        ReadProviderSummariesAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_json
            FROM snapshots_raw
            ORDER BY sample_time_ms DESC
            LIMIT 1;
            """;
        var value = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        if (value is not string json)
        {
            return [];
        }

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
        if (!document.RootElement.TryGetProperty(
                "groups",
                out var groups) ||
            groups.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<ProviderSummary>();
        foreach (var group in groups.EnumerateObject())
        {
            var valueElement = group.Value;
            var providerId = ReadString(
                valueElement,
                "providerId");
            var availability = ReadString(
                valueElement,
                "availability");
            var freshness = ReadString(
                valueElement,
                "freshness");
            var errorCodes = new SortedSet<string>(
                StringComparer.Ordinal);
            if (valueElement.TryGetProperty(
                    "errors",
                    out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var code = ReadString(
                        error,
                        "errorCode");
                    if (code is not null)
                    {
                        errorCodes.Add(code);
                    }
                }
            }

            result.Add(new ProviderSummary(
                group.Name,
                providerId,
                availability,
                freshness,
                errorCodes.ToArray()));
        }

        return result
            .OrderBy(item => item.GroupId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<DiagnosticStateSummary>>
        ReadDiagnosticSummariesAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                rule_id,
                severity,
                state,
                COUNT(*),
                (MIN(first_seen_ms) / 86400000) * 86400000,
                (MAX(last_seen_ms) / 86400000) * 86400000
            FROM diagnostic_events
            GROUP BY rule_id, severity, state
            ORDER BY rule_id, severity, state
            LIMIT 256;
            """;
        var result = new List<DiagnosticStateSummary>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            result.Add(new DiagnosticStateSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return result;
    }

    private static async Task<BuildSummary?> ReadBuildSummaryAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.GetFullPath(path),
                cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var fileCount = root.TryGetProperty(
                "files",
                out var files) &&
            files.ValueKind == JsonValueKind.Array
                ? files.GetArrayLength()
                : 0;
        return new BuildSummary(
            ReadString(root, "productVersion"),
            ReadString(root, "gitCommit"),
            ReadString(root, "targetFramework"),
            ReadString(root, "runtimeIdentifier"),
            ReadString(root, "signatureMode"),
            fileCount);
    }

    private static async Task<ResourceSummary?>
        ReadResourceSummaryAsync(
            string? path,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.GetFullPath(path),
                cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty(
                "resources",
                out var resources))
        {
            throw new InvalidDataException(
                "diagnostic_resource_evidence_invalid");
        }

        return new ResourceSummary(
            ReadNestedDouble(
                resources,
                "workingSet",
                "peakMiB"),
            ReadNestedDouble(
                resources,
                "privateMemory",
                "peakMiB"),
            ReadNestedDouble(
                resources,
                "privateMemory",
                "retainedGrowthMiB"),
            ReadNestedDouble(
                resources,
                "gcHeap",
                "growthMiB"),
            ReadDouble(
                resources,
                "cpuCoreEquivalentMeanPct"),
            ReadDouble(resources, "handlePeak"),
            ReadDouble(resources, "threadPeak"),
            resources.TryGetProperty(
                    "passed",
                    out var passed) &&
                passed.ValueKind == JsonValueKind.True);
    }

    private static async Task<long> ReadLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadString(
        JsonElement parent,
        string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(
        JsonElement parent,
        string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static double? ReadNestedDouble(
        JsonElement parent,
        string objectName,
        string propertyName) =>
        parent.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? ReadDouble(nested, propertyName)
            : null;

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(
            name,
            CompressionLevel.SmallestSize);
        entry.LastWriteTime = new DateTimeOffset(
            2000,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: false);
        await writer.WriteAsync(
            content.AsMemory(),
            cancellationToken).ConfigureAwait(false);
    }

    private static void RejectSensitiveText(string value)
    {
        foreach (var pattern in SensitivePatterns())
        {
            if (pattern.Expression.IsMatch(value))
            {
                throw new InvalidDataException(
                    "diagnostic_sensitive_pattern_detected_" +
                    pattern.Name);
            }
        }
    }

    private static IReadOnlyList<SensitivePattern>
        SensitivePatterns() =>
    [
        new("windows_path", WindowsPathRegex()),
        new("sid", SidRegex()),
        new("ipv4", Ipv4Regex()),
        new("mac", MacRegex()),
        new("email", EmailRegex()),
        new("secret", SecretRegex()),
    ];

    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9])[A-Za-z]:\\",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        @"(?i)\bS-1-(?:\d+-){1,14}\d+\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex SidRegex();

    [GeneratedRegex(
        @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(
        @"(?i)\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    [GeneratedRegex(
        @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        @"(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*[^"",\s}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();

    private sealed record DiagnosticBundle(
        string SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        ProductSummary Product,
        PlatformSummary Platform,
        DatabaseSummary Database,
        BuildSummary? Build,
        ResourceSummary? Resources);

    private sealed record ProductSummary(
        string ProductVersion,
        string ContractVersion,
        string Framework);

    private sealed record PlatformSummary(
        string OsVersion,
        string Architecture,
        bool Is64BitOperatingSystem);

    private sealed record DatabaseSummary(
        bool Available,
        int? SchemaVersion,
        long? SizeBytes,
        bool? IntegrityPassed,
        long SnapshotCount,
        long DiagnosticCount,
        IReadOnlyList<ProviderSummary> Providers,
        IReadOnlyList<DiagnosticStateSummary> DiagnosticStates)
    {
        public static DatabaseSummary Unavailable { get; } = new(
            false,
            null,
            null,
            null,
            0,
            0,
            [],
            []);
    }

    private sealed record ProviderSummary(
        string GroupId,
        string? ProviderId,
        string? Availability,
        string? Freshness,
        IReadOnlyList<string> ErrorCodes);

    private sealed record DiagnosticStateSummary(
        string RuleId,
        string Severity,
        string State,
        long Count,
        long FirstSeenDayEpochMs,
        long LastSeenDayEpochMs);

    private sealed record BuildSummary(
        string? ProductVersion,
        string? GitCommit,
        string? TargetFramework,
        string? RuntimeIdentifier,
        string? SignatureMode,
        int FileCount);

    private sealed record ResourceSummary(
        double? WorkingSetPeakMiB,
        double? PrivateMemoryPeakMiB,
        double? RetainedPrivateGrowthMiB,
        double? GcHeapGrowthMiB,
        double? CpuCoreEquivalentMeanPct,
        double? HandlePeak,
        double? ThreadPeak,
        bool Passed);

    private sealed record PrivacyManifest(
        string SchemaVersion,
        string ExportMode,
        IReadOnlyList<string> ExcludedCategories);

    private sealed record SensitivePattern(
        string Name,
        Regex Expression);
}
