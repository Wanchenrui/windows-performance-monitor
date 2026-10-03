using System.Text.Json;
using System.Text.Json.Serialization;
using PerfMonitor.Core;

namespace PerfMonitor.Agent;

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize(AgentSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);
}
