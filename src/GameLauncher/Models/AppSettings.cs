namespace GameLauncher.Models;

public sealed class AppSettings
{
    public List<WatchedFolder> WatchedFolders { get; set; } = new();
    public Dictionary<string, GameOverride> Overrides { get; set; } = new();
    public bool DetectSteam { get; set; } = true;
    public bool DetectEpic { get; set; } = true;
    public bool DetectGog { get; set; } = true;
    public bool DetectXbox { get; set; } = true;
    public bool DetectEa { get; set; } = true;
    public bool DetectUbisoft { get; set; } = true;
    public bool DetectBattleNet { get; set; } = true;
    public bool DetectRockstar { get; set; } = true;
    public bool DetectAmazonGames { get; set; } = true;

    /// <summary>Frosted acrylic window backdrop. Costs some GPU while the window is visible
    /// (nothing while minimized), so it's switchable for anyone who wants it truly idle.</summary>
    public bool VibrantBackground { get; set; } = true;

    /// <summary>Hide to the system tray while a game is running, and come back when it exits.
    /// When off, the launcher just minimizes to the taskbar as before.</summary>
    public bool MinimizeToTrayWhileGaming { get; set; } = true;

    /// <summary>Whether the left sidebar (source toggles) is expanded or collapsed to a slim rail.</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>
    /// Optional SteamGridDB API key (steamgriddb.com/profile/preferences/api). When set, cover art
    /// for non-Steam games is fetched from SteamGridDB instead of falling back to the exe icon.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }

    /// <summary>Check GitHub for a newer release on startup. Off just skips the check entirely -
    /// UpdateService.CheckForUpdateAsync is never called, not merely ignored.</summary>
    public bool CheckForUpdates { get; set; } = true;
}
