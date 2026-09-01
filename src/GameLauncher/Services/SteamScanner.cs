using System.IO;
using System.Text.RegularExpressions;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

public static partial class SteamScanner
{
    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        var steamPath = GetSteamInstallPath();
        if (steamPath is null || !Directory.Exists(steamPath))
        {
            Logger.Info("Steam not detected (not installed, or install path not found in the registry).");
            return games;
        }

        foreach (var library in GetLibraryFolders(steamPath))
        {
            try
            {
                var steamAppsDir = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamAppsDir))
                {
                    Logger.Warn($"Steam library folder unavailable, skipping: {library}");
                    continue;
                }

                foreach (var manifest in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
                {
                    try
                    {
                        var entry = ParseManifest(manifest, steamAppsDir);
                        if (entry is not null)
                            games.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Logger.Warn($"Couldn't read Steam manifest '{manifest}', skipping it.", ex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed scanning Steam library '{library}', skipping it.", ex);
                // A library on a removable/external drive can go away mid-scan - skip it rather than
                // losing every other library's games (e.g. the ones still on the internal drive).
            }
        }

        return games;
    }

    private static string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path)
                return path.Replace('/', '\\');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path)
                return path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static List<string> GetLibraryFolders(string steamPath)
    {
        var libraries = new List<string> { steamPath };

        var libraryFoldersVdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersVdf))
            return libraries;

        var content = File.ReadAllText(libraryFoldersVdf);
        foreach (Match match in PathRegex().Matches(content))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                libraries.Add(path);
        }

        return libraries;
    }

    private static GameEntry? ParseManifest(string manifestPath, string steamAppsDir)
    {
        var content = File.ReadAllText(manifestPath);

        var appIdMatch = AppIdRegex().Match(content);
        var nameMatch = NameRegex().Match(content);
        var installDirMatch = InstallDirRegex().Match(content);

        if (!appIdMatch.Success || !nameMatch.Success || !installDirMatch.Success)
            return null;

        var appId = appIdMatch.Groups[1].Value;
        var name = nameMatch.Groups[1].Value;
        var installDir = Path.Combine(steamAppsDir, "common", installDirMatch.Groups[1].Value);

        if (!Directory.Exists(installDir))
            return null;

        // The .acf manifest doesn't list a launch exe, and games launch via LaunchUri anyway,
        // so ExecutablePath points at the install dir; IconService falls back to the largest .exe in it.
        return new GameEntry
        {
            Id = $"steam-{appId}",
            Name = name,
            ExecutablePath = installDir,
            InstallDir = installDir,
            Source = GameSource.Steam,
            LaunchUri = $"steam://rungameid/{appId}",
        };
    }

    [GeneratedRegex("\"path\"\\s*\"(.*?)\"")]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"appid\"\\s*\"(\\d+)\"")]
    private static partial Regex AppIdRegex();

    [GeneratedRegex("\"name\"\\s*\"(.*?)\"")]
    private static partial Regex NameRegex();

    [GeneratedRegex("\"installdir\"\\s*\"(.*?)\"")]
    private static partial Regex InstallDirRegex();
}
