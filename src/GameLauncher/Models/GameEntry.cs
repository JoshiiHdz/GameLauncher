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
}
