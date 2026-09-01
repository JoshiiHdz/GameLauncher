using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class ManualFolderScanner
{
    private static readonly string[] ExcludedNamePatterns =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "directx", "dotnet",
        "crashreport", "crashpad", "redist", "prereq", "easyanticheat", "battleye",
        "vc_redist", "ueprereq", "helper", "updater", "uninstall",
    };

    private const int MaxScanDepth = 3;

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

                var root = watched.Path;

                foreach (var subDir in Directory.EnumerateDirectories(root))
                {
                    var entry = BuildEntryForFolder(subDir);
                    if (entry is not null)
                        games.Add(entry);
                }

                // Also treat exes sitting directly in the watched root as standalone games.
                foreach (var exe in Directory.EnumerateFiles(root, "*.exe"))
                {
                    if (IsExcluded(exe))
                        continue;

                    games.Add(new GameEntry
                    {
                        Id = StableId(exe),
                        Name = Path.GetFileNameWithoutExtension(exe),
                        ExecutablePath = exe,
                        InstallDir = root,
                        Source = GameSource.Manual,
                    });
                }
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

    private static GameEntry? BuildEntryForFolder(string folder)
    {
        var bestExe = FindBestExecutable(folder, depth: 0);
        if (bestExe is null)
            return null;

        return new GameEntry
        {
            Id = StableId(folder),
            Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
            ExecutablePath = bestExe,
            InstallDir = folder,
            Source = GameSource.Manual,
        };
    }

    private static string? FindBestExecutable(string folder, int depth)
    {
        if (depth > MaxScanDepth)
            return null;

        FileInfo? best = null;

        try
        {
            foreach (var exe in Directory.EnumerateFiles(folder, "*.exe"))
            {
                if (IsExcluded(exe))
                    continue;

                var info = new FileInfo(exe);
                if (best is null || info.Length > best.Length)
                    best = info;
            }

            if (best is null)
            {
                foreach (var subDir in Directory.EnumerateDirectories(folder))
                {
                    var candidate = FindBestExecutable(subDir, depth + 1);
                    if (candidate is null)
                        continue;

                    var candidateInfo = new FileInfo(candidate);
                    if (best is null || candidateInfo.Length > best.Length)
                        best = candidateInfo;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        return best?.FullName;
    }

    private static bool IsExcluded(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return ExcludedNamePatterns.Any(name.Contains);
    }

    // string.GetHashCode() is randomized per process run, which would break icon caching
    // (cache filename is derived from Id) and any future per-game overrides keyed by Id.
    private static string StableId(string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return $"manual-{Convert.ToHexString(hash)}";
    }
}
