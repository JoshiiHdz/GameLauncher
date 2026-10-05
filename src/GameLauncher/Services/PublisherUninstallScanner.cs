using System.IO;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>
/// Finds games from launchers that don't expose a clean enumerable install manifest (Battle.net,
/// Rockstar Games Launcher, Amazon Games) by reading the same "installed programs" registry data
/// Windows' own "Apps &amp; features" list reads from, filtered by publisher name. This is a
/// standard, well-documented Windows mechanism every installer writes to - not a launcher-specific
/// format that has to be reverse-engineered - but it's also less precise than a launcher's own
/// manifest: InstallLocation isn't always populated, and it can't tell a real game apart from a
/// same-publisher tool/DLC entry beyond a name-based exclude list.
/// </summary>
public static class PublisherUninstallScanner
{
    private static readonly string[] UninstallKeyPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public static List<GameEntry> Scan(GameSource source, string sourceLabel, string idPrefix,
        string[] publisherContains, string[] excludeNameContains)
    {
        var games = new List<GameEntry>();

        // Per-user installs (Amazon Games installs to %LOCALAPPDATA% rather than Program Files, and
        // likely registers itself under HKCU rather than HKLM for that reason) wouldn't be found by
        // HKLM alone, so both hives are checked the same way.
        foreach (var keyPath in UninstallKeyPaths)
        {
            ScanKey(Registry.LocalMachine, keyPath, source, idPrefix, publisherContains, excludeNameContains, games);
            ScanKey(Registry.CurrentUser, keyPath, source, idPrefix, publisherContains, excludeNameContains, games);
        }

        if (games.Count == 0)
            Logger.Info($"{sourceLabel}: nothing found in the installed-programs registry.");

        return games;
    }

    private static void ScanKey(RegistryKey hive, string keyPath, GameSource source, string idPrefix,
        string[] publisherContains, string[] excludeNameContains, List<GameEntry> games)
    {
        try
        {
            using var root = hive.OpenSubKey(keyPath);
            if (root is null)
                return;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var entry = root.OpenSubKey(name);
                    var publisher = entry?.GetValue("Publisher") as string;
                    var displayName = entry?.GetValue("DisplayName") as string;

                    if (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(displayName))
                        continue;

                    if (!publisherContains.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (excludeNameContains.Any(x => displayName.Contains(x, StringComparison.OrdinalIgnoreCase)) || IsComponentName(displayName))
                        continue;

                    // From here on the publisher already matched and the name wasn't excluded - a
                    // strong signal this really is one of the user's games, so failing to resolve it
                    // the rest of the way is worth a trace (unlike the publisher-mismatch skip above,
                    // which fires for every unrelated installed program and would just be noise).
                    var installLocation = ResolveInstallDir(entry?.GetValue("InstallLocation") as string, entry?.GetValue("DisplayIcon") as string);
                    if (installLocation is null)
                    {
                        Logger.Warn($"  {source}: '{displayName}' matched but the registry gives no install folder for it (InstallLocation or a DisplayIcon inside the game).");
                        continue;
                    }

                    var exe = GameExeFinder.FindLargestExe(installLocation);
                    if (exe is null)
                    {
                        Logger.Warn($"  {source}: '{displayName}' matched but no launchable exe found under '{installLocation}'.");
                        continue;
                    }

                    var id = $"{idPrefix}-{InstallPaths.StableHash(installLocation)}";
                    if (games.Any(g => g.Id == id))
                        continue;

                    games.Add(new GameEntry
                    {
                        Id = id,
                        Name = displayName,
                        ExecutablePath = exe,
                        InstallDir = installLocation,
                        Source = source,
                    });
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Individual uninstall subkeys are essentially always readable (that's the whole
                    // point of "Programs and Features" using them) - a failure here is unusual enough
                    // to be worth a trace rather than a routine, expected skip.
                    Logger.Warn($"  {source}: couldn't read uninstall entry '{name}'.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't read uninstall registry key '{(hive == Registry.CurrentUser ? "HKCU" : "HKLM")}\\{keyPath}'.", ex);
        }
    }

    /// <summary>Words that mark a publisher's entry as a piece of software rather than a game: "Rockstar Games SDK", "EA Redistributable",
    /// "Ubisoft Runtime", ... Matched as whole words, so a game with "SDK" inside a longer word is not caught.</summary>
    private static readonly HashSet<string> ComponentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SDK", "Redistributable", "Redistributables", "Redist", "Runtime", "Runtimes", "Prerequisite", "Prerequisites", "Prereq",
        "Driver", "Drivers", "Updater", "Uninstaller", "Bootstrapper", "Crashpad",
    };

    internal static bool IsComponentName(string displayName)
    {
        var words = displayName.Split([' ', '-', '_', '.', ',', '(', ')', '[', ']', '/', '\\', ':', '+'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(ComponentWords.Contains) || displayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The game's folder: its InstallLocation, or - when an installer left that blank, which many do - the folder of the exe its
    /// DisplayIcon points at (installers set the icon to the game's own exe).</summary>
    internal static string? ResolveInstallDir(string? installLocation, string? displayIcon,
        Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null)
    {
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;

        if (!string.IsNullOrWhiteSpace(installLocation) && directoryExists(installLocation))
            return installLocation;

        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;

        // "C:\Games\Foooo.exe,0" - the index after the comma is the icon number, not part of the path.
        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 1 && int.TryParse(path[(comma + 1)..], out _))
            path = path[..comma].Trim().Trim('"');

        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !fileExists(path))
            return null;

        var folder = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(folder) && directoryExists(folder) ? folder : null;
    }

}
