using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Finds Amazon Games (Prime Gaming) games. Amazon Games tracks installs in a private SQLite
/// database rather than a plain manifest, so this reads the standard installed-programs registry
/// data instead - see PublisherUninstallScanner. Unverified against a live Amazon Games install.
/// </summary>
public static class AmazonGamesScanner
{
    public static List<GameEntry> Scan() => PublisherUninstallScanner.Scan(
        GameSource.AmazonGames,
        sourceLabel: "Amazon Games",
        idPrefix: "amazon",
        publisherContains: new[] { "Amazon Games", "Amazon.com" },
        excludeNameContains: new[] { "Amazon Games" });
}
