using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfMonitor.Core;

/// <summary>Preserves the existing group JSON shape while writing its frozen payload.</summary>
public sealed class SnapshotGroupJsonConverter : JsonConverter<SnapshotGroup>
{
    public override SnapshotGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<WireGroup>(ref reader, options)
            ?? throw new JsonException("Snapshot group cannot be null.");
        return SnapshotGroup.FromJson(wire.ProviderId, wire.ObservedAtUtc, wire.Availability,
            wire.Freshness, wire.Coverage, wire.Errors, wire.Data) with
        {
            ObservationSequence = wire.ObservationSequence,
            ObservedElapsedSeconds = wire.ObservedElapsedSeconds,
            CollectionState = wire.CollectionState,
        };
    }

    public override void Write(Utf8JsonWriter writer, SnapshotGroup value, JsonSerializerOptions options)
    {
        value.EnsureSerializable();
        JsonSerializer.Serialize(writer, new WireGroup(value.ProviderId, value.ObservedAtUtc, value.Availability,
            value.Freshness, value.Coverage, value.Errors, value.SerializedData)
        {
            ObservationSequence = value.ObservationSequence,
            ObservedElapsedSeconds = value.ObservedElapsedSeconds,
            CollectionState = value.CollectionState,
        }, options);
    }

    private sealed record WireGroup(
        string ProviderId,
        DateTimeOffset? ObservedAtUtc,
        string Availability,
        string Freshness,
        ProviderCoverage Coverage,
        IReadOnlyList<ProviderError> Errors,
        JsonElement? Data)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ObservationSequence { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ObservedElapsedSeconds { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CollectionState { get; init; }
    }
}
