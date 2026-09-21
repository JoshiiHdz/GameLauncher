using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Serialization;

namespace GameLauncher.Models;

public sealed class AppSettings
{
    public List<WatchedFolder> WatchedFolders { get; set; } = new();
    public Dictionary<string, GameOverride> Overrides { get; set; } = new();

    /// <summary>See ArtworkConflict's own remarks - a dedup merge where both sides had their own
    /// explicit cover selection records the losing side here instead of silently discarding it.</summary>
    public List<ArtworkConflict> ArtworkConflicts { get; set; } = new();

    /// <summary>A dedup merge where two user identity decisions could not both stand - preserved, never dropped
    /// (design 8.2). Each element is read tolerantly; one we cannot understand is kept verbatim.</summary>
    [JsonConverter(typeof(TolerantIdentityConflictListConverter))]
    public List<IdentityConflict> IdentityConflicts { get; set; } = new();

    /// <summary>Raw identity subtrees the user explicitly replaced ("Clear identity" on a quarantined record).
    /// Written back verbatim and never deleted (design 5.2 item 5).</summary>
    public List<JsonElement> QuarantineArchive { get; set; } = new();

    /// <summary>Forward compatibility: top-level fields a later version adds survive this version's save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
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

    /// <summary>
    /// Optional IGDB Client ID (a Twitch Developer application - dev.twitch.tv/console/apps): the user's OWN
    /// application, used instead of the project's IGDB relay. It counts only together with the secret in
    /// IgdbCredentialStore - see IgdbAccess.Resolve: a lone id is ignored, never combined with anything.
    ///
    /// With no complete pair of their own, users reach IGDB through the project's relay (DefaultIgdbRelay), which keeps the shared
    /// Twitch credentials on its own server - no secret ships in the launcher, and nobody has to sign up for anything.
    ///
    /// The matching Client SECRET is deliberately NOT a field here - see IgdbCredentialStore's own
    /// remarks for why a plain settings field was a real, confirmed plaintext-storage gap (settings.json,
    /// its temp file, and its backup all inherit whatever this class serializes). It lives OS-protected,
    /// outside this file, in IgdbCredentialStore instead. With no complete user pair and no relay in the build,
    /// IGDB is skipped entirely and resolution falls straight to SteamGridDB.
    /// </summary>
    public string? IgdbClientId { get; set; }

    /// <summary>Check GitHub for a newer release on startup. Off just skips the check entirely -
    /// UpdateService.CheckForUpdateAsync is never called, not merely ignored.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Logging-only trial of GameWindowTracker (see its own remarks) as a PREFERRED exit signal
    /// alongside GameSessionWatcher's real, authoritative process-exit tracking - off by default, no UI
    /// toggle yet (hand-edit settings.json to enable for a validation session). NEVER changes what
    /// actually restores the window, cancels process monitoring, or clears running state, regardless of
    /// this setting - see GameSessionOrchestrator/GameWindowObserver.
    /// </summary>
    public bool EnableWindowExitDiagnostics { get; set; }
}
