using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>One game the Space saver suggests removing: the biggest ones that have not been played for a while.</summary>
public sealed record SpaceSaverSuggestion(GameEntry Game, long Bytes, string LastPlayedText);

/// <summary>Works out which installed games would free the most space for the least loss: big, and not played for a long time. Only suggests;
/// nothing is removed from here.</summary>
public static class SpaceSaver
{
    /// <summary>How long a game must have sat unplayed before it is suggested.</summary>
    public const int UnplayedDays = 60;

    /// <summary>Games with a measured install size that have not been played in <paramref name="days"/> days, biggest first. A game that was never
    /// launched from Axis counts from the day it was added (Axis cannot know about play that happened elsewhere, so the text says "from Axis").</summary>
    public static IReadOnlyList<SpaceSaverSuggestion> Suggest(IEnumerable<GameEntry> games, DateTime nowUtc, int days = UnplayedDays, int take = 10, string? runningGameId = null)
    {
        var cutoff = nowUtc.AddDays(-days);
        return games
            .Where(g => g.Id != runningGameId && g.InstallSizeBytes is > 0)
            .Where(g => (g.LastPlayedUtc ?? (g.DateAdded == default ? nowUtc : g.DateAdded)) < cutoff)
            .OrderByDescending(g => g.InstallSizeBytes)
            .Take(take)
            .Select(g => new SpaceSaverSuggestion(g, g.InstallSizeBytes!.Value, Describe(g, nowUtc)))
            .ToList();
    }

    /// <summary>How many games have no size yet (the background measuring has not reached them), so the list can say it is still filling in.</summary>
    public static int Unmeasured(IEnumerable<GameEntry> games) => games.Count(g => g.InstallSizeBytes is null);

    private static string Describe(GameEntry game, DateTime nowUtc)
    {
        if (game.LastPlayedUtc is not { } last)
            return "Not played from Axis";

        var days = (int)(nowUtc - last).TotalDays;
        return days switch
        {
            < 365 => $"Last played {days / 30} months ago",
            _ => "Last played over a year ago",
        };
    }
}
