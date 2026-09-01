using System.Text.Json.Serialization;
using GameLauncher.Serialization;

namespace GameLauncher.Models;

/// <summary>
/// A folder to scan for games, anchored to its volume so a drive-letter change (unplug an external
/// drive, plug it into a different port) doesn't silently break it. VolumeSerialNumber/RelativePath
/// are populated the first time the folder resolves successfully; until then, Path alone is used.
/// </summary>
[JsonConverter(typeof(WatchedFolderJsonConverter))]
public sealed class WatchedFolder
{
    public required string Path { get; set; }
    public uint? VolumeSerialNumber { get; set; }
    public string? RelativePath { get; set; }
}
