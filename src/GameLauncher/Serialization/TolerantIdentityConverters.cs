using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Serialization;

/// <summary>Options for the INNER (de)serialization of identity types. Deliberately has no converter for the
/// tolerant wrappers below, so a wrapper can hand an element to the ordinary deserializer without recursing.</summary>
internal static class IdentityJson
{
    internal static readonly JsonSerializerOptions Inner = new();

    /// <summary>What a tolerant reader treats as "could not understand this": wrong JSON type, a missing or blank
    /// required value, an unparseable date, a non-object subtree. Never a reason to fail the whole document.</summary>
    internal static bool IsUnderstandingFailure(Exception ex) =>
        ex is JsonException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException;
}

/// <summary>Reads GameOverride.Identity. A subtree it cannot understand is QUARANTINED (kept as the raw element and
/// written back verbatim), never thrown - one bad identity value must not discard favorites, watched folders and
/// covers (5.2). JSON null is "no identity". Written back verbatim when quarantined.</summary>
public sealed class TolerantIdentityRecordConverter : JsonConverter<GameIdentityRecord>
{
    public override GameIdentityRecord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new JsonException("Identity must be a JSON object.");

            var record = element.Deserialize<GameIdentityRecord>(IdentityJson.Inner);
            if (record is null || !record.IsWellFormed())
                throw new JsonException("Identity record is not well formed.");

            return record;
        }
        catch (Exception ex) when (IdentityJson.IsUnderstandingFailure(ex))
        {
            Logger.Warn($"An identity record could not be understood - quarantined verbatim ({ex.Message}).");
            return new GameIdentityRecord { Quarantined = element.Clone() };
        }
    }

    public override void Write(Utf8JsonWriter writer, GameIdentityRecord value, JsonSerializerOptions options)
    {
        if (value.Quarantined is { } raw)
        {
            raw.WriteTo(writer);
            return;
        }

        JsonSerializer.Serialize(writer, value, IdentityJson.Inner);
    }
}

/// <summary>A list whose elements are each read tolerantly: an element that cannot be understood is kept as a raw
/// element (IdentityConflict.Raw) and written back verbatim, instead of failing the whole document.</summary>
public sealed class TolerantIdentityConflictListConverter : JsonConverter<List<IdentityConflict>>
{
    public override List<IdentityConflict>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var list = new List<IdentityConflict>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            // The whole value is unusable as a list: keep it verbatim as one raw element.
            if (doc.RootElement.ValueKind != JsonValueKind.Null)
                list.Add(new IdentityConflict { Raw = doc.RootElement.Clone() });
            return list;
        }

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            try
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new JsonException("IdentityConflict must be a JSON object.");

                var conflict = element.Deserialize<IdentityConflict>(IdentityJson.Inner);
                if (conflict is null)
                    throw new JsonException("IdentityConflict was null.");

                list.Add(conflict);
            }
            catch (Exception ex) when (IdentityJson.IsUnderstandingFailure(ex))
            {
                Logger.Warn($"An identity conflict entry could not be understood - kept verbatim ({ex.Message}).");
                list.Add(new IdentityConflict { Raw = element.Clone() });
            }
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<IdentityConflict> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var conflict in value)
        {
            if (conflict.Raw is { } raw)
                raw.WriteTo(writer);
            else
                JsonSerializer.Serialize(writer, conflict, IdentityJson.Inner);
        }

        writer.WriteEndArray();
    }
}

/// <summary>A revision counter that loads as 0 when its persisted value has the wrong type (5.2 item 4). Range
/// clamping happens in SettingsService.Load exactly as for ArtworkRevision.</summary>
public sealed class TolerantLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value))
            return value;

        reader.Skip();
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>ArtworkSelection.DerivedFrom: a value that cannot be understood loads as null. Null is the SAFE reading -
/// it authorizes nothing by itself (the legacy-continuity branch additionally needs a Pending LegacyAssociation).</summary>
public sealed class TolerantIdentityKeyConverter : JsonConverter<IdentityKey>
{
    public override IdentityKey? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var key = doc.RootElement.Deserialize<IdentityKey>(IdentityJson.Inner);
            return key is null || string.IsNullOrWhiteSpace(key.Id) || string.IsNullOrWhiteSpace(key.Namespace.Value) ? null : key;
        }
        catch (Exception ex) when (IdentityJson.IsUnderstandingFailure(ex))
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, IdentityKey value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, IdentityJson.Inner);
}
