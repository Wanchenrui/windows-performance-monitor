using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PerfMonitor.Storage.Sqlite;

public sealed record SqliteIntegrityReport(
    string DatabasePath,
    int SchemaVersion,
    long SizeBytes,
    string Sha256,
    bool IntegrityPassed);

public sealed record SqliteBackupManifest(
    string FormatVersion,
    int SourceSchemaVersion,
    int TargetSchemaVersion,
    string SourceFileName,
    string BackupFileName,
    string Sha256,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);

public sealed record SqliteBackupResult(
    string BackupPath,
    string ManifestPath,
    SqliteBackupManifest Manifest);

public static class SqliteRecoveryManager
{
    private const string BackupFormatVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly Lazy<bool> ProviderInitialization = new(
        static () =>
        {
            SQLitePCL.Batteries_V2.Init();
            return true;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<SqliteBackupResult>
        CreateMigrationBackupAsync(
            string databasePath,
            int targetSchemaVersion,
            CancellationToken cancellationToken = default)
    {
        _ = ProviderInitialization.Value;
        var sourcePath = Path.GetFullPath(databasePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "sqlite_source_not_found",
                sourcePath);
        }

        var sourceReport = await VerifyAsync(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        if (!sourceReport.IntegrityPassed)
        {
            throw new InvalidDataException(
                "sqlite_source_integrity_failed");
        }

        if (targetSchemaVersion <= sourceReport.SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetSchemaVersion),
                "Target schema must be newer than source schema.");
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath) ??
            throw new InvalidOperationException(
                "sqlite_source_directory_missing");
        var backupDirectory = Path.Combine(
            sourceDirectory,
            "migration-backups");
        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddTHHmmssfffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        var token = Guid.NewGuid().ToString("N")[..8];
        var baseName = string.Concat(
            Path.GetFileName(sourcePath),
            ".schema-",
            sourceReport.SchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "-to-",
            targetSchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ".",
            stamp,
            ".",
            token);
        var backupPath = Path.Combine(
            backupDirectory,
            baseName + ".db");
        var manifestPath = backupPath + ".manifest.json";
        var temporaryPath = backupPath + ".tmp";
        var temporaryManifestPath = manifestPath + ".tmp";

        try
        {
            await BackupDatabaseAsync(
                sourcePath,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            var backupReport = await VerifyAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            if (!backupReport.IntegrityPassed ||
                backupReport.SchemaVersion !=
                    sourceReport.SchemaVersion)
            {
                throw new InvalidDataException(
                    "sqlite_backup_verification_failed");
            }

            File.Move(temporaryPath, backupPath);
            var manifest = new SqliteBackupManifest(
                BackupFormatVersion,
                sourceReport.SchemaVersion,
                targetSchemaVersion,
                Path.GetFileName(sourcePath),
                Path.GetFileName(backupPath),
                backupReport.Sha256,
                backupReport.SizeBytes,
                DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(
                manifest,
                JsonOptions);
            await File.WriteAllTextAsync(
                temporaryManifestPath,
                json,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryManifestPath, manifestPath);
            return new SqliteBackupResult(
                backupPath,
                manifestPath,
                manifest);
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryManifestPath);
        }
    }

    public static async Task<SqliteIntegrityReport> VerifyAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        _ = ProviderInitialization.Value;
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "sqlite_database_not_found",
                fullPath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        await using var connection = new SqliteConnection(
            builder.ToString());
        await connection.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA query_only=1;
            PRAGMA integrity_check(1);
            """;
        var integrity = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var file = new FileInfo(fullPath);
        return new SqliteIntegrityReport(
            fullPath,
            version,
            file.Length,
            await ComputeSha256Async(
                fullPath,
                cancellationToken).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase.Equals(
                integrity as string,
                "ok"));
    }

    public static async Task RestoreAsync(
        string databasePath,
        string manifestPath,
        bool confirmDiscardNewerData,
        CancellationToken cancellationToken = default)
    {
        if (!confirmDiscardNewerData)
        {
            throw new InvalidOperationException(
                "sqlite_restore_confirmation_required");
        }

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var json = await File.ReadAllTextAsync(
            fullManifestPath,
            cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<
            SqliteBackupManifest>(json, JsonOptions) ??
            throw new InvalidDataException(
                "sqlite_backup_manifest_invalid");
        ValidateManifest(manifest);
        var manifestDirectory =
            Path.GetDirectoryName(fullManifestPath) ??
            throw new InvalidDataException(
                "sqlite_backup_manifest_directory_missing");
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(fullDatabasePath),
                manifest.SourceFileName))
        {
            throw new InvalidDataException(
                "sqlite_backup_target_mismatch");
        }
        var backupPath = Path.Combine(
            manifestDirectory,
            manifest.BackupFileName);
        var backupReport = await VerifyAsync(
            backupPath,
            cancellationToken).ConfigureAwait(false);
        if (!backupReport.IntegrityPassed ||
            backupReport.SchemaVersion !=
                manifest.SourceSchemaVersion ||
            backupReport.SizeBytes != manifest.SizeBytes ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                backupReport.Sha256,
                manifest.Sha256))
        {
            throw new InvalidDataException(
                "sqlite_backup_manifest_mismatch");
        }

        var databaseDirectory =
            Path.GetDirectoryName(fullDatabasePath) ??
            throw new InvalidOperationException(
                "sqlite_restore_directory_missing");
        Directory.CreateDirectory(databaseDirectory);
        using var restoreLease = AcquireRestoreLease(
            databaseDirectory);
        foreach (var sidecar in new[]
        {
            fullDatabasePath + "-wal",
            fullDatabasePath + "-shm",
        })
        {
            if (File.Exists(sidecar))
            {
                throw new IOException(
                    "sqlite_restore_requires_clean_shutdown");
            }
        }

        var temporaryPath = Path.Combine(
            databaseDirectory,
            "." + Path.GetFileName(fullDatabasePath) +
            "." + Guid.NewGuid().ToString("N") + ".restore.tmp");
        var displacedPath = fullDatabasePath + ".pre-restore-" +
            DateTimeOffset.UtcNow.ToString(
                "yyyyMMddTHHmmssfffZ",
                System.Globalization.CultureInfo.InvariantCulture) +
            "." + Guid.NewGuid().ToString("N")[..8];
        var failedRestorePath = fullDatabasePath +
            ".failed-restore-" +
            DateTimeOffset.UtcNow.ToString(
                "yyyyMMddTHHmmssfffZ",
                System.Globalization.CultureInfo.InvariantCulture) +
            "." + Guid.NewGuid().ToString("N")[..8];
        var destinationExisted = File.Exists(fullDatabasePath);

        try
        {
            File.Copy(backupPath, temporaryPath);
            var copiedReport = await VerifyAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            if (!copiedReport.IntegrityPassed ||
                copiedReport.SchemaVersion !=
                    manifest.SourceSchemaVersion ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    copiedReport.Sha256,
                    manifest.Sha256))
            {
                throw new InvalidDataException(
                    "sqlite_restore_copy_invalid");
            }

            if (destinationExisted)
            {
                File.Replace(
                    temporaryPath,
                    fullDatabasePath,
                    displacedPath,
                    ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporaryPath, fullDatabasePath);
            }

            var restoredReport = await VerifyAsync(
                fullDatabasePath,
                cancellationToken).ConfigureAwait(false);
            if (!restoredReport.IntegrityPassed ||
                restoredReport.SchemaVersion !=
                    manifest.SourceSchemaVersion ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    restoredReport.Sha256,
                    manifest.Sha256))
            {
                // v1.0: preserve the failed image and put the displaced
                // database back atomically whenever one existed.
                if (destinationExisted &&
                    File.Exists(displacedPath))
                {
                    try
                    {
                        File.Replace(
                            displacedPath,
                            fullDatabasePath,
                            failedRestorePath,
                            ignoreMetadataErrors: false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or
                        UnauthorizedAccessException)
                    {
                        throw new InvalidDataException(
                            "sqlite_restore_verification_failed_" +
                            "rollback_failed",
                            exception);
                    }
                }
                else if (File.Exists(fullDatabasePath))
                {
                    File.Move(
                        fullDatabasePath,
                        failedRestorePath);
                }

                throw new InvalidDataException(
                    "sqlite_restore_verification_failed");
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        await using var source = new SqliteConnection(
            sourceBuilder.ToString());
        await using var destination = new SqliteConnection(
            destinationBuilder.ToString());
        await source.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        source.BackupDatabase(destination);
        await destination.CloseAsync().ConfigureAwait(false);
        await source.CloseAsync().ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous |
                FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static FileStream AcquireRestoreLease(
        string databaseDirectory)
    {
        try
        {
            return new FileStream(
                Path.Combine(databaseDirectory, ".agent.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "sqlite_restore_requires_agent_stopped",
                exception);
        }
    }

    private static void ValidateManifest(
        SqliteBackupManifest manifest)
    {
        if (manifest.FormatVersion != BackupFormatVersion ||
            manifest.SourceSchemaVersion < 0 ||
            manifest.TargetSchemaVersion <=
                manifest.SourceSchemaVersion ||
            Path.GetFileName(manifest.SourceFileName) !=
                manifest.SourceFileName ||
            Path.GetFileName(manifest.BackupFileName) !=
                manifest.BackupFileName ||
            manifest.Sha256.Length != 64 ||
            manifest.SizeBytes <= 0)
        {
            throw new InvalidDataException(
                "sqlite_backup_manifest_invalid");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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
