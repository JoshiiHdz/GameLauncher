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
        try
        {
            using var stream = File.OpenRead(itemFile);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("DisplayName", out var nameProp) ||
                !root.TryGetProperty("InstallLocation", out var installLocProp) ||
                !root.TryGetProperty("LaunchExecutable", out var launchExeProp) ||
                !root.TryGetProperty("AppName", out var appNameProp))
            {
                Logger.Warn($"  Epic: manifest '{Path.GetFileName(itemFile)}' is missing a required field, skipping it.");
                return null;
            }

            var installLocation = installLocProp.GetString();
            var launchExecutable = launchExeProp.GetString();
            if (string.IsNullOrEmpty(installLocation) || string.IsNullOrEmpty(launchExecutable))
            {
                Logger.Warn($"  Epic: '{nameProp.GetString()}' has no install location or launch exe recorded, skipping it.");
                return null;
            }

            var exePath = Path.Combine(installLocation, launchExecutable);
            if (!File.Exists(exePath))
            {
                // A manifest for a game that's since been uninstalled or moved (Epic doesn't always
                // clean these up) - real signal for "why isn't this in my library" rather than noise.
                Logger.Warn($"  Epic: '{nameProp.GetString()}' manifest points at a missing exe, skipping it: {exePath}");
                return null;
            }

            return new GameEntry
            {
                Id = $"epic-{appNameProp.GetString()}",
                Name = nameProp.GetString() ?? Path.GetFileName(installLocation),
                ExecutablePath = exePath,
                InstallDir = installLocation,
                Source = GameSource.Epic,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read Epic manifest '{itemFile}', skipping it.", ex);
            return null;
        }
    }
}
