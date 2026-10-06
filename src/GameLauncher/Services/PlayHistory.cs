using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>What the Play time page shows for one game. "Today" and "last 7 days" come from the session records, which only exist from the version
/// that started keeping them; <see cref="EarlierSeconds"/> is the part of the all-time total that no record covers.</summary>
public sealed record PlayTimeSummary(long TodaySeconds, long Last7DaysSeconds, long AllTimeSeconds, long EarlierSeconds);

/// <summary>What the PlayStation home's Sessions panel shows for one game: the last sitting, how many this week, the longest and the average.</summary>
public sealed record SessionStats(int Count, long LastSeconds, string LastWhen, int CountLast7Days, long LongestSeconds, long AverageSeconds);

public static class PlayHistory
{
    /// <summary>The most records kept per game; the oldest go first. Joined records keep this far above what a person plays in years.</summary>
    internal const int MaxRecords = 1500;

    /// <summary>A record that starts within this long of the previous one ending is the same sitting (the passive tracker saves a minute at a time).</summary>
    internal static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(120);

    /// <summary>Adds time that ended at <paramref name="endUtc"/> and lasted <paramref name="seconds"/> to a game's records.</summary>
    public static void Record(GameOverride over, DateTime endUtc, long seconds)
    {
        if (seconds <= 0)
            return;

        var start = endUtc.AddSeconds(-seconds);
        if (over.Sessions.Count > 0)
        {
            var last = over.Sessions[^1];
            var gap = start - last.EndUtc;
            if (gap >= TimeSpan.FromSeconds(-5) && gap <= JoinGap)
            {
                last.Seconds += seconds;
                return;
            }
        }

        over.Sessions.Add(new PlaySessionRecord { StartUtc = start, Seconds = seconds });
        if (over.Sessions.Count > MaxRecords)
            over.Sessions.RemoveRange(0, over.Sessions.Count - MaxRecords);
    }

    /// <summary>Adds another game's records to this one's (two entries found to be the same install), in time order, joining records that now touch.</summary>
    public static void Merge(GameOverride into, IEnumerable<PlaySessionRecord> others)
    {
        var all = into.Sessions.Concat(others).OrderBy(s => s.StartUtc).ToList();
        into.Sessions.Clear();
        foreach (var record in all)
        {
            var last = into.Sessions.Count > 0 ? into.Sessions[^1] : null;
            if (last is not null && record.StartUtc - last.EndUtc <= TimeSpan.Zero)
                last.Seconds += record.Seconds; // overlapping records: both were tracked, so both count
            else
                into.Sessions.Add(new PlaySessionRecord { StartUtc = record.StartUtc, Seconds = record.Seconds });
        }

        if (into.Sessions.Count > MaxRecords)
            into.Sessions.RemoveRange(0, into.Sessions.Count - MaxRecords);
    }

    /// <summary>Adds the records a backup holds that this game does not have yet (the same start and length is the same record), so importing the same backup
    /// twice changes nothing.</summary>
    public static void Union(GameOverride into, IEnumerable<PlaySessionRecord> incoming)
    {
        var known = into.Sessions.Select(s => (s.StartUtc, s.Seconds)).ToHashSet();
        var added = false;
        foreach (var record in incoming)
        {
            if (record is null || record.Seconds <= 0 || !known.Add((record.StartUtc, record.Seconds)))
                continue;

            into.Sessions.Add(new PlaySessionRecord { StartUtc = record.StartUtc, Seconds = record.Seconds });
            added = true;
        }

        if (!added)
            return;

        into.Sessions.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        if (into.Sessions.Count > MaxRecords)
            into.Sessions.RemoveRange(0, into.Sessions.Count - MaxRecords);
    }

    /// <summary>How much of the records fall between two moments; a record that straddles an edge counts for the part inside it (a game left running
    /// past midnight is split between the two days).</summary>
    public static long SecondsBetween(IEnumerable<PlaySessionRecord> sessions, DateTime fromUtc, DateTime toUtc)
    {
        double total = 0;
        foreach (var session in sessions)
        {
            var start = session.StartUtc > fromUtc ? session.StartUtc : fromUtc;
            var end = session.EndUtc < toUtc ? session.EndUtc : toUtc;
            if (end > start)
                total += (end - start).TotalSeconds;
        }

        return (long)Math.Round(total);
    }

    /// <summary>Today (since local midnight), the last 7 days (today and the six before it) and all time, for one game.</summary>
    public static PlayTimeSummary Summarize(IReadOnlyCollection<PlaySessionRecord> sessions, long totalSeconds, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var todayStart = StartOfDayUtc(localNow.Date, zone);
        var weekStart = StartOfDayUtc(localNow.Date.AddDays(-6), zone);

        var recorded = sessions.Sum(s => s.Seconds);
        return new PlayTimeSummary(
            TodaySeconds: SecondsBetween(sessions, todayStart, nowUtc),
            Last7DaysSeconds: SecondsBetween(sessions, weekStart, nowUtc),
            AllTimeSeconds: totalSeconds,
            EarlierSeconds: Math.Max(0, totalSeconds - recorded));
    }

    private static DateTime StartOfDayUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        // Midnight can be skipped by a clock change in some zones; the first valid moment of that day is then the day's start.
        while (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddMinutes(30);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    /// <summary>The figures for the Sessions panel, or null when no session was recorded. A "session" is one sitting: records that follow on within a couple of
    /// minutes were already joined by <see cref="Record"/>.</summary>
    public static SessionStats? Describe(IReadOnlyCollection<PlaySessionRecord> sessions, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        if (sessions.Count == 0)
            return null;

        zone ??= TimeZoneInfo.Local;
        var last = sessions.MaxBy(s => s.StartUtc)!;
        var weekStart = StartOfDayUtc(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date.AddDays(-6), zone);
        var total = sessions.Sum(s => s.Seconds);
        return new SessionStats(
            Count: sessions.Count,
            LastSeconds: last.Seconds,
            LastWhen: When(last.EndUtc, nowUtc, zone),
            CountLast7Days: sessions.Count(s => s.EndUtc >= weekStart),
            LongestSeconds: sessions.Max(s => s.Seconds),
            AverageSeconds: total / sessions.Count);
    }

    /// <summary>"Today", "Yesterday", "3 days ago", or the date for anything older than a week.</summary>
    internal static string When(DateTime utc, DateTime nowUtc, TimeZoneInfo zone)
    {
        var then = TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date;
        var days = (int)(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date - then).TotalDays;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            _ => then.ToString("d MMM yyyy"),
        };
    }

    /// <summary>"45 min", "3.2 h", or <paramref name="none"/> when there is nothing.</summary>
    public static string Show(long seconds, string none) => seconds <= 0 ? none : PlayTimeFormat.Duration(seconds);
}
