using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>What one sweep of the disks learned: the game folders, and when and where it looked, so the next scans can reuse it.</summary>
public sealed record DiskSweep(List<string> Folders, DateTime SweptUtc, string Signature);

/// <summary>
/// Finds games that were never installed by a launcher: repacks, pre-installed copies, a game folder copied from another PC or a USB
/// stick. It looks at every ready drive the user has not switched off, but only at folder and file NAMES (one directory listing per
/// folder, nothing opened, nothing read), and recognises a game by what its engine leaves next to the exe: Unity's player and
/// "_Data" folder, Unreal's Engine/Binaries/Content, the Steamworks dll a repack ships, anti-cheat folders, Godot and GameMaker packs,
/// the files a crack drops, and so on. One such sign is enough; the weaker ones (an uninstaller, a redistributables folder, a ".pak")
/// need two. A folder with no plausible game exe is never a game, whatever else is in it.
///
/// It never replaces what the launchers report: those scans run first, their folders are skipped here, and anything this finds that
/// a launcher also knows is merged into the launcher's entry by the usual de-duplication. A full sweep is the one slow thing a scan can
/// do, so its folders are kept (see <see cref="DiskSweep"/>) and later scans only re-check them, until a sweep is due again or the
/// user asks for a rescan.
/// </summary>
public static class DiskGameFinder
{
    /// <summary>How long a sweep's result is trusted before the disks are walked again. A rescan the user asks for always walks.</summary>
    public static readonly TimeSpan SweepMaxAge = TimeSpan.FromHours(12);

    /// <summary>One drive may take this long before the walk of it is cut short (a huge archive disk should not hold up a scan).</summary>
    private static readonly TimeSpan DriveBudget = TimeSpan.FromSeconds(60);

    /// <summary>Folders that are never searched, anywhere: Windows and system data, other apps' storage, and launcher libraries that the
    /// launcher scans already cover (their leftovers are not games).</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "windows.old", "program files", "program files (x86)", "programdata", "windowsapps", "steamapps", "$recycle.bin",
        "system volume information", "recovery", "perflogs", "appdata", "msocache", "$winreagent", "config.msi", "found.000",
        "node_modules", ".git", "__pycache__", "drivers", "intel", "amd", "nvidia", "temp", "tmp", "_commonredist", "commonredist",
        "directx", "redist", "redistributables", "dotnet", "vcredist", "_installer", "cache", "caches",
    };

    /// <summary>Inside a user profile, only the places people really put games.</summary>
    private static readonly string[] UserFolders = ["Desktop", "Downloads", "Games"];

    private const int MaxDepth = 5;

    // ---- what a game folder looks like ---------------------------------------------------------------------------------

    /// <summary>File names that on their own say "this is a game": Unity, Steamworks as shipped by repacks, bink video, the files cracks
    /// drop, and engine packs that never appear outside a game.</summary>
    private static readonly HashSet<string> StrongFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "steam_api.dll", "steam_api64.dll", "steam_appid.txt",
        "steam_emu.ini", "codex.ini", "onlinefix.ini", "eossdk-win64-shipping.dll",
        "bink2w64.dll", "bink2w32.dll", "binkw32.dll", "binkw64.dll", "data.win", "game.win", "ue4prereqsetup_x64.exe",
        "ueprereqsetup_x64.exe", "gameassembly.dll", "discord_game_sdk.dll", "galaxy64.dll", "galaxy.dll", "skidrow.ini", "3dmgame.ini",
    };

    private static readonly string[] StrongExtensions = [".pck", ".vpk", ".bsa", ".ba2", ".forge", ".rpf", ".wad", ".uasset", ".utoc", ".ucas"];

    /// <summary>Folder names that mark a game: engine runtimes, anti-cheat, and the folder a crack leaves.</summary>
    private static readonly HashSet<string> StrongFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "MonoBleedingEdge", "EasyAntiCheat", "BattlEye", "EasyAntiCheat_EOS", "steam_settings", "CODEX", "SKIDROW", "PLAZA", "RUNE", "Crack",
        "Il2CppData",
    };

    /// <summary>A launcher's own install folder is not a game, whatever else it holds (Steam installed on D:\ carries game-like dlls).</summary>
    private static readonly HashSet<string> LauncherExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamservice.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "Battle.net.exe", "Battle.net Launcher.exe", "Origin.exe",
        "EADesktop.exe", "upc.exe", "UbisoftConnect.exe", "RockstarGamesLauncher.exe", "AmazonGames.exe",
    };

    private static readonly HashSet<string> WeakFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins000.exe", "unins000.dat", "unins001.exe", "installscript.vdf",
    };

    private static readonly string[] WeakExtensions = [".pak", ".bik", ".bk2", ".wem", ".bnk"];

    private static readonly HashSet<string> WeakFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_CommonRedist", "Redist", "__Installer", "DirectX", "Savegames", "Paks", "Engine",
    };

    /// <summary>How many strong and weak signs a folder's own entries show. Pure, so it is tested without a disk.</summary>
    internal static (int Strong, int Weak) Score(IReadOnlyCollection<string> fileNames, IReadOnlyCollection<string> folderNames)
    {
        var strong = 0;
        var weak = 0;

        foreach (var file in fileNames)
        {
            var extension = Path.GetExtension(file);
            if (StrongFiles.Contains(file) || file.StartsWith("Manifest_NonUFSFiles", StringComparison.OrdinalIgnoreCase)
                || StrongExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                strong++;
            else if (WeakFiles.Contains(file) || WeakExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                weak++;
        }

        // An exe with its own "<name>_Data" folder is Unity's layout, whatever the dll is called.
        var exeStems = fileNames.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folderNames)
        {
            if (folder.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && exeStems.Contains(folder[..^5]))
                strong++;
            else if (StrongFolders.Contains(folder))
                strong++;
            else if (WeakFolders.Contains(folder))
                weak++;
        }

        // Unreal ships Binaries and Content side by side; either with the Engine folder is the same layout.
        var has = (string name) => folderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (has("Binaries") && has("Content"))
            strong++;
        else if (has("Engine") && (has("Binaries") || has("Content")))
            strong++;

        return (strong, weak);
    }

    /// <summary>One strong sign, or two weak ones.</summary>
    internal static bool LooksLikeAGame(int strong, int weak) => strong >= 1 || weak >= 2;

    // ---- the sweep -----------------------------------------------------------------------------------------------------

    /// <summary>The drives a sweep covers: every ready fixed or removable drive that is not ignored.</summary>
    public static List<string> SearchableDrives(DriveFilter filter, IEnumerable<string>? readyDrives = null) =>
        (readyDrives ?? InstallPaths.ReadyDrives()).Where(d => !filter.IsIgnored(d)).ToList();

    /// <summary>A string that changes when the set of searched drives does, so a drive that appears (or is switched back on) makes the
    /// remembered result stale instead of being searched only at the next 12-hour mark.</summary>
    public static string SignatureOf(IEnumerable<string> drives) =>
        string.Join('|', drives.Select(d => DriveFilter.NormalizeLetter(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

    /// <summary>True when the remembered sweep can still be used.</summary>
    public static bool CanReuse(IReadOnlyCollection<string>? remembered, DateTime? sweptUtc, string? rememberedSignature, string signature,
        DateTime nowUtc, bool force) =>
        !force && remembered is not null && sweptUtc is { } at && nowUtc - at < SweepMaxAge && nowUtc >= at
        && string.Equals(rememberedSignature, signature, StringComparison.Ordinal);

    /// <summary>Walks the given drives in parallel and returns every folder that looks like a game's. <paramref name="knownDirs"/> (the
    /// install folders the launchers and watched folders already reported, without a trailing slash, in a case-insensitive set) are not entered.</summary>
    public static List<string> Sweep(IReadOnlyList<string> drives, IReadOnlySet<string> knownDirs, CancellationToken ct)
    {
        var found = new ConcurrentBag<string>();
        var units = drives.SelectMany(drive => TopFolders(drive).Select(dir => (Drive: drive, dir.Path, dir.Depth))).ToList();

        // Each drive gets its own clock, started when its first folder is picked up, so a slow archive disk is cut off on its own
        // and a fast one is never rushed by it.
        var driveClocks = new ConcurrentDictionary<string, Stopwatch>(StringComparer.OrdinalIgnoreCase);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6), CancellationToken = ct };

        Parallel.ForEach(units, options, unit =>
        {
            var clock = driveClocks.GetOrAdd(unit.Drive, _ => Stopwatch.StartNew());
            Walk(unit.Path, unit.Depth, knownDirs, found, () => clock.Elapsed > DriveBudget, ct);
        });

        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folders directly under a drive root worth walking (each becomes one unit of parallel work). A user profile is
    /// narrowed to Desktop, Downloads and Games; everything in <see cref="SkippedFolders"/> and every hidden or system folder is left.</summary>
    private static IEnumerable<(string Path, int Depth)> TopFolders(string drive)
    {
        List<DirectoryInfo> top;
        try
        {
            top = new DirectoryInfo(drive).EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in top)
        {
            if (ShouldSkip(dir))
                continue;

            if (string.Equals(dir.Name, "Users", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var profile in SafeDirectories(dir))
                {
                    foreach (var name in UserFolders)
                    {
                        var path = Path.Combine(profile.FullName, name);
                        if (Directory.Exists(path))
                            yield return (path, 2);
                    }
                }

                continue;
            }

            yield return (dir.FullName, 1);
        }
    }

    private static IEnumerable<DirectoryInfo> SafeDirectories(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool ShouldSkip(DirectoryInfo dir)
    {
        if (SkippedFolders.Contains(dir.Name) || ManualFolderScanner.IsExcludedFolder(dir.Name))
            return true;

        try
        {
            // Junctions and symlinks can loop back into the tree; hidden and system folders are Windows' own.
            var attributes = dir.Attributes;
            return attributes.HasFlag(FileAttributes.ReparsePoint) || (attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void Walk(string folder, int depth, IReadOnlySet<string> knownDirs, ConcurrentBag<string> found, Func<bool> outOfTime,
        CancellationToken ct)
    {
        if (depth > MaxDepth || outOfTime())
            return;

        ct.ThrowIfCancellationRequested();

        if (knownDirs.Contains(Normalize(folder)))
            return; // a launcher's own game: already in the library

        var files = new List<string>();
        var folders = new List<DirectoryInfo>();
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory)
                    folders.Add(directory);
                else
                    files.Add(entry.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (files.Any(LauncherExes.Contains))
            return; // a launcher's own folder

        var folderNames = folders.Select(f => f.Name).ToList();
        var (strong, weak) = Score(files, folderNames);
        if (strong == 0 && HasUnrealGameFolder(folder, folderNames))
            strong++;

        if (LooksLikeAGame(strong, weak) && ExeFor(folder, files, folderNames) is not null)
        {
            found.Add(folder);
            return; // claimed: what is inside belongs to this game
        }

        foreach (var sub in folders)
        {
            if (!ShouldSkip(sub))
                Walk(sub.FullName, depth + 1, knownDirs, found, outOfTime, ct);
        }
    }

    /// <summary>An Unreal game's root holds "Engine" next to the game's own folder, which has Binaries\Win64 inside. The listing of the
    /// root cannot show that, so the one folder worth a look is asked (only for roots that have an Engine folder at all).</summary>
    private static bool HasUnrealGameFolder(string folder, IReadOnlyCollection<string> folderNames)
    {
        if (!folderNames.Contains("Engine", StringComparer.OrdinalIgnoreCase))
            return false;

        foreach (var name in folderNames)
        {
            if (!string.Equals(name, "Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(folder, name, "Binaries", "Win64")))
                return true;
        }

        return false;
    }

    // ---- turning folders into library entries ---------------------------------------------------------------------------

    /// <summary>The exe to launch for a game folder, or null when it has none worth launching. A Unity folder prefers the exe that has a
    /// matching "_Data" folder; an Unreal one looks in Binaries\Win64, where the real exe lives when the root only holds a stub.</summary>
    internal static string? ExeFor(string folder, IReadOnlyCollection<string> fileNames, IReadOnlyCollection<string> folderNames)
    {
        var exes = fileNames.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(f => Path.Combine(folder, f)).ToArray();

        foreach (var exe in exes)
        {
            var stem = Path.GetFileNameWithoutExtension(exe);
            if (folderNames.Contains(stem + "_Data", StringComparer.OrdinalIgnoreCase) && ManualFolderScanner.IsPlausibleGameExe(exe))
                return exe;
        }

        if (ManualFolderScanner.PickGameExe(exes) is { } picked)
            return picked;

        // Unreal: <root>\<GameName>\Binaries\Win64\<GameName>-Win64-Shipping.exe
        foreach (var name in folderNames)
        {
            var win64 = Path.Combine(folder, name, "Binaries", "Win64");
            if (!Directory.Exists(win64))
                continue;

            try
            {
                var shipping = ManualFolderScanner.PickGameExe(Directory.GetFiles(win64, "*.exe"));
                if (shipping is not null)
                    return shipping;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary>Library entries for the given game folders. A folder that has gone, is on an ignored drive, or no longer has a game exe is
    /// dropped. Every entry is a "no launcher" game (<see cref="GameSource.Manual"/>), the same as one added by watching a folder.</summary>
    public static List<GameEntry> Resolve(IEnumerable<string> folders, DriveFilter filter, IReadOnlySet<string> knownDirs)
    {
        var games = new List<GameEntry>();
        foreach (var folder in folders)
        {
            try
            {
                if (filter.IsIgnored(folder) || knownDirs.Contains(Normalize(folder)) || !Directory.Exists(folder))
                    continue;

                var files = new List<string>();
                var folderNames = new List<string>();
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    (entry is DirectoryInfo ? folderNames : files).Add(entry.Name);

                if (ExeFor(folder, files, folderNames) is { } exe)
                    games.Add(ManualFolderScanner.BuildEntry(folder, exe, Path.GetPathRoot(folder) ?? folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Disk search: could not read '{folder}', skipping it.", ex);
            }
        }

        return games;
    }

    /// <summary>Runs the whole step for one scan: reuse the remembered folders while they are fresh, otherwise sweep. Returns the entries
    /// and, when a sweep ran, what it found for the caller to remember.</summary>
    public static (List<GameEntry> Games, DiskSweep? NewSweep) Find(DriveFilter filter, IReadOnlySet<string> knownDirs,
        IReadOnlyCollection<string>? rememberedFolders, DateTime? rememberedAtUtc, string? rememberedSignature, bool force, CancellationToken ct,
        IEnumerable<string>? readyDrives = null, DateTime? nowUtc = null)
    {
        var drives = SearchableDrives(filter, readyDrives);
        var signature = SignatureOf(drives);
        var now = nowUtc ?? DateTime.UtcNow;

        if (CanReuse(rememberedFolders, rememberedAtUtc, rememberedSignature, signature, now, force))
            return (Resolve(rememberedFolders!, filter, knownDirs), null);

        var clock = Stopwatch.StartNew();
        var folders = Sweep(drives, knownDirs, ct);
        Logger.Info($"Disk search: {folders.Count} game folder(s) on {drives.Count} drive(s) in {clock.Elapsed.TotalSeconds:0.0}s.");
        return (Resolve(folders, filter, knownDirs), new DiskSweep(folders, now, signature));
    }

    internal static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
