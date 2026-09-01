using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Finds Xbox / Game Pass PC games. The Xbox app installs them into an "XboxGames" folder at the
/// root of whichever drive you chose, one folder per game, with the executable usually under a
/// "Content" subfolder. There's no manifest to read, so this checks that folder on every ready
/// drive.
///
/// Deliberately not touching WindowsApps: those are locked-down MSIX installs that can't be
/// launched by path anyway.
/// </summary>
public static class XboxScanner
{
    private const string XboxFolderName = "XboxGames";

    /// <summary>Game Pass installs drop DLC/tracker stub folders next to real games; they carry no
    /// playable executable, but they're skipped by name too so they never surface as blank cards.</summary>
    private static readonly string[] StubNamePatterns =
        { " dlc", "launch tracker", "game stub", "game pass pack", "pre-order", "preorder" };

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();
        var anyFolderFound = false;
        var packagedApps = StartAppsResolver.GetPackagedApps();

        foreach (var drive in GetReadyDrives())
        {
            var xboxDir = Path.Combine(drive, XboxFolderName);
            if (!Directory.Exists(xboxDir))
                continue;

            anyFolderFound = true;

            try
            {
                foreach (var gameDir in Directory.EnumerateDirectories(xboxDir))
                {
                    var name = Path.GetFileName(gameDir);
                    if (IsStub(name))
                    {
                        Logger.Info($"  Xbox: skipping DLC/stub folder '{name}'.");
                        continue;
                    }

                    var exe = FindGameExe(gameDir);
                    if (exe is null)
                    {
                        Logger.Info($"  Xbox: no launchable exe under '{name}'.");
                        continue;
                    }

                    var aumid = MatchAumid(name, packagedApps);
                    if (aumid is null)
                    {
                        Logger.Warn($"  Xbox: no Start Menu entry matched '{name}' - launching the exe "
                                    + "directly, which can error for packaged titles that expect proper activation.");
                    }

                    games.Add(new GameEntry
                    {
                        Id = $"xbox-{StableId(gameDir)}",
                        Name = name,
                        ExecutablePath = exe,
                        InstallDir = gameDir,
                        Source = GameSource.Xbox,
                        // Launching the exe directly skips the activation context Windows sets up for
                        // packaged apps - shell:appsFolder is how a real shortcut actually launches one.
                        LaunchUri = aumid is null ? null : $"shell:appsFolder\\{aumid}",
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed reading Xbox games from '{xboxDir}'.", ex);
            }
        }

        if (!anyFolderFound)
            Logger.Info("Xbox: no XboxGames folder found on any drive.");

        return games;
    }

    private static string? FindGameExe(string gameDir)
    {
        // Xbox layout is usually <Game>\Content\<game>.exe, but check the game folder itself too.
        var searchDirs = new List<string> { gameDir };

        try
        {
            searchDirs.AddRange(Directory.EnumerateDirectories(gameDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        FileInfo? best = null;

        foreach (var dir in searchDirs)
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    if (fileName.Contains("unins") || fileName.Contains("redist") || fileName.Contains("crash"))
                        continue;

                    var info = new FileInfo(exe);
                    if (best is null || info.Length > best.Length)
                        best = info;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return best?.FullName;
    }

    /// <summary>Matches an Xbox game folder name against Get-StartApps entries. Exact (normalized)
    /// match first, then a looser contains-match, since folder names and Start Menu display names
    /// don't always agree exactly (e.g. a trailing edition/region suffix).</summary>
    private static string? MatchAumid(string folderName, IReadOnlyList<(string Name, string Aumid)> packagedApps)
    {
        var normalizedFolder = Normalize(folderName);
        if (normalizedFolder.Length == 0)
            return null;

        var exact = packagedApps.FirstOrDefault(a => Normalize(a.Name) == normalizedFolder);
        if (exact != default)
            return exact.Aumid;

        var loose = packagedApps.FirstOrDefault(a =>
        {
            var normalizedName = Normalize(a.Name);
            return normalizedName.Length > 0
                   && (normalizedName.Contains(normalizedFolder) || normalizedFolder.Contains(normalizedName));
        });

        return loose == default ? null : loose.Aumid;
    }

    private static string Normalize(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsStub(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        return StubNamePatterns.Any(lower.Contains);
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
