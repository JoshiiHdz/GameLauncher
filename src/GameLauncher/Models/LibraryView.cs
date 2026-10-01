namespace GameLauncher.Models;

/// <summary>Which of the icon rail's top-level views the library is showing. Drives both the grid's
/// contents and its heading; the hero and the "Recently played" strip only appear on All (see
/// LibraryViewModel.ApplyFilter), the same way the design preview behaves.</summary>
public enum LibraryView
{
    All,
    Favorites,
    Recent,
}
