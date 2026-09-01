using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Finds games under a watched folder.
///
/// This used to assume every immediate subfolder was one game, so pointing it at a drive root gave
/// you entries called "XboxGames" and "WindowsApps" rather than the games inside them. It now walks
/// down looking for folders that actually contain a game executable, claims that folder as one
/// game, and stops descending into it - so adding a whole drive works.
/// </summary>
public static class ManualFolderScanner
{
    private static readonly string[] ExcludedExePatterns =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "directx", "dotnet",
        "crashreport", "crashpad", "redist", "prereq", "easyanticheat", "battleye",
        "vc_redist", "ueprereq", "helper", "updater", "uninstall", "cleanup",
        "touchup", "activation", "diagnostic", "reporter",
    };

    /// <summary>Folders that never contain a game of their own, or that are too expensive/noisy to walk.</summary>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "windowsapps", "$recycle.bin", "system volume information", "recovery",
        "programdata", "perflogs", "appdata", "msocache", "intel", "amd", "nvidia",
        "drivers", "temp", "tmp", "node_modules", "_commonredist", "commonredist",
        "directx", "redist", "redistributables", "dotnet", "vcredist", "_installer",
    };

    /// <summary>Generic container folders - when a game's exe lives in one of these, the game's real
    /// name is the nearest meaningful ancestor (Call of Duty\Content\game.exe -> "Call of Duty").</summary>
    private static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games",
        "retail", "build", "release", "application", "app", "data", "engine", "launcher",
    };

    private const int MaxWalkDepth = 5;
    private const long MinGameExeBytes = 256 * 1024;

    public static List<GameEntry> Scan(IEnumerable<WatchedFolder> watchedFolders)
    {
        var games = new List<GameEntry>();

        foreach (var watched in watchedFolders)
        {
            try
            {
                // Heals a drive-letter change (external drive reconnected under a different letter)
                // by re-deriving the path from the folder's volume anchor before giving up on it.
                if (!WatchedFolderResolver.TryResolve(watched))
                {
                    Logger.Warn($"Watched folder unavailable, skipping: {watched.Path}");
                    continue;
                }

                var before = games.Count;
                Walk(watched.Path, watched.Path, depth: 0, games);
                Logger.Info($"  watched '{watched.Path}': {games.Count - before} game(s).");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed scanning watched folder '{watched.Path}', skipping it.", ex);
                // A watched folder on a removable/external drive can go away mid-scan (unplugged,
                // asleep, slow to spin up) - skip it rather than losing every other folder's results.
            }
        }

        return games;
    }

    private static void Walk(string folder, string watchedRoot, int depth, List<GameEntry> games)
    {
        if (depth > MaxWalkDepth)
            return;

        string[] exes;
        string[] subDirs;
        try
        {
            exes = Directory.GetFiles(folder, "*.exe");
            subDirs = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // The watched root itself isn't a game - loose exes sitting in it are treated individually.
        if (depth > 0)
        {
            var gameExe = PickGameExe(exes);
            if (gameExe is not null)
            {
                games.Add(BuildEntry(folder, gameExe, watchedRoot));
                return; // claimed: don't descend and produce nested duplicates
            }
        }
        else
        {
            foreach (var exe in exes.Where(e => !IsExcludedExe(e)))
                games.Add(BuildEntry(Path.GetDirectoryName(exe) ?? folder, exe, watchedRoot));
        }

        foreach (var sub in subDirs)
        {
            if (ExcludedFolderNames.Contains(Path.GetFileName(sub)))
                continue;

            Walk(sub, watchedRoot, depth + 1, games);
        }
    }

    /// <summary>The largest non-excluded exe of a plausible size, or null if this folder holds no game.</summary>
    private static string? PickGameExe(string[] exes)
    {
        FileInfo? best = null;

        foreach (var exe in exes)
        {
            if (IsExcludedExe(exe))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(exe);
                if (info.Length < MinGameExeBytes)
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (best is null || info.Length > best.Length)
                best = info;
        }

        return best?.FullName;
    }

    private static GameEntry BuildEntry(string gameFolder, string exePath, string watchedRoot)
    {
        var name = ResolveName(gameFolder, watchedRoot, exePath);

        return new GameEntry
        {
            Id = StableId(gameFolder),
            Name = name,
            ExecutablePath = exePath,
            InstallDir = gameFolder,
            Source = GameSource.Manual,
        };
    }

    /// <summary>Walks up past generic container folders so the game gets its real name.</summary>
    private static string ResolveName(string gameFolder, string watchedRoot, string exePath)
    {
        var current = new DirectoryInfo(gameFolder);
        var rootFull = Path.GetFullPath(watchedRoot).TrimEnd(Path.DirectorySeparatorChar);

        while (current is not null
               && GenericFolderNames.Contains(current.Name)
               && !string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), rootFull,
                   StringComparison.OrdinalIgnoreCase))
        {
            var parent = current.Parent;
            if (parent is null
                || string.Equals(parent.FullName.TrimEnd(Path.DirectorySeparatorChar), rootFull,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        var name = current?.Name;
        return string.IsNullOrWhiteSpace(name) || GenericFolderNames.Contains(name)
            ? Path.GetFileNameWithoutExtension(exePath)
            : name;
    }

    private static bool IsExcludedExe(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return ExcludedExePatterns.Any(name.Contains);
    }

    // string.GetHashCode() is randomized per process run, which would break icon caching
    // (cache filename is derived from Id) and any future per-game overrides keyed by Id.
    private static string StableId(string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return $"manual-{Convert.ToHexString(hash)}";
    }
}
