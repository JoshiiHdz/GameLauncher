using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The Play time page: a game's time today, over the last 7 days and all time (see PlayHistory).</summary>
public partial class LibraryViewModel
{
    public const string PlayTimePageKey = "playtime";

    /// <summary>Today, the last 7 days and all time for a game, worked out now.</summary>
    internal PlayTimeViewModel BuildPlayTime(GameEntry game)
    {
        var sessions = _settings.Overrides.TryGetValue(game.Id, out var over) ? (IReadOnlyCollection<PlaySessionRecord>)over.Sessions : [];
        var now = SessionClock();
        return new PlayTimeViewModel(game, PlayHistory.Summarize(sessions, game.TotalPlaySeconds, now), PlayHistory.Describe(sessions, now));
    }

    [RelayCommand]
    private void ShowPlayTime(GameEntry? game)
    {
        if (game is null)
            return;

        OpenPage(PlayTimePageKey, "Play time", BuildPlayTime(game));
    }
}
