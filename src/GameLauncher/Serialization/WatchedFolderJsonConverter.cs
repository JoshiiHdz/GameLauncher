using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Models;

namespace GameLauncher.Serialization;

/// <summary>
/// Reads WatchedFolders from either the old format (a plain path string) or the current format
/// (an object with a volume anchor), so settings.json files written before the anchor feature
/// existed still load instead of getting wiped back to defaults. Always writes the current format.
/// </summary>
public sealed class WatchedFolderJsonConverter : JsonConverter<WatchedFolder>
{
    public override WatchedFolder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var path = reader.GetString();
            if (string.IsNullOrEmpty(path))
                throw new JsonException("A WatchedFolder string entry was null or empty.");
            return new WatchedFolder { Path = path };
        }

        // Every other shape (a number, an array, a bool...) would otherwise flow into
        // JsonDocument.ParseValue below and fail on TryGetProperty with an InvalidOperationException
        // - a type SettingsService.Load doesn't catch. Rejecting it here explicitly, as a JsonException,
        // keeps every malformed-entry shape funneled through the one path Load already recovers from.
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"A WatchedFolder entry must be a string or an object, not {reader.TokenType}.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // TryGetProperty rather than GetProperty: a hand-edited or corrupted settings.json missing
        // "Path" would otherwise throw KeyNotFoundException, a type SettingsService.Load doesn't
        // catch, crashing the app at startup instead of falling back to defaults. ValueKind is checked
        // before GetString() too - "Path": 123 is valid JSON but GetString() throws
        // InvalidOperationException on a non-string element, another type Load doesn't catch.
        if (!root.TryGetProperty("Path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String
            || pathElement.GetString() is not { Length: > 0 } pathValue)
            throw new JsonException("A WatchedFolder object entry is missing a non-empty string 'Path'.");

        uint? volumeSerial = null;
        if (root.TryGetProperty("VolumeSerialNumber", out var v) && v.ValueKind == JsonValueKind.Number)
        {
            // TryGetUInt32 rather than GetUInt32: a negative or too-large number is valid JSON but
            // GetUInt32() throws FormatException on it - again a type Load doesn't catch. Out-of-range
            // just means "no usable anchor", same as the property being absent.
            if (!v.TryGetUInt32(out var parsedSerial))
                throw new JsonException("WatchedFolder.VolumeSerialNumber was out of range for a UInt32.");
            volumeSerial = parsedSerial;
        }

        return new WatchedFolder
        {
            Path = pathValue,
            VolumeSerialNumber = volumeSerial,
            RelativePath = root.TryGetProperty("RelativePath", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, WatchedFolder value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Path", value.Path);

        if (value.VolumeSerialNumber.HasValue)
            writer.WriteNumber("VolumeSerialNumber", value.VolumeSerialNumber.Value);
        else
            writer.WriteNull("VolumeSerialNumber");

        if (value.RelativePath is not null)
            writer.WriteString("RelativePath", value.RelativePath);
        else
            writer.WriteNull("RelativePath");

        writer.WriteEndObject();
    }
}
