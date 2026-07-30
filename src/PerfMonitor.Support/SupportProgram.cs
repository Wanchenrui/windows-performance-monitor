using System.Text.Json;
using PerfMonitor.Storage.Sqlite;

namespace PerfMonitor.Support;

public static class SupportProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            await Console.Error.WriteLineAsync(
                StableError(exception)).ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException(
                "support_command_required");
        }

        var category = args[0];
        var operation = args[1];
        var options = ParseOptions(args.Skip(2).ToArray());
        if (category == "database" && operation == "verify")
        {
            AssertAllowedOptions(options, "--path");
            var report = await SqliteRecoveryManager.VerifyAsync(
                RequiredPath(options, "--path"),
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(report).ConfigureAwait(false);
            return report.IntegrityPassed ? 0 : 3;
        }

        if (category == "database" && operation == "backup")
        {
            AssertAllowedOptions(
                options,
                "--path",
                "--target-schema");
            var targetSchema = ParsePositiveInt32(
                Required(options, "--target-schema"),
                "--target-schema");
            var result =
                await SqliteRecoveryManager
                    .CreateMigrationBackupAsync(
                        RequiredPath(options, "--path"),
                        targetSchema,
                        cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(result).ConfigureAwait(false);
            return 0;
        }

        if (category == "database" && operation == "restore")
        {
            AssertAllowedOptions(
                options,
                "--path",
                "--manifest",
                "--confirm-discard-newer-data");
            await SqliteRecoveryManager.RestoreAsync(
                RequiredPath(options, "--path"),
                RequiredPath(options, "--manifest"),
                options.ContainsKey(
                    "--confirm-discard-newer-data"),
                cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (category == "diagnostics" && operation == "export")
        {
            AssertAllowedOptions(
                options,
                "--data-directory",
                "--output",
                "--build-manifest",
                "--resource-evidence");
            await DiagnosticBundleExporter.ExportAsync(
                new DiagnosticExportOptions(
                    RequiredPath(options, "--data-directory"),
                    RequiredPath(options, "--output"),
                    OptionalPath(options, "--build-manifest"),
                    OptionalPath(options, "--resource-evidence")),
                cancellationToken).ConfigureAwait(false);
            return 0;
        }

        throw new ArgumentException(
            "support_command_unknown");
    }

    private static Dictionary<string, string?> ParseOptions(
        IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (!option.StartsWith(
                    "--",
                    StringComparison.Ordinal) ||
                options.ContainsKey(option))
            {
                throw new ArgumentException(
                    "support_option_invalid");
            }

            if (option == "--confirm-discard-newer-data")
            {
                options.Add(option, null);
                continue;
            }

            index++;
            if (index >= args.Count ||
                args[index].StartsWith(
                    "--",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "support_option_value_required");
            }

            options.Add(option, args[index]);
        }

        return options;
    }

    private static string Required(
        IReadOnlyDictionary<string, string?> options,
        string name)
    {
        if (!options.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"support_option_required:{name}");
        }

        return value;
    }

    private static void AssertAllowedOptions(
        IReadOnlyDictionary<string, string?> options,
        params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(
            StringComparer.Ordinal);
        foreach (var option in options.Keys)
        {
            if (!allowedSet.Contains(option))
            {
                throw new ArgumentException(
                    $"support_option_unknown:{option}");
            }
        }
    }

    private static string RequiredPath(
        IReadOnlyDictionary<string, string?> options,
        string name) =>
        Path.GetFullPath(Required(options, name));

    private static string? OptionalPath(
        IReadOnlyDictionary<string, string?> options,
        string name) =>
        options.TryGetValue(name, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : null;

    private static int ParsePositiveInt32(
        string value,
        string name)
    {
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result) ||
            result <= 0)
        {
            throw new ArgumentException(
                $"support_option_positive_integer_required:{name}");
        }

        return result;
    }

    private static async Task WriteJsonAsync<T>(T value)
    {
        await Console.Out.WriteLineAsync(
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy =
                        JsonNamingPolicy.CamelCase,
                })).ConfigureAwait(false);
    }

    private static string StableError(Exception exception) =>
        exception.Message.StartsWith(
            "sqlite_",
            StringComparison.Ordinal) ||
        exception.Message.StartsWith(
            "diagnostic_",
            StringComparison.Ordinal) ||
        exception.Message.StartsWith(
            "support_",
            StringComparison.Ordinal)
            ? exception.Message
            : exception.GetType().Name;
}
