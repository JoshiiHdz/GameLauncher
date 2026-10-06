using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Big-screen mode: the library as a full-screen, controller-friendly carousel. In the app it is called controller mode.</summary>
public partial class LibraryViewModel
{
    /// <summary>Controller mode is a skeleton for now: the screen, the carousel and the controller reading all exist and are tested, but it
    /// is being built up to a finished standard (it will be PlayStation-theme only, see docs/design/controller-mode-plan.md). Until this is
    /// switched on there is no way into it: no sidebar entry, no Settings card and no palette command. F11 and a direct command call
    /// only say it is coming.</summary>
    public bool ControllerModeAvailable { get; internal set; }

    private bool CanShowBigScreen() => ControllerModeAvailable;

    /// <summary>Test seam: receives the big-screen view model instead of opening the window; it may drive it and set what is chosen.</summary>
    internal Action<BigScreenViewModel>? BigScreenDialogForTest { get; set; }

    internal const int MaxJumpBackInShelf = 12;

    /// <summary>The shelves, top to bottom: what was played lately, favorites, then everything. Hidden games and disabled launchers stay
    /// out, the same as the main grid, and a shelf with nothing on it is left off.</summary>
    internal IReadOnlyList<BigShelf> BuildBigScreenShelves()
    {
        var visible = _allGames.Where(g => !g.Hidden && IsSourceEnabled(g.Source)).ToList();
        var shelves = new List<BigShelf>
        {
            new("Jump back in", visible.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).Take(MaxJumpBackInShelf).ToList()),
            new("Favorites", visible.Where(g => g.Favorite).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()),
            new("All games", visible.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList()),
        };

        return shelves.Where(s => s.Games.Count > 0).ToList();
    }

    [RelayCommand(CanExecute = nameof(CanShowBigScreen))]
    private void ShowBigScreen()
    {
        // Execute() on a command does not consult CanExecute, so F11 and the palette are stopped here as well.
        if (!ControllerModeAvailable)
        {
            StatusText = "Controller mode is coming soon.";
            return;
        }

        var shelves = BuildBigScreenShelves();
        if (shelves.Count == 0)
        {
            StatusText = "There are no games to show in big-screen mode yet.";
            return;
        }

        GameEntry? chosen;
        try
        {
            var screen = new BigScreenViewModel(shelves);
            screen.FavoriteRequested += game => ToggleFavorite(game);
            if (BigScreenDialogForTest is { } seam)
                seam(screen);
            else
                new BigScreenWindow(screen) { Owner = System.Windows.Application.Current?.MainWindow }.ShowDialog();

            chosen = screen.Chosen;
        }
        catch (Exception ex)
        {
            Logger.Error("Couldn't open big-screen mode.", ex);
            StatusText = $"Couldn't open big-screen mode: {ex.Message}";
            return;
        }

        // Launched only now that the full-screen window is gone, so the game (and the tray hand-off) never starts behind it.
        if (chosen is not null && _allGames.FirstOrDefault(g => g.Id == chosen.Id) is { } current)
            Launch(current);
    }
}
