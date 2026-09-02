using System.IO;
using System.Security.Cryptography;
using System.Text;
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

                    if (excludeNameContains.Any(x => displayName.Contains(x, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // From here on the publisher already matched and the name wasn't excluded - a
                    // strong signal this really is one of the user's games, so failing to resolve it
                    // the rest of the way is worth a trace (unlike the publisher-mismatch skip above,
                    // which fires for every unrelated installed program and would just be noise).
                    var installLocation = entry?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
                    {
                        Logger.Warn($"  {source}: '{displayName}' matched but has no valid InstallLocation in the registry.");
                        continue;
                    }

                    var exe = GameExeFinder.FindLargestExe(installLocation);
                    if (exe is null)
                    {
                        Logger.Warn($"  {source}: '{displayName}' matched but no launchable exe found under '{installLocation}'.");
                        continue;
                    }

                    var id = $"{idPrefix}-{StableId(installLocation)}";
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

    private static string StableId(string path)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
}
