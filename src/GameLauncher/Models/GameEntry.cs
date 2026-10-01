using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

public sealed partial class GameEntry : ObservableObject
{
    public required string Id { get; init; }

    private string _name = "";
    private string? _detectedTitle;

    /// <summary>The DISPLAY name: the scanner's detected title until a custom name is overlaid on it. Mutable, and
    /// the only name a user ever sees or edits.</summary>
    public required string Name
    {
        get => _name;
        // The FIRST assignment (every scanner creates the entry with the title it detected) is captured as
        // DetectedTitle and never changes again, so overlaying a custom name later cannot reach a provider.
        set { _name = value; _detectedTitle ??= value; }
    }

    /// <summary>What the scanner actually detected, immutable after construction and never a user's custom name
    /// (design I7). Every provider search is built from this - never from Name.</summary>
    public string DetectedTitle => _detectedTitle ?? _name;
    public required string ExecutablePath { get; init; }
    public required string InstallDir { get; init; }
    public required GameSource Source { get; init; }

    public string? LaunchUri { get; init; }

    /// <summary>An id this SAME install was once found under, if a scanner's detection method changed in
    /// a way that changes Id going forward but shouldn't silently disconnect a game already in someone's
    /// library. Null for every ordinary entry. GameScannerService falls back to looking up an override by
    /// LegacyId when none exists under Id yet - a narrow, explicit reconciliation step, not a general
    /// migration framework: a scanner sets this only for the specific transition it's making, once.</summary>
    public string? LegacyId { get; init; }

    /// <summary>Verified real catalog title, for AUTOMATIC cover-art matching only - null unless a
    /// scanner has explicit, hand-verified evidence that Name (the raw detected name - still used for
    /// display, dedup, and session tracking, and never overwritten by this) is an abbreviation of a
    /// different real title. Never derived from a heuristic or guess; see EaScanner.ResolveCatalogName
    /// for the one place this is populated today (EA/Origin installs some titles - Apex Legends among
    /// them - under a folder literally named the abbreviation, not the real title). CoverArtService/
    /// SteamGridDbCoverArtProvider search and compare against this instead of Name when it's set - see
    /// SteamGridDbCoverArtProvider.IsConfidentMatch's own remarks for why loosening the MATCH comparison
    /// instead (rather than fixing the identity feeding it) would reopen exactly the false-positive
    /// matches that comparison exists to prevent.</summary>
    public string? CatalogName { get; init; }

    // Observable (not plain auto-properties) so Change Cover/Reset can update a single card's displayed
    // image in place - without change notification here, writing these directly would silently do
    // nothing visible until an unrelated full library refresh happened to replace this GameEntry.
    [ObservableProperty]
    private BitmapImage? _icon;

    /// <summary>True when Icon is real portrait box art (fills the card edge-to-edge); false when
    /// it's a fallback exe icon (small, centered, on a plate) - the UI renders these differently.</summary>
    [ObservableProperty]
    private bool _isCoverArt;

    /// <summary>Icon of the launcher this game came from (Steam, Epic, ...), extracted from that
    /// launcher's own executable. Null when the source is unknown or its launcher isn't installed.</summary>
    public BitmapImage? PlatformIcon { get; set; }

    public bool Hidden { get; set; }
    public DateTime DateAdded { get; set; }

    [ObservableProperty]
    private bool _favorite;

    /// <summary>True when automatic identification could not settle which game this is (no confident match, an ambiguous name,
    /// a contradiction, or unreadable identity data) - drives the small "?" badge that invites the user to identify it. A
    /// pinned or existing cover is unaffected: this is about IDENTITY, not about what image is shown.</summary>
    [ObservableProperty]
    private bool _needsIdentity;

    [ObservableProperty]
    private string _identityBadgeText = "";

    /// <summary>True from the moment this game is launched until GameSessionWatcher confirms it has
    /// exited (or gives up ever finding it running). Drives the "Running" badge on its card.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Null until LibraryViewModel's background size estimation walks this game's folder.
    /// Used for sorting library-wide, displayed only in the drive-filtered view.
    /// Reset to null on every rescan (a fresh GameEntry
    /// is always a new instance) since files on disk may have changed since the last estimate.</summary>
    [ObservableProperty]
    private long? _installSizeBytes;

    [ObservableProperty]
    private bool _hasInstallSize;

    /// <summary>The measured install size as plain text ("2 GB"), empty until measured.</summary>
    [ObservableProperty]
    private string _installSizeDisplay = "";

    partial void OnInstallSizeBytesChanged(long? value)
    {
        HasInstallSize = value is not null;
        InstallSizeDisplay = FormatSize(value);
    }

    private static string FormatSize(long? value) => value switch
    {
        null => "",
        >= 1L << 40 => $"{value.Value / (double)(1L << 40):0.#} TB",
        >= 1L << 30 => $"{value.Value / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{value.Value / (double)(1L << 20):0.#} MB",
        >= 1024 => $"{value.Value / 1024d:0.#} KB",
        _ => $"{value.Value} B",
    };

    /// <summary>Play time this app has actually tracked, in seconds - see GameOverride.TotalPlaySeconds
    /// for why this is explicitly not a lifetime total.</summary>
    [ObservableProperty]
    private long _totalPlaySeconds;

    [ObservableProperty]
    private DateTime? _lastPlayedUtc;

    /// <summary>"38.2 h tracked" / "45 min tracked" - empty until there's something to show.</summary>
    [ObservableProperty]
    private string _playTimeDisplay = "";

    /// <summary>"Yesterday", "2 days ago", ... - empty until this game has been played once.</summary>
    [ObservableProperty]
    private string _lastPlayedDisplay = "";

    /// <summary>True once a session has been tracked, so the UI can hide the whole play-time line
    /// rather than showing a meaningless "0 h" for a game that was never launched from here.</summary>
    [ObservableProperty]
    private bool _hasPlayTime;

    partial void OnTotalPlaySecondsChanged(long value) => RefreshPlayTimeDisplay();

    partial void OnLastPlayedUtcChanged(DateTime? value) => RefreshPlayTimeDisplay();

    private void RefreshPlayTimeDisplay()
    {
        HasPlayTime = TotalPlaySeconds > 0;
        PlayTimeDisplay = FormatTracked(TotalPlaySeconds);
        LastPlayedDisplay = LastPlayedUtc is { } last ? FormatRelative(last) : "";
    }

    private static string FormatTracked(long seconds)
    {
        if (seconds <= 0)
            return "";

        // Minutes below an hour: "38.2 h tracked" is meaningless detail for a 12-minute session, and
        // "0.2 h" reads like a bug.
        if (seconds < 3600)
            return $"{Math.Max(1, seconds / 60)} min tracked";

        return $"{seconds / 3600d:0.#} h tracked";
    }

    private static string FormatRelative(DateTime utc)
    {
        var days = (int)(DateTime.UtcNow.Date - utc.ToLocalTime().Date).TotalDays;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            < 14 => "Last week",
            < 31 => $"{days / 7} weeks ago",
            < 62 => "A month ago",
            < 365 => $"{days / 30} months ago",
            _ => "Over a year ago",
        };
    }
}
