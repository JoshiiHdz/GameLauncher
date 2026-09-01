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

                    // The XboxGames folder name is often a generic umbrella the Xbox app reuses across
                    // years/editions (a "Call of Duty" folder holding this year's actual release, e.g.
                    // Black Ops 7) rather than the real title, which threw off both the displayed name
                    // and the cover art search (search used this name too, and matched the wrong,
                    // generic game's art as a result). The Start Menu entry's own display name is the
                    // real title, so use that in place of the folder name whenever one is matched.
                    var match = MatchAumid(name, packagedApps);
                    var displayName = name;

                    if (match is null)
                    {
                        Logger.Warn($"  Xbox: no Start Menu entry matched '{name}' - launching the exe "
                                    + "directly (can error for packaged titles) and using the folder name as-is.");
                    }
                    else if (!string.Equals(match.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"  Xbox: '{name}' folder is actually '{match.Value.Name}' per the Start Menu.");
                        displayName = match.Value.Name;
                    }

                    games.Add(new GameEntry
                    {
                        Id = $"xbox-{StableId(gameDir)}",
                        Name = displayName,
                        ExecutablePath = exe,
                        InstallDir = gameDir,
                        Source = GameSource.Xbox,
                        // Launching the exe directly skips the activation context Windows sets up for
                        // packaged apps - shell:appsFolder is how a real shortcut actually launches one.
                        LaunchUri = match is null ? null : $"shell:appsFolder\\{match.Value.Aumid}",
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

    /// <summary>Matches an Xbox game folder name against Get-StartApps entries, returning the real
    /// display name alongside the AUMID. Exact (normalized) match first, then a looser contains-match,
    /// since folder names and Start Menu display names don't always agree exactly (e.g. a folder like
    /// "Call of Duty" reused for whichever year's title is actually installed, "Call of Duty: Black
    /// Ops 7"). With more than one installed title the folder name could contain-match either, so this
    /// is best-effort, not a guarantee, when a franchise has multiple entries on the same PC.</summary>
    private static (string Name, string Aumid)? MatchAumid(
        string folderName, IReadOnlyList<(string Name, string Aumid)> packagedApps)
    {
        var normalizedFolder = Normalize(folderName);
        if (normalizedFolder.Length == 0)
            return null;

        var exact = packagedApps.FirstOrDefault(a => Normalize(a.Name) == normalizedFolder);
        if (exact != default)
            return exact;

        var loose = packagedApps.FirstOrDefault(a =>
        {
            var normalizedName = Normalize(a.Name);
            return normalizedName.Length > 0
                   && (normalizedName.Contains(normalizedFolder) || normalizedFolder.Contains(normalizedName));
        });

        return loose == default ? null : loose;
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
