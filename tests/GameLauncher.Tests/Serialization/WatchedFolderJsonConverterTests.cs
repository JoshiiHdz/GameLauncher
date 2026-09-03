using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Tests.Serialization;

/// <summary>Covers the malformed-settings.json crash scenarios found during the settings-durability
/// audit: every one of these used to throw an exception type SettingsService.Load didn't catch,
/// crashing the app at startup instead of falling back to defaults.</summary>
public class WatchedFolderJsonConverterTests
{
    private static List<WatchedFolder>? Deserialize(string json) =>
        JsonSerializer.Deserialize<List<WatchedFolder>>(json);

    [Fact]
    public void OldStringFormat_StillLoads()
    {
        var result = Deserialize("""["C:\\Games"]""");

        var folder = Assert.Single(result!);
        Assert.Equal("C:\\Games", folder.Path);
        Assert.Null(folder.VolumeSerialNumber);
        Assert.Null(folder.RelativePath);
    }

    [Fact]
    public void CurrentObjectFormat_RoundTrips()
    {
        var original = new List<WatchedFolder>
        {
            new() { Path = "D:\\Games", VolumeSerialNumber = 12345, RelativePath = "Games" },
        };

        var json = JsonSerializer.Serialize(original);
        var result = Deserialize(json);

        var folder = Assert.Single(result!);
        Assert.Equal("D:\\Games", folder.Path);
        Assert.Equal(12345u, folder.VolumeSerialNumber);
        Assert.Equal("Games", folder.RelativePath);
    }

    [Fact]
    public void EmptyStringEntry_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => Deserialize("""[""]"""));
    }

    [Fact]
    public void MissingPathProperty_ThrowsJsonException()
    {
        // Previously threw KeyNotFoundException from root.GetProperty("Path") - a type
        // SettingsService.Load doesn't catch, crashing the app instead of falling back to defaults.
        Assert.Throws<JsonException>(() => Deserialize("""[{"VolumeSerialNumber": 123}]"""));
    }

    [Fact]
    public void PathIsWrongJsonType_ThrowsJsonException()
    {
        // Previously threw InvalidOperationException from pathElement.GetString() on a non-string
        // element - also uncaught by SettingsService.Load.
        Assert.Throws<JsonException>(() => Deserialize("""[{"Path": 123}]"""));
    }

    [Fact]
    public void EntryIsNotStringOrObject_ThrowsJsonException()
    {
        // A bare number or array entry previously fell into JsonDocument.ParseValue and threw
        // InvalidOperationException from TryGetProperty on a non-object element.
        Assert.Throws<JsonException>(() => Deserialize("[123]"));
    }

    [Fact]
    public void VolumeSerialNumberOutOfUInt32Range_ThrowsJsonException()
    {
        // Previously threw FormatException from GetUInt32() - also uncaught by SettingsService.Load.
        Assert.Throws<JsonException>(() => Deserialize("""[{"Path": "C:\\Games", "VolumeSerialNumber": -1}]"""));
    }

    [Fact]
    public void MissingOptionalProperties_DefaultToNull()
    {
        var result = Deserialize("""[{"Path": "C:\\Games"}]""");

        var folder = Assert.Single(result!);
        Assert.Equal("C:\\Games", folder.Path);
        Assert.Null(folder.VolumeSerialNumber);
        Assert.Null(folder.RelativePath);
    }
}
