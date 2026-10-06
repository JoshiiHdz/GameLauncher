using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The game details page and the user's notes on a game. Notes live on the game's override, next to its collections.</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: stands in for the details window. It may edit Notes, set PlayRequested and raise the requests.</summary>
    internal Action<GameDetailsViewModel>? GameDetailsDialogForTest { get; set; }

    internal string GetGameNotes(string gameId) => _settings.Overrides.GetValueOrDefault(gameId)?.Notes ?? "";

    /// <summary>Saves a game's notes: capped, and stored as nothing at all when only whitespace is left.</summary>
    internal void SetGameNotes(string gameId, string? notes)
    {
        var cleaned = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleaned is { Length: > GameDetailsViewModel.MaxNotesLength })
            cleaned = cleaned[..GameDetailsViewModel.MaxNotesLength];

        if (!_settings.Overrides.TryGetValue(gameId, out var over))
        {
            if (cleaned is null)
                return;

            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        over.Notes = cleaned;
        _settingsService.Save(_settings);
        Logger.Info($"Notes: {(cleaned is null ? "cleared" : $"saved {cleaned.Length} characters")} for game '{gameId}'.");
    }

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

    [RelayCommand]
    private void ShowGameDetails(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        GameDetailsViewModel? details = null;
        try
        {
            details = new GameDetailsViewModel(game, GetGameNotes(gameId), GetRaiseGamePriority(gameId));
            details.OpenInstallLocationRequested += () => OpenInstallLocation(game);
            details.EditCollectionsRequested += () => EditCollections(game);
            details.PlayTimeRequested += () => ShowPlayTimeCommand.Execute(game);
            details.UninstallRequested += () => _ = UninstallGame(game);

            // Not measured yet: start it now, so the size appears on the page as it is found.
            if (!game.HasInstallSize)
                _ = EstimateInstallSizesAsync();

            // Saved when the page closes, however it closes (Back, another view, Esc), so typed notes are never lost.
            void Finish()
            {
                if (details.NotesChanged)
                    SetGameNotes(gameId, details.Notes);

                if (details.RaiseGamePriorityChanged)
                    SetRaiseGamePriority(gameId, details.RaiseGamePriority);

                // The game is looked up again by id: a refresh can replace the entry while the page is open.
                if (details.PlayRequested && _allGames.FirstOrDefault(g => g.Id == gameId) is { } current)
                    Launch(current);
            }

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
