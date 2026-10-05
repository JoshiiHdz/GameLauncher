using System.IO;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>
/// Finds EA app / Origin games, without assuming where they were installed. EA lets you pick any drive and any folder, and most
/// people never choose - so no single method is trusted on its own. Every one of these runs and the results are merged:
///  1. the registry keys EA and Origin write for each game (several names, 32 and 64 bit);
///  2. Origin's own manifests (ProgramData\Origin\LocalContent), which record each game's install path;
///  3. the usual library folders - on EVERY ready drive, at the drive root and under Program Files, Games and so on;
///  4. a shallow sweep of every ready drive for the marker EA puts in every game folder (__Installer\installerdata.xml), which
///     finds a game wherever it is, within a few folders of the drive root;
///  5. the installed-programs list, for anything published by Electronic Arts.
/// </summary>
public static class EaScanner
{
    // EA/Origin sometimes installs a game under a folder/registry-key name that's a genuine
    // ABBREVIATION of its real catalog title, not just a formatting variant IsConfidentMatch's own
    // collapsed comparison could already recover on its own (compare "AWayOut" vs "A Way Out" - same
    // words, no spaces - which that comparison already handles). Apex Legends is the confirmed real
    // case: it installs to a folder simply named "Apex", and searching SteamGridDB for the literal
    // string "Apex" finds an unrelated, differently-catalogued game of that exact name before ever
    // finding "Apex Legends" - see SteamGridDbCoverArtProvider.SelectGameId's own remarks.
    //
    // Deliberately a short, explicit, hand-verified list - exactly the same discipline
    // SteamGridDbCoverArtProvider.RomanNumeralSequelNumbers/AmbiguousUmbrellaProductNamesCollapsed
    // already apply to other known, confirmed naming quirks - NEVER a heuristic that could silently
    // relabel some other, unrelated game's identity. GameEntry.Name (the raw detected name, used for
    // display/dedup/session-tracking) is never changed by this; only GameEntry.CatalogName, consulted
    // solely for automatic cover-art matching, is affected. Case-insensitive: registry/folder casing
    // isn't guaranteed consistent across installs.
    private static readonly Dictionary<string, string> KnownAbbreviatedCatalogNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Apex"] = "Apex Legends",
        };

    /// <summary>Resolves `rawDetectedName` to its verified real catalog title, or null when no explicit,
    /// hand-verified mapping exists for it - see KnownAbbreviatedCatalogNames's own remarks. Pulled out
    /// as its own pure function specifically so it's unit-testable directly, without needing to fake the
    /// Windows registry this class otherwise reads from.</summary>
    internal static string? ResolveCatalogName(string rawDetectedName) =>
        KnownAbbreviatedCatalogNames.TryGetValue(rawDetectedName, out var catalogName) ? catalogName : null;

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();
        var drives = InstallPaths.ReadyDrives().ToList();

        ScanRegistry(games);
        ScanOriginGamesRegistry(games);
        ScanOriginManifests(games, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Origin", "LocalContent"));
        ScanKnownFolders(games, drives);
        SweepForInstallerMarker(games, drives);
        ScanInstalledPrograms(games);

        if (games.Count == 0)
            Logger.Info("EA: nothing found in the registry, Origin's manifests, the usual folders, a sweep of the drives or the installed programs.");
        else
            Logger.Info($"EA: found {games.Count} game(s).");

        return games;
    }

    /// <summary>The folder-based methods only (3 and 4 above), against the given drive roots - what tests exercise without a registry.</summary>
    internal static List<GameEntry> ScanFolders(IReadOnlyList<string> driveRoots)
    {
        var games = new List<GameEntry>();
        ScanKnownFolders(games, driveRoots);
        SweepForInstallerMarker(games, driveRoots);
        return games;
    }

    private static void ScanRegistry(List<GameEntry> games)
    {
        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Electronic Arts",
                     @"SOFTWARE\Electronic Arts",
                     // Many EA titles register here instead - "EA Games" is the name EA's installers have long used.
                     @"SOFTWARE\WOW6432Node\EA Games",
                     @"SOFTWARE\EA Games",
                     @"SOFTWARE\WOW6432Node\EA SPORTS",
                     @"SOFTWARE\EA SPORTS",
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
                    {
                        Logger.Warn($"  EA: '{name}' registry entry has no valid install directory, skipping it.");
                        continue;
                    }

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
                {
                    Logger.Warn($"  EA: Origin Games entry '{contentId}' has no valid install directory, skipping it.");
                    continue;
                }

                var name = Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar));
                AddIfPlayable(games, installDir, name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read EA registry key '{keyPath}'.", ex);
        }
    }

    /// <summary>Library folders where EA's installers put games, relative to a drive root. The first group holds nothing but games; the
    /// "Electronic Arts" group is shared with EA's own apps, so a folder there only counts with EA's installer marker in it.</summary>
    private static readonly string[] GameLibraryRoots =
    {
        "EA Games", "Origin Games", "EASports",
        @"Program Files\EA Games", @"Program Files (x86)\EA Games",
        @"Program Files\Origin Games", @"Program Files (x86)\Origin Games",
        @"Games\EA Games", @"Games\Origin Games", @"Games\EA", @"Games\EASports",
    };

    private static readonly string[] SharedLibraryRoots =
    {
        @"Program Files\Electronic Arts", @"Program Files (x86)\Electronic Arts", "Electronic Arts", @"Games\Electronic Arts",
    };

    private static readonly HashSet<string> NotGameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "EA Desktop", "EA Core", "EA app", "Origin", "EALaunchHelper", "Cleanup", "Redist", "_CommonRedist",
    };

    private static void ScanKnownFolders(List<GameEntry> games, IReadOnlyList<string> driveRoots)
    {
        foreach (var drive in driveRoots)
        {
            foreach (var relative in GameLibraryRoots.Select(r => (Path: r, NeedsMarker: false))
                         .Concat(SharedLibraryRoots.Select(r => (Path: r, NeedsMarker: true))))
            {
                var libraryDir = Path.Combine(drive, relative.Path);
                if (!Directory.Exists(libraryDir))
                    continue;

                try
                {
                    foreach (var gameDir in Directory.EnumerateDirectories(libraryDir))
                    {
                        var name = Path.GetFileName(gameDir);
                        if (NotGameFolders.Contains(name) || (relative.NeedsMarker && !HasInstallerMarker(gameDir)))
                            continue;

                        AddIfPlayable(games, gameDir, name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Failed reading EA games from '{libraryDir}'.", ex);
                }
            }
        }
    }

    /// <summary>EA puts <c>__Installer\installerdata.xml</c> in every game folder it installs.</summary>
    private static bool HasInstallerMarker(string gameDir) => File.Exists(Path.Combine(gameDir, "__Installer", "installerdata.xml"));

    /// <summary>Folders the sweep never goes into: system and other launchers' areas, where an EA game is not and walking is slow.</summary>
    private static readonly HashSet<string> SweepSkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "windowsapps", "$recycle.bin", "system volume information", "recovery", "programdata", "users", "perflogs",
        "appdata", "msocache", "common files", "windows kits", "dotnet", "steamapps", "steam", "epic games", "gog galaxy",
        "node_modules", ".git", "microsoft", "nvidia corporation", "intel", "amd", "drivers", "temp", "tmp", "$winreagent",
    };

    private const int SweepDepth = 3;

    /// <summary>
    /// Finds games wherever they are by their marker, a few folders below each drive root (C:\Program Files\EA Games\Game is three
    /// down). Only directory names are listed - never file contents - and a folder that holds the marker is claimed whole, so the
    /// sweep stops there.
    /// </summary>
    private static void SweepForInstallerMarker(List<GameEntry> games, IReadOnlyList<string> driveRoots)
    {
        foreach (var drive in driveRoots)
            Sweep(drive, depth: 0, games);
    }

    private static void Sweep(string folder, int depth, List<GameEntry> games)
    {
        // EA's own apps carry the same installer data; they are not games.
        if (depth > 0 && !NotGameFolders.Contains(Path.GetFileName(folder)) && HasInstallerMarker(folder))
        {
            AddIfPlayable(games, folder, Path.GetFileName(folder));
            return;
        }

        if (depth >= SweepDepth)
            return;

        IEnumerable<string> subFolders;
        try
        {
            subFolders = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sub in subFolders)
        {
            if (!SweepSkipFolders.Contains(Path.GetFileName(sub)))
                Sweep(sub, depth + 1, games);
        }
    }

    /// <summary>Origin keeps one manifest per installed game; the install path is in it, percent-encoded.</summary>
    private static void ScanOriginManifests(List<GameEntry> games, string localContentDir)
    {
        try
        {
            if (!Directory.Exists(localContentDir))
                return;

            foreach (var manifest in Directory.EnumerateFiles(localContentDir, "*.mfst", SearchOption.AllDirectories))
            {
                try
                {
                    var installDir = ParseManifestInstallPath(File.ReadAllText(manifest));
                    if (installDir is not null && Directory.Exists(installDir))
                        AddIfPlayable(games, installDir, Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"  EA: couldn't read the Origin manifest '{manifest}'.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't read Origin's LocalContent folder.", ex);
        }
    }

    /// <summary>The install path out of an Origin manifest ("...&amp;dipinstallpath=C%3A%5CProgram%20Files%5CGame&amp;...").</summary>
    internal static string? ParseManifestInstallPath(string manifestText)
    {
        const string key = "dipinstallpath=";
        var start = manifestText.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += key.Length;
        var end = manifestText.IndexOfAny(['&', '\r', '\n'], start);
        var value = end < 0 ? manifestText[start..] : manifestText[start..end];
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var path = Uri.UnescapeDataString(value.Trim());
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    /// <summary>Anything Windows lists as published by Electronic Arts, apart from EA's own apps.</summary>
    private static void ScanInstalledPrograms(List<GameEntry> games)
    {
        foreach (var found in PublisherUninstallScanner.Scan(GameSource.Ea, "EA", "ea",
                     publisherContains: ["Electronic Arts"],
                     excludeNameContains: ["EA app", "EA Desktop", "EA Core", "Origin", "EA Help", "Redistributable", "Visual C++"]))
        {
            if (!games.Any(g => g.Id == found.Id))
                games.Add(found);
        }
    }

    private static void AddIfPlayable(List<GameEntry> games, string installDir, string name)
    {
        installDir = NormalizeDir(installDir);
        var id = $"ea-{InstallPaths.StableHash(installDir)}";
        if (games.Any(g => g.Id == id))
            return;

        // "showcase" is EA Sports-specific (a demo/kiosk mode bundled alongside the real game, e.g.
        // "FC26_Showcase.exe" next to "FC26.exe") - not a broadly generalizable enough term for the
        // shared default list, but a real, confirmed false-pick here otherwise. See
        // GameExeFinder.EaFriendsPassExcludePattern's own remarks for why "_friend" is scoped here too,
        // rather than added to the shared default patterns every scanner uses.
        var exe = GameExeFinder.FindLargestExe(installDir,
            extraExcludePatterns: new[] { "showcase", GameExeFinder.EaFriendsPassExcludePattern });
        if (exe is null)
        {
            Logger.Warn($"  EA: '{name}' found at '{installDir}' but no launchable exe was found in it.");
            return;
        }

        games.Add(new GameEntry
        {
            Id = id,
            Name = name,
            CatalogName = ResolveCatalogName(name),
            ExecutablePath = exe,
            InstallDir = installDir,
            Source = GameSource.Ea,
        });
    }


    /// <summary>The same install reached by two routes (registry and a folder scan) must give one entry: slashes and a trailing
    /// separator are not part of its identity.</summary>
    private static string NormalizeDir(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

}
