using GameLauncher.Converters;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The Play time page for one game: today, the last 7 days and all time, beside the game's card. Read-only: it is a snapshot taken when the page opens.</summary>
public sealed class PlayTimeViewModel(GameEntry game, PlayTimeSummary summary, SessionStats? sessions = null)
{
    public GameEntry Game { get; } = game;

    public PlayTimeSummary Summary { get; } = summary;

    public SessionStats? Sessions { get; } = sessions;

    public bool HasSessions => Sessions is not null;

    /// <summary>"45 min · Yesterday".</summary>
    public string LastSessionText => Sessions is { } s ? $"{PlayHistory.Show(s.LastSeconds, "Under a minute")} · {s.LastWhen}" : "";

    public string SessionsThisWeekText => Sessions is { } s ? (s.CountLast7Days == 1 ? "1 session" : s.CountLast7Days == 0 ? "None" : $"{s.CountLast7Days} sessions") : "";

    public string LongestSessionText => Sessions is { } s ? PlayHistory.Show(s.LongestSeconds, "Under a minute") : "";

    public string AverageSessionText => Sessions is { } s ? PlayHistory.Show(s.AverageSeconds, "Under a minute") : "";

    public string SourceText => LauncherText.Name(Game.Source);

    public string LastPlayedText => Game.HasPlayTime ? $"Last played {Game.LastPlayedDisplay.ToLowerInvariant()}" : "Not played yet";

    public string TodayText => PlayHistory.Show(Summary.TodaySeconds, "None");

    public string Last7DaysText => PlayHistory.Show(Summary.Last7DaysSeconds, "None");

    public string AllTimeText => PlayHistory.Show(Summary.AllTimeSeconds, "None");

    /// <summary>Said when part of the all-time total was played before daily history was kept, so Today and Last 7 days cannot account for it.</summary>
    public string HistoryNote => Summary.EarlierSeconds >= 60
        ? $"{PlayHistory.Show(Summary.EarlierSeconds, "")} of the all-time total was played before Axis Game Launcher started keeping daily history, so it only shows in All time."
        : "";

    public bool HasHistoryNote => HistoryNote.Length > 0;

    /// <summary>The one line the hero's Play time card shows: "Today 45 min · Week 3.2 h", or the all-time figure when nothing was played this week.</summary>
    public string CardText
    {
        get
        {
            if (Summary.AllTimeSeconds <= 0)
                return "Not played yet";

            if (Summary.TodaySeconds <= 0 && Summary.Last7DaysSeconds <= 0)
                return $"All time {PlayHistory.Show(Summary.AllTimeSeconds, "")}";

            return $"Today {PlayHistory.Show(Summary.TodaySeconds, "0 min")} · Week {PlayHistory.Show(Summary.Last7DaysSeconds, "0 min")}";
        }
    }
}
