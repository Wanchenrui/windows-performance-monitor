using System.Text.Json;
using Microsoft.Data.Sqlite;
using PerfMonitor.Contracts;

namespace PerfMonitor.Storage.Sqlite;

internal static class SqliteDiagnosticQuery
{
    public static async Task<DiagnosticsContract> ExecuteAsync(
        SqliteConnection connection,
        DiagnosticQueryContract query,
        string responseInstanceId,
        CancellationToken cancellationToken)
    {
        var validated = DiagnosticQueryValidation.Validate(query);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue(
            "$from_ms",
            validated.FromEpochMs);
        command.Parameters.AddWithValue(
            "$to_ms",
            validated.ToEpochMs);
        command.Parameters.AddWithValue(
            "$limit",
            checked(validated.MaxEvents + 1));

        var clauses = new List<string>
        {
            "last_seen_ms BETWEEN $from_ms AND $to_ms",
        };
        AddFilter(
            command,
            clauses,
            "rule_id",
            "rule",
            validated.RuleIds);
        AddFilter(
            command,
            clauses,
            "state",
            "state",
            validated.States);
        command.CommandText = $"""
            SELECT payload_json
            FROM diagnostic_events
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY last_seen_ms DESC, diagnostic_id DESC
            LIMIT $limit;
            """;

        var events = new List<DiagnosticEventContract>(
            checked(validated.MaxEvents + 1));
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            events.Add(
                JsonSerializer.Deserialize<DiagnosticEventContract>(
                    reader.GetString(0),
                    ContractJson.Options) ??
                throw new InvalidDataException(
                    "sqlite_diagnostic_payload_invalid"));
        }

        var truncated = events.Count > validated.MaxEvents;
        if (truncated)
        {
            events.RemoveAt(events.Count - 1);
        }

        events.Reverse();
        return new DiagnosticsContract
        {
            ContractVersion = ContractVersions.V1,
            ProductVersion = ProductVersions.Agent,
            InstanceId = responseInstanceId,
            Query = query,
            EventCount = events.Count,
            Truncated = truncated,
            Events = events,
        };
    }

    private static void AddFilter(
        SqliteCommand command,
        ICollection<string> clauses,
        string column,
        string parameterPrefix,
        IReadOnlySet<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var parameterNames = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            var name = $"${parameterPrefix}_{index}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, value);
            index++;
        }

        clauses.Add(
            $"{column} IN ({string.Join(", ", parameterNames)})");
    }
}
