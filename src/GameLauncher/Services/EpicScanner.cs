using System.IO;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class EpicScanner
{
    private static readonly string ManifestsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        if (!Directory.Exists(ManifestsDir))
        {
            Logger.Info("Epic Games Launcher not detected (manifests folder not found).");
            return games;
        }

        foreach (var itemFile in Directory.EnumerateFiles(ManifestsDir, "*.item"))
        {
            var entry = ParseItem(itemFile);
            if (entry is not null)
                games.Add(entry);
        }

        return games;
    }

    private static GameEntry? ParseItem(string itemFile)
    {
        string json;
        try
        {
            json = File.ReadAllText(itemFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read Epic manifest '{itemFile}', skipping it.", ex);
            return null;
        }

        return ParseManifest(json, Path.GetFileName(itemFile));
    }

    /// <summary>Parses one manifest's JSON content in isolation from file I/O, so malformed-data
    /// handling (an unexpected root shape, missing fields, wrong-typed fields) can be exercised
    /// directly in tests without touching the real filesystem or the hardcoded
    /// %ProgramData%\Epic\... path Scan() reads from.
    ///
    /// TryGetProperty alone only proves a field EXISTS, not that it has the expected string type - a
    /// manifest with, say, "DisplayName": 123 would previously reach GetString() and throw
    /// InvalidOperationException, which escaped this method's old catch clause entirely (it only
    /// listed JsonException/IOException/UnauthorizedAccessException) and could crash the whole scan
    /// over one malformed file. Every field is now explicitly checked for ValueKind.String before
    /// GetString() is ever called, and the catch below is now the last-resort backstop for anything
    /// this explicit validation doesn't anticipate (a non-object root, for instance), not the primary
    /// mechanism.</summary>
    internal static GameEntry? ParseManifest(string json, string manifestNameForLogging)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                Logger.Warn($"  Epic: manifest '{manifestNameForLogging}' has an unexpected root (not an object), skipping it.");
                return null;
            }

            if (!TryGetRequiredString(root, "DisplayName", manifestNameForLogging, out var name) ||
                !TryGetRequiredString(root, "InstallLocation", manifestNameForLogging, out var installLocation) ||
                !TryGetRequiredString(root, "LaunchExecutable", manifestNameForLogging, out var launchExecutable) ||
                !TryGetRequiredString(root, "AppName", manifestNameForLogging, out var appName))
            {
                return null;
            }

            if (string.IsNullOrEmpty(installLocation) || string.IsNullOrEmpty(launchExecutable))
            {
                Logger.Warn($"  Epic: '{name}' has no install location or launch exe recorded, skipping it.");
                return null;
            }

            var exePath = Path.Combine(installLocation, launchExecutable);
            if (!File.Exists(exePath))
            {
                // A manifest for a game that's since been uninstalled or moved (Epic doesn't always
                // clean these up) - real signal for "why isn't this in my library" rather than noise.
                Logger.Warn($"  Epic: '{name}' manifest points at a missing exe, skipping it: {exePath}");
                return null;
            }

            return new GameEntry
            {
                Id = $"epic-{appName}",
                Name = name,
                ExecutablePath = exePath,
                InstallDir = installLocation,
                Source = GameSource.Epic,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Logger.Warn($"Couldn't parse Epic manifest '{manifestNameForLogging}', skipping it.", ex);
            return null;
        }
    }

    /// <summary>True only if `propertyName` exists on `root` AND is a JSON string - a present-but-
    /// wrong-typed field (a number, object, array, or null where a manifest string value is expected)
    /// is exactly as unusable as a missing one, and calling GetString() on it would throw
    /// InvalidOperationException rather than hitting the ordinary "missing field" handling this already
    /// logs and skips for.</summary>
    private static bool TryGetRequiredString(JsonElement root, string propertyName, string manifestNameForLogging, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            Logger.Warn($"  Epic: manifest '{manifestNameForLogging}' is missing or has a malformed "
                + $"'{propertyName}' field, skipping it.");
            return false;
        }

        value = prop.GetString() ?? "";
        return true;
    }
}
