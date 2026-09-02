using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>
/// Finds EA app / Origin games. EA registers each installed game under
/// HKLM\SOFTWARE\WOW6432Node\Electronic Arts\<Game> with an install directory, which is the
/// reliable route. As a fallback it also checks the conventional "EA Games" / "Origin Games"
/// folders on every ready drive, since EA lets you install to any drive.
/// </summary>
public static class EaScanner
{
    private static readonly string[] LibraryFolderNames = { "EA Games", "Origin Games", "EASports" };

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        ScanRegistry(games);
        ScanOriginGamesRegistry(games);
        ScanKnownFolders(games);

        if (games.Count == 0)
            Logger.Info("EA: nothing found in the registry or the usual EA/Origin folders.");

        return games;
    }

    private static void ScanRegistry(List<GameEntry> games)
    {
        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Electronic Arts",
                     @"SOFTWARE\Electronic Arts",
                 })
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(keyPath);
                if (root is null)
                    continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    // These two are the launcher itself, not games.
                    if (name.Equals("EA Desktop", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("EA Core", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var gameKey = root.OpenSubKey(name);
                    var installDir = gameKey?.GetValue("Install Dir") as string
                                     ?? gameKey?.GetValue("InstallDir") as string
                                     ?? gameKey?.GetValue("Install Location") as string;

                    if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                        continue;

                    AddIfPlayable(games, installDir, name);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                Logger.Warn($"Couldn't read EA registry key '{keyPath}'.", ex);
            }
        }
    }

    /// <summary>
    /// A friend's PC had every other source load but zero EA games, with no log to diagnose it from.
    /// The "Electronic Arts" key above is where EA app registers its own newer-style titles, but
    /// Origin games (and some EA app titles installed the older way) instead register each install
    /// under SOFTWARE\WOW6432Node\Origin Games\&lt;content id&gt;, which was never checked - a very
    /// plausible explanation for "every source works except EA" without needing a log to prove it.
    /// Entries here are keyed by an opaque content ID rather than a readable name, so the game's own
    /// folder name is used instead, same as the folder-scan fallback below.
    /// </summary>
    private static void ScanOriginGamesRegistry(List<GameEntry> games)
    {
        const string keyPath = @"SOFTWARE\WOW6432Node\Origin Games";

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(keyPath);
            if (root is null)
                return;

            foreach (var contentId in root.GetSubKeyNames())
            {
                using var gameKey = root.OpenSubKey(contentId);
                var installDir = gameKey?.GetValue("Install Dir") as string
                                 ?? gameKey?.GetValue("InstallDir") as string;

                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                    continue;

                var name = Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar));
                AddIfPlayable(games, installDir, name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read EA registry key '{keyPath}'.", ex);
        }
    }

    private static void ScanKnownFolders(List<GameEntry> games)
    {
        foreach (var drive in GetReadyDrives())
        {
            foreach (var libraryName in LibraryFolderNames)
            {
                var libraryDir = Path.Combine(drive, libraryName);
                if (!Directory.Exists(libraryDir))
                    continue;

                try
                {
                    foreach (var gameDir in Directory.EnumerateDirectories(libraryDir))
                        AddIfPlayable(games, gameDir, Path.GetFileName(gameDir));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Failed reading EA games from '{libraryDir}'.", ex);
                }
            }
        }
    }

    private static void AddIfPlayable(List<GameEntry> games, string installDir, string name)
    {
        var id = $"ea-{StableId(installDir)}";
        if (games.Any(g => g.Id == id))
            return;

        // "showcase" is EA Sports-specific (a demo/kiosk mode bundled alongside the real game, e.g.
        // "FC26_Showcase.exe" next to "FC26.exe") - not a broadly generalizable enough term for the
        // shared default list, but a real, confirmed false-pick here otherwise.
        var exe = GameExeFinder.FindLargestExe(installDir, extraExcludePatterns: new[] { "showcase" });
        if (exe is null)
            return;

        games.Add(new GameEntry
        {
            Id = id,
            Name = name,
            ExecutablePath = exe,
            InstallDir = installDir,
            Source = GameSource.Ea,
        });
    }

    private static IEnumerable<string> GetReadyDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var ready = false;
            try
            {
                ready = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
            }

            if (ready)
                yield return drive.RootDirectory.FullName;
        }
    }

    private static string StableId(string path)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
}
