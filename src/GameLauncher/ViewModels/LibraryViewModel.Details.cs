using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The game details page. (Notes were taken out of the launcher; what a person already wrote stays in their settings and backups.)</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: stands in for the details window. It may set PlayRequested, switch the priority and raise the requests.</summary>
    internal Action<GameDetailsViewModel>? GameDetailsDialogForTest { get; set; }

    /// <summary>Both sides of a merge are the same install, so both people's notes are kept: identical or one-sided notes stay as they
    /// are, differing ones are joined.</summary>
    internal static string? MergeNotes(string? winner, string? loser)
    {
        if (string.IsNullOrWhiteSpace(loser))
            return winner;
        if (string.IsNullOrWhiteSpace(winner))
            return loser;

        // Already in there (identical, or joined by an earlier merge): nothing to add - what keeps a repeated import from piling up copies.
        return winner.Contains(loser.Trim(), StringComparison.Ordinal) ? winner : winner.TrimEnd() + "\n\n" + loser.TrimStart();
    }

    /// <summary>Test seam: stands in for the quick-view panel (the floating card a click on an Axis game card opens). Same shape as
    /// <see cref="GameDetailsDialogForTest"/>: it may set PlayRequested or TweaksRequested, switch the priority and raise the requests.</summary>
    internal Action<GameDetailsViewModel>? GameQuickViewDialogForTest { get; set; }

    /// <summary>What the details page and the quick-view panel share: the buttons hand off to the library, and a game that is not measured yet is measured now
    /// (this one first), so its size appears as it is found rather than after every other game in the library.</summary>
    private void WireDetails(GameDetailsViewModel details, GameEntry game)
    {
        details.OpenInstallLocationRequested += () => OpenInstallLocation(game);
        details.EditCollectionsRequested += () => EditCollections(game);
        details.GameTweaksRequested += () => EditGameTweaks(game);
        details.PlayTimeRequested += () => ShowPlayTimeCommand.Execute(game);
        details.UninstallRequested += () => _ = UninstallGame(game);

        if (!game.HasInstallSize)
            EnsureInstallSizeMeasured(game);
    }

    /// <summary>Applies what was chosen while the page or panel was open: the priority switch is saved, then the game starts if Play was pressed.</summary>
    private void ApplyDetailsChoices(string gameId, GameDetailsViewModel details)
    {
        if (details.RaiseGamePriorityChanged)
            SetRaiseGamePriority(gameId, details.RaiseGamePriority);

        // The game is looked up again by id: a refresh can replace the entry while the page is open.
        if (details.PlayRequested && _allGames.FirstOrDefault(g => g.Id == gameId) is { } current)
            Launch(current);
    }

    /// <summary>The Axis way into a game, and its only details view: a click on its card opens a small floating card over the library - Play, the High priority switch,
    /// the facts, collections, play time and install folder, Game tweaks (which goes to Settings) and Uninstall. Axis has no full details page; the Xbox and PlayStation
    /// styles do (<see cref="ShowGameDetailsCommand"/>).</summary>
    [RelayCommand]
    private void ShowGameQuickView(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        GameDetailsViewModel? details = null;
        try
        {
            details = new GameDetailsViewModel(game, GetRaiseGamePriority(gameId));
            WireDetails(details, game);

            if (GameQuickViewDialogForTest is { } seam)
                seam(details);
            else
                AppShell.ShowModal(game.Name, new GameQuickViewDialog(details)); // waits here until the panel closes (Esc, a click outside, Play, its X)

            // Saved when the panel closes, however it closes.
            ApplyDetailsChoices(gameId, details);

            var wantsTweaks = details.TweaksRequested;
            details.Dispose();
            details = null;

            if (wantsTweaks)
                OpenSettingsAt(PerformanceSettingsCategory);
        }
        catch (Exception ex)
        {
            Logger.Error($"Couldn't open the quick view for '{game.Name}'.", ex);
            StatusText = $"Couldn't open {game.Name}: {ex.Message}";
            details?.Dispose();
        }
    }

    [RelayCommand]
    private void ShowGameDetails(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        GameDetailsViewModel? details = null;
        try
        {
            details = new GameDetailsViewModel(game, GetRaiseGamePriority(gameId));
            WireDetails(details, game);

            // Saved when the page closes, however it closes (Back, another view, Esc).
            void Finish() => ApplyDetailsChoices(gameId, details);

            if (GameDetailsDialogForTest is { } seam)
            {
                seam(details);
                Finish();
                details.Dispose();
            }
            else
            {
                // The page owns the view model from here: ClosePage runs Finish, then disposes it.
                OpenPage(DetailsPageKey, "Game details", details, Finish);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Couldn't open the details page for '{game.Name}'.", ex);
            StatusText = $"Couldn't open the details page for {game.Name}: {ex.Message}";
            details?.Dispose();
        }
    }
}
