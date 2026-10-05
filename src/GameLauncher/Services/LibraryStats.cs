using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>One line in a stats list: a game, a short detail ("38.2 h"), and a 0-1 bar length relative to the top entry.</summary>
public sealed record GameStatLine(string Name, string Detail, double Fraction);

/// <summary>What the Stats page shows. Every figure is play time THIS app tracked - it cannot know about play before a game was
/// added here - and hidden games are left out, the same as the rest of the library views.</summary>
public sealed record LibraryStatsSnapshot(
    int GameCount,
    long TotalPlaySeconds,
    int PlayedGameCount,
    int NotPlayedCount,
    IReadOnlyList<GameStatLine> MostPlayed,
    IReadOnlyList<GameStatLine> PlayedThisWeek)
{
    public string TotalPlayDisplay => TotalPlaySeconds <= 0 ? "None yet" : PlayTimeFormat.Duration(TotalPlaySeconds);

    public bool HasMostPlayed => MostPlayed.Count > 0;

    public bool HasNoPlay => !HasMostPlayed;

    public bool HasPlayedThisWeek => PlayedThisWeek.Count > 0;

    public bool HasNotPlayed => NotPlayedCount > 0;

    public string NotPlayedText => NotPlayedCount == 1 ? "1 game has no tracked play time." : $"{NotPlayedCount} games have no tracked play time.";

    public string PlayedThisWeekHeading => $"Played this week ({PlayedThisWeek.Count})";
}

public static class LibraryStats
{
    internal const int MostPlayedCount = 5;
    internal const int ThisWeekDays = 7;

    public static LibraryStatsSnapshot Compute(IEnumerable<GameEntry> games, DateTime nowUtc)
    {
        var visible = games.Where(g => !g.Hidden).ToList();
        var played = visible.Where(g => g.HasPlayTime).ToList();

        var top = played
            .OrderByDescending(g => g.TotalPlaySeconds)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MostPlayedCount)
            .ToList();
        var longest = top.Count > 0 ? top[0].TotalPlaySeconds : 0;

        var weekStart = nowUtc.AddDays(-ThisWeekDays);
        var thisWeek = played
            .Where(g => g.LastPlayedUtc is { } last && last >= weekStart)
            .OrderByDescending(g => g.LastPlayedUtc)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GameStatLine(g.Name, g.LastPlayedDisplay, 0))
            .ToList();

        return new LibraryStatsSnapshot(
            GameCount: visible.Count,
            TotalPlaySeconds: played.Sum(g => g.TotalPlaySeconds),
            PlayedGameCount: played.Count,
            NotPlayedCount: visible.Count - played.Count,
            MostPlayed: top.Select(g => new GameStatLine(g.Name, PlayTimeFormat.Duration(g.TotalPlaySeconds),
                (double)g.TotalPlaySeconds / longest)).ToList(),
            PlayedThisWeek: thisWeek);
    }
}
