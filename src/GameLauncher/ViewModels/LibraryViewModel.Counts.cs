using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.ViewModels;

/// <summary>How many games each library view holds (shown next to the view names in every theme). Counted over the library as it is
/// before search, drive or collection narrowing - the same set the views themselves start from - so a count never moves while typing.</summary>
public partial class LibraryViewModel
{
    [ObservableProperty]
    private int _allGamesCount;

    [ObservableProperty]
    private int _favoritesCount;

    [ObservableProperty]
    private int _recentCount;

    [ObservableProperty]
    private int _unplayedCount;

    [ObservableProperty]
    private int _duplicatesCount;

    private void UpdateViewCounts()
    {
        var all = 0;
        var favorites = 0;
        var recent = 0;
        var unplayed = 0;
        var duplicates = 0;
        foreach (var game in _allGames)
        {
            if (game.Hidden || !IsSourceEnabled(game.Source))
                continue;

            all++;
            if (game.Favorite) favorites++;
            if (game.HasPlayTime) recent++; else unplayed++;
            if (game.HasDuplicate) duplicates++;
        }

        AllGamesCount = all;
        FavoritesCount = favorites;
        RecentCount = recent;
        UnplayedCount = unplayed;
        DuplicatesCount = duplicates;
    }
}
