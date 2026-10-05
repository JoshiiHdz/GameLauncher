namespace GameLauncher.Models;

/// <summary>Which of the icon rail's top-level views the library is showing. Drives both the grid's
/// contents and its heading; the hero and the "Recently played" strip only appear on All (see
/// LibraryViewModel.ApplyFilter), the same way the design preview behaves.</summary>
public enum LibraryView
{
    All,
    Favorites,
    Recent,
    /// <summary>Games with no tracked play time yet - for "what haven't I tried?". Tracked time only exists for play this app saw, so
    /// it is not proof a game was never played, and the UI says "not played yet", not "never played".</summary>
    NeverPlayed,
    /// <summary>Games installed through more than one launcher, side by side.</summary>
    Duplicates,
}
