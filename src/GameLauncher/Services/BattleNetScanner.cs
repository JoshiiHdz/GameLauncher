using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Finds Battle.net games. Blizzard doesn't expose an enumerable install manifest the way Steam or
/// Ubisoft does (its product.db is a private binary format), so this reads the standard
/// installed-programs registry data instead - see PublisherUninstallScanner. Unverified against a
/// live Battle.net install.
/// </summary>
public static class BattleNetScanner
{
    public static List<GameEntry> Scan() => PublisherUninstallScanner.Scan(
        GameSource.BattleNet,
        sourceLabel: "Battle.net",
        idPrefix: "battlenet",
        publisherContains: new[] { "Blizzard Entertainment" },
        excludeNameContains: new[] { "Battle.net" });
}
