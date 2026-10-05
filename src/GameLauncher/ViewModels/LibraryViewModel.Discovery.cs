using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Ways to decide what to play: the Stats page, "Pick a game for me", and the "Not played yet" view (see ApplyFilter).</summary>
public partial class LibraryViewModel
{
    [ObservableProperty]
    private bool _isNeverPlayedViewSelected;

    /// <summary>Test seam: receives the stats instead of opening the page, and returns true to jump to the "Not played yet" view.</summary>
    internal Func<LibraryStatsSnapshot, bool>? StatsDialogForTest { get; set; }

    /// <summary>Test seam: stands in for the picker dialog, and returns true when the user chose Play.</summary>
    internal Func<PickGameViewModel, bool>? PickGameDialogForTest { get; set; }

    /// <summary>Random index source for "Pick a game for me" - replaced in tests so the choice is deterministic.</summary>
    internal Func<int, int> RandomIndex { get; set; } = Random.Shared.Next;

    [RelayCommand]
    private void ShowStats()
    {
        try
        {
            var stats = LibraryStats.Compute(_allGames, SessionClock());
            if (StatsDialogForTest is { } seam)
            {
                if (seam(stats))
                    SelectView("unplayed");
            }
            else
            {
                // "Show them" on the page is the "unplayed" view command itself, so there is nothing to read back on close.
                OpenPage(StatsPageKey, "Stats", stats);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the stats page.", ex);
            StatusText = $"Couldn't open the stats page: {ex.Message}";
        }
    }

    /// <summary>Suggests a game from what is on screen right now, so a search, drive, collection or the "Not played yet" view narrows the
    /// choice the way the user would expect.</summary>
    [RelayCommand]
    private void PickGame()
    {
        var pool = Games.ToList();
        if (pool.Count == 0)
        {
            StatusText = "There are no games in this view to pick from.";
            return;
        }

        try
        {
            var dialog = new PickGameViewModel(pool, RandomIndex);
            if (PickGameDialogForTest is { } seam)
            {
                if (seam(dialog))
                    Launch(dialog.Game);
            }
            else
            {
                // Launched once the page has closed, so the game (and the tray hand-off) never starts behind it.
                OpenPage(PickPageKey, "Pick a game", dialog, () =>
                {
                    if (dialog.PlayRequested)
                        Launch(dialog.Game);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open the game picker.", ex);
            StatusText = $"Couldn't open the game picker: {ex.Message}";
        }
    }
}
