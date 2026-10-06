using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

public sealed class PlayHistoryTests
{
    // A fixed zone, two hours ahead of UTC, so "today" does not depend on the machine running the tests.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    // 12:00 local on 2026-10-06 = 10:00 UTC.
    private static readonly DateTime Noon = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private static PlaySessionRecord Session(DateTime start, int minutes) => new() { StartUtc = start, Seconds = minutes * 60L };

    [Fact]
    public void Summarize_SplitsTodayLast7DaysAndAllTime()
    {
        var sessions = new[]
        {
            Session(Noon.AddMinutes(-90), 30),          // today, 30 min
            Session(Noon.AddDays(-1), 60),              // yesterday, 60 min
            Session(Noon.AddDays(-6), 45),              // six days back: still inside the 7 days
            Session(Noon.AddDays(-8), 120),             // eight days back: all time only
        };

        var summary = PlayHistory.Summarize(sessions, totalSeconds: 255 * 60, Noon, Zone);

        Assert.Equal(30 * 60, summary.TodaySeconds);
        Assert.Equal((30 + 60 + 45) * 60, summary.Last7DaysSeconds);
        Assert.Equal(255 * 60, summary.AllTimeSeconds);
        Assert.Equal(0, summary.EarlierSeconds);
    }

    [Fact]
    public void ASessionLeftRunningPastMidnight_IsSplitBetweenTheTwoDays()
    {
        // Local 23:00 to 01:00 is 21:00 to 23:00 UTC; "now" is 02:00 local the next day.
        var start = new DateTime(2026, 10, 5, 21, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var summary = PlayHistory.Summarize([Session(start, 120)], 7200, now, Zone);

        Assert.Equal(3600, summary.TodaySeconds); // 00:00-01:00 local
        Assert.Equal(7200, summary.Last7DaysSeconds);
    }

    [Fact]
    public void PlayFromBeforeHistoryWasKept_IsOnlyInAllTime_AndReportedAsEarlier()
    {
        var summary = PlayHistory.Summarize([Session(Noon.AddHours(-1), 20)], totalSeconds: 3 * 3600, Noon, Zone);

        Assert.Equal(20 * 60, summary.TodaySeconds);
        Assert.Equal(3 * 3600, summary.AllTimeSeconds);
        Assert.Equal(3 * 3600 - 20 * 60, summary.EarlierSeconds);
    }

    [Fact]
    public void Record_JoinsFollowOnTimeIntoOneSitting_ButNotAfterALongGap()
    {
        var over = new GameOverride();

        PlayHistory.Record(over, Noon, 600);                         // 09:50-10:00
        PlayHistory.Record(over, Noon.AddSeconds(660), 600);         // starts 60 s after the first ended: same sitting
        PlayHistory.Record(over, Noon.AddHours(5), 600);             // hours later: a new one

        Assert.Equal(2, over.Sessions.Count);
        Assert.Equal(1200, over.Sessions[0].Seconds);
        Assert.Equal(600, over.Sessions[1].Seconds);
    }

    [Fact]
    public void Record_IgnoresNothingToRecord_AndKeepsOnlyTheNewestRecords()
    {
        var over = new GameOverride();
        PlayHistory.Record(over, Noon, 0);
        PlayHistory.Record(over, Noon, -5);
        Assert.Empty(over.Sessions);

        for (var i = 0; i < PlayHistory.MaxRecords + 20; i++)
            PlayHistory.Record(over, Noon.AddDays(i), 600);

        Assert.Equal(PlayHistory.MaxRecords, over.Sessions.Count);
        Assert.Equal(Noon.AddDays(20).AddSeconds(-600), over.Sessions[0].StartUtc); // the oldest 20 went
    }

    [Fact]
    public void Merge_PutsTwoEntriesRecordsInTimeOrder_AndCountsOverlapsTwice()
    {
        var winner = new GameOverride();
        winner.Sessions.Add(Session(Noon.AddDays(-2), 30));
        winner.Sessions.Add(Session(Noon.AddHours(-1), 30));

        PlayHistory.Merge(winner, [Session(Noon.AddDays(-3), 10), Session(Noon.AddHours(-1), 15)]);

        Assert.Equal(new[] { Noon.AddDays(-3), Noon.AddDays(-2), Noon.AddHours(-1) }, winner.Sessions.Select(s => s.StartUtc));
        Assert.Equal(45 * 60, winner.Sessions[2].Seconds); // the overlapping 15 min were tracked too, so they count
    }

    [Fact]
    public void Records_SurviveSavingAndLoading_WithoutWritingTheComputedEnd()
    {
        var over = new GameOverride();
        over.Sessions.Add(Session(Noon, 42));

        var json = JsonSerializer.Serialize(over);
        var back = JsonSerializer.Deserialize<GameOverride>(json)!;

        Assert.DoesNotContain("EndUtc", json);
        Assert.Equal(Noon, back.Sessions.Single().StartUtc);
        Assert.Equal(42 * 60, back.Sessions.Single().Seconds);
    }

    [Fact]
    public void AnOlderSettingsFile_WithNoSessions_LoadsWithNone()
    {
        var back = JsonSerializer.Deserialize<GameOverride>("""{"Favorite":true,"TotalPlaySeconds":3600}""")!;

        Assert.Empty(back.Sessions);
        Assert.Equal(3600, back.TotalPlaySeconds);
    }
}

[Collection(WpfStaCollection.Name)]
public sealed class PlayTimePageTests(WpfStaFixture sta)
{
    private static (LibraryViewModel Vm, GameEntry Game, DateTime Clock) Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-PlayTime-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        var game = new GameEntry { Id = "steam-1", Name = "Elden Ring", Source = GameSource.Steam, ExecutablePath = @"C:\G\er.exe", InstallDir = @"C:\G" };
        vm.SimulateRefreshResult([game]);

        // Local noon today, so "today" holds the whole test whatever time it runs.
        var clock = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(DateTime.Now.Date.AddHours(12), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        return (vm, game, clock);
    }

    [Fact]
    public void AFinishedSession_AppearsInTodayThisWeekAndAllTime_AndThePageOpens() => sta.RunAsync(async () =>
    {
        var (vm, game, clock) = Create();
        var now = clock;
        vm.SessionClock = () => now;

        var session = vm.MarkGameRunning(game);
        now = clock.AddMinutes(25);
        vm.MarkGameNotRunning(game, session);

        var page = vm.BuildPlayTime(game);
        Assert.Equal(25 * 60, page.Summary.TodaySeconds);
        Assert.Equal(25 * 60, page.Summary.Last7DaysSeconds);
        Assert.Equal(25 * 60, page.Summary.AllTimeSeconds);
        Assert.Equal("25 min", page.TodayText);
        Assert.Equal("Today 25 min · Week 25 min", page.CardText);

        vm.ShowPlayTimeCommand.Execute(game);
        Assert.Equal(LibraryViewModel.PlayTimePageKey, vm.CurrentPageKey);
        Assert.IsType<PlayTimeViewModel>(vm.CurrentPage);

        await Task.CompletedTask;
    });

    [Fact]
    public void AGameWithOnlyOldTotals_ShowsNothingToday_AndSaysWhy() => sta.RunAsync(async () =>
    {
        var (vm, game, clock) = Create();
        vm.SessionClock = () => clock;
        game.TotalPlaySeconds = 5 * 3600;

        var page = vm.BuildPlayTime(game);

        Assert.Equal("None", page.TodayText);
        Assert.Equal("5 h", page.AllTimeText);
        Assert.Equal("All time 5 h", page.CardText);
        Assert.True(page.HasHistoryNote);

        await Task.CompletedTask;
    });

    [Fact]
    public void AGameNeverPlayed_SaysSo() => sta.RunAsync(async () =>
    {
        var (vm, game, clock) = Create();
        vm.SessionClock = () => clock;

        var page = vm.BuildPlayTime(game);

        Assert.Equal("Not played yet", page.CardText);
        Assert.False(page.HasHistoryNote);

        await Task.CompletedTask;
    });

    [Fact]
    public void PlayStationHero_PlayTimeCard_OpensThePage_AndPlayShowsRunningWhileTheGameRuns() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (vm, game, clock) = Create();
            vm.SessionClock = () => clock;
            var shell = new ShellState(vm);
            shell.FocusRibbonItem(game);

            var card = shell.ActivityCards.Single(c => c.Label.StartsWith("PLAY TIME"));
            Assert.True(card.Clickable);
            card.Run();
            Assert.Equal(LibraryViewModel.PlayTimePageKey, vm.CurrentPageKey);

            Assert.Equal("Play", shell.RibbonPlayLabel);
            game.IsRunning = true;
            Assert.Equal("Running", shell.RibbonPlayLabel);

            shell.Play(game); // already running: told so, not launched a second time
            Assert.Contains("already running", shell.FeedbackText);

            game.IsRunning = false;
            Assert.Equal("Play", shell.RibbonPlayLabel);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });
}
