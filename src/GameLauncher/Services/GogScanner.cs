using System.IO;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

public static class GogScanner
{
    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        ScanKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games", games);
        ScanKey(Registry.LocalMachine, @"SOFTWARE\GOG.com\Games", games);

        return games;
    }

    private static void ScanKey(RegistryKey root, string keyPath, List<GameEntry> games)
    {
        try
        {
            using var gamesKey = root.OpenSubKey(keyPath);
            if (gamesKey is null)
                return;

            foreach (var gameId in gamesKey.GetSubKeyNames())
            {
                using var gameKey = gamesKey.OpenSubKey(gameId);
                if (gameKey is null)
                    continue;

                var name = gameKey.GetValue("gameName") as string;
                var exe = gameKey.GetValue("exe") as string;
                var path = gameKey.GetValue("path") as string;

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    // A real GOG registry entry that failed to become a playable game - worth a
                    // trace, unlike a routine "not a game" skip elsewhere, since this is exactly the
                    // shape of "why didn't my GOG game show up" a user's log needs to answer.
                    Logger.Warn($"  GOG: '{gameId}' registry entry found but unusable "
                        + $"(name={(string.IsNullOrEmpty(name) ? "missing" : name)}, "
                        + $"exe={(string.IsNullOrEmpty(exe) ? "missing" : (File.Exists(exe) ? "ok" : "not found: " + exe))}).");
                    continue;
                }

                var id = $"gog-{gameId}";
                if (games.Any(g => g.Id == id))
                    continue;

                games.Add(new GameEntry
                {
                    Id = id,
                    Name = name,
                    ExecutablePath = exe,
                    InstallDir = path ?? Path.GetDirectoryName(exe) ?? exe,
                    Source = GameSource.Gog,
                });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read GOG registry key '{keyPath}'.", ex);
        }
    }
}
