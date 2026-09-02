using System.IO;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>
/// Finds Ubisoft Connect (formerly Uplay) games. Ubisoft Connect registers every installed game
/// under HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;numeric game id&gt;, each with its
/// own InstallDir value - a stable, enumerable manifest like Steam's, unlike launchers that only
/// expose per-title uninstall entries. The launch URI format (uplay://launch/&lt;id&gt;/0) is the
/// commonly documented one but hasn't been verified against a live install.
/// </summary>
public static class UbisoftScanner
{
    private const string InstallsKeyPath = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        try
        {
            using var installs = Registry.LocalMachine.OpenSubKey(InstallsKeyPath);
            if (installs is null)
            {
                Logger.Info("Ubisoft Connect: not installed (no Installs key).");
                return games;
            }

            foreach (var gameId in installs.GetSubKeyNames())
            {
                try
                {
                    using var gameKey = installs.OpenSubKey(gameId);
                    var installDir = gameKey?.GetValue("InstallDir") as string;
                    if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                    {
                        Logger.Warn($"  Ubisoft Connect: install id '{gameId}' has no valid InstallDir, skipping it.");
                        continue;
                    }

                    var exe = GameExeFinder.FindLargestExe(installDir);
                    if (exe is null)
                    {
                        Logger.Warn($"  Ubisoft Connect: no launchable exe found under '{installDir}' (id '{gameId}').");
                        continue;
                    }

                    games.Add(new GameEntry
                    {
                        Id = $"ubisoft-{gameId}",
                        Name = Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)),
                        ExecutablePath = exe,
                        InstallDir = installDir,
                        Source = GameSource.Ubisoft,
                        LaunchUri = $"uplay://launch/{gameId}/0",
                    });
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    Logger.Warn($"  Ubisoft Connect: couldn't read install id '{gameId}'.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read Ubisoft registry key '{InstallsKeyPath}'.", ex);
        }

        if (games.Count == 0)
            Logger.Info("Ubisoft Connect: nothing found.");

        return games;
    }
}
