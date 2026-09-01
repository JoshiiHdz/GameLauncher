using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Finds Rockstar Games Launcher games. Rockstar's per-title registry keys aren't uniform enough to
/// enumerate reliably, so this reads the standard installed-programs registry data instead - see
/// PublisherUninstallScanner. Unverified against a live Rockstar Games Launcher install.
/// </summary>
public static class RockstarScanner
{
    public static List<GameEntry> Scan() => PublisherUninstallScanner.Scan(
        GameSource.Rockstar,
        sourceLabel: "Rockstar Games Launcher",
        idPrefix: "rockstar",
        publisherContains: new[] { "Rockstar Games" },
        excludeNameContains: new[] { "Rockstar Games Launcher", "Social Club" });
}
