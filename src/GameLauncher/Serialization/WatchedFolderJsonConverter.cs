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
            return new WatchedFolder { Path = reader.GetString()! };

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return new WatchedFolder
        {
            Path = root.GetProperty("Path").GetString()!,
            VolumeSerialNumber = root.TryGetProperty("VolumeSerialNumber", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetUInt32()
                : null,
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
