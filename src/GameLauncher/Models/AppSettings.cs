namespace GameLauncher.Models;

public sealed class AppSettings
{
    public List<WatchedFolder> WatchedFolders { get; set; } = new();
    public Dictionary<string, GameOverride> Overrides { get; set; } = new();
    public bool DetectSteam { get; set; } = true;
    public bool DetectEpic { get; set; } = true;
    public bool DetectGog { get; set; } = true;

    /// <summary>
    /// Optional SteamGridDB API key (steamgriddb.com/profile/preferences/api). When set, cover art
    /// for non-Steam games is fetched from SteamGridDB instead of falling back to the exe icon.
    /// </summary>
    public string? SteamGridDbApiKey { get; set; }
}
