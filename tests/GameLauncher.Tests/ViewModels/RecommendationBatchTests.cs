using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

public sealed class SessionStatsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
    private static readonly DateTime Noon = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc); // 12:00 local

    private static PlaySessionRecord Session(DateTime start, int minutes) => new() { StartUtc = start, Seconds = minutes * 60L };

    [Fact]
    public void NoSessions_MeansNoStats() => Assert.Null(PlayHistory.Describe([], Noon, Zone));

    [Fact]
    public void TheStats_AreTheLastSitting_TheCountThisWeek_TheLongestAndTheAverage()
    {
        var stats = PlayHistory.Describe(
        [
            Session(Noon.AddDays(-20), 120),   // old
            Session(Noon.AddDays(-3), 30),
            Session(Noon.AddDays(-1), 90),     // the last one: yesterday
        ], Noon, Zone)!;

        Assert.Equal(3, stats.Count);
        Assert.Equal(90 * 60, stats.LastSeconds);
        Assert.Equal("Yesterday", stats.LastWhen);
        Assert.Equal(2, stats.CountLast7Days);
        Assert.Equal(120 * 60, stats.LongestSeconds);
        Assert.Equal(80 * 60, stats.AverageSeconds);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(4, "4 days ago")]
    public void TheLastSitting_IsNamedByHowLongAgoItWas(int daysAgo, string expected) =>
        Assert.Equal(expected, PlayHistory.Describe([Session(Noon.AddDays(-daysAgo).AddMinutes(-30), 20)], Noon, Zone)!.LastWhen);

    [Fact]
    public void ThePanelWording_ComesFromTheStats()
    {
        var game = new GameEntry { Id = "a", Name = "A", Source = GameSource.Steam, ExecutablePath = @"C:\a.exe", InstallDir = @"C:\" };
        var stats = PlayHistory.Describe([Session(Noon.AddDays(-1), 45), Session(Noon.AddDays(-2), 90)], Noon, Zone);
        var page = new PlayTimeViewModel(game, PlayHistory.Summarize([], 0, Noon, Zone), stats);

        Assert.True(page.HasSessions);
        Assert.Equal("45 min · Yesterday", page.LastSessionText);
        Assert.Equal("2 sessions", page.SessionsThisWeekText);
        Assert.Equal("1.5 h", page.LongestSessionText);
        Assert.Equal("1.1 h", page.AverageSessionText); // 67.5 min, shown in hours once past an hour
        Assert.False(new PlayTimeViewModel(game, PlayHistory.Summarize([], 0, Noon, Zone)).HasSessions);
    }
}

[Collection(WpfStaCollection.Name)]
public sealed class RecommendationBatchTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary(params GameEntry[] games)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Batch-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult([.. games]);
        return vm;
    }

    private static GameEntry Game(string id, string name, GameSource source = GameSource.Steam, string? installDir = null) =>
        new() { Id = id, Name = name, Source = source, ExecutablePath = @"C:\G\" + name + ".exe", InstallDir = installDir ?? @"C:\G\" + name };

    // ---- settings can be found from the palette ----

    [Fact]
    public void EachSettingCanBeFoundByName_AndTakesYouToItsSection() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(Game("1", "Apex"));
        var items = vm.BuildPaletteItems();

        var focus = items.Single(i => i.Title.StartsWith("Settings: Focus play", StringComparison.Ordinal));
        Assert.Equal("Opens Settings > Performance", focus.Subtitle);
        focus.Execute();

        Assert.True(vm.IsSettingsPageOpen);
        Assert.Equal(LibraryViewModel.PerformanceSettingsCategory, vm.SettingsCategory);

        // Already open on another section: it reopens on the right one.
        items.Single(i => i.Title == "Settings: Start with Windows").Execute();
        Assert.Equal(LibraryViewModel.GeneralSettingsCategory, vm.SettingsCategory);
        Assert.True(vm.IsSettingsPageOpen);

        await Task.CompletedTask;
    });

    [Fact]
    public void TypingASettingsName_FindsIt_AndTheSettingsEntriesDoNotCrowdTheEmptyPalette() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(Game("1", "Apex"));
        var items = vm.BuildPaletteItems();

        Assert.Contains("Settings: Start with Windows", PaletteSearch.Search(items, "startup", 8).Select(i => i.Title));
        Assert.Contains("Settings: Change theme", PaletteSearch.Search(items, "theme", 8).Select(i => i.Title));
        Assert.DoesNotContain(PaletteSearch.Search(items, "", 20), i => i.Title.StartsWith("Settings: ", StringComparison.Ordinal));
        Assert.Equal(items.Count, items.Select(i => i.Title).Distinct().Count());

        await Task.CompletedTask;
    });

    // ---- the PlayStation home: status pill, facts line and hero panel ----

    [Fact]
    public void TheStatusLine_CombinesTheCountAndTheLastMessage_AndFollowsThem() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(Game("1", "Apex"));
        var shell = new ShellState(vm);

        vm.StatusText = "Ready";
        vm.FooterCountText = "1 of 1 game shown";

        Assert.Equal("1 of 1 game shown · Ready", shell.StatusLine);
        Assert.True(shell.HasStatusLine);

        vm.StatusText = "";
        Assert.Equal("1 of 1 game shown", shell.StatusLine);

        vm.FooterCountText = "";
        Assert.False(shell.HasStatusLine);

        await Task.CompletedTask;
    });

    [Fact]
    public void TheFactsLine_GivesTheDriveLetterAndTheSizeSeparately_SoTheDriveCanCarryAnIcon() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var apex = Game("1", "Apex", installDir: @"D:\Games\Apex");
            apex.InstallSizeBytes = 14L << 30;
            var vm = NewLibrary(apex);
            var shell = new ShellState(vm);

            shell.FocusRibbonItem(apex);

            Assert.Equal("D:", shell.RibbonDriveLetter);
            Assert.True(shell.HasRibbonDrive);
            Assert.Equal("14 GB", shell.RibbonSizeText);
            Assert.Equal("Not played yet", shell.RibbonPlayMeta);

            shell.FocusRibbonItem(null);                              // the Library tile has no drive or size
            Assert.False(shell.HasRibbonDrive);
            Assert.False(shell.HasRibbonSize);
            Assert.Contains("games", shell.RibbonPlayMeta);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void TheHeroPanelFollowsTheFocusedGame() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var apex = Game("1", "Apex");
            var vm = NewLibrary(apex, Game("2", "Baldur"));
            var shell = new ShellState(vm);

            shell.FocusRibbonItem(null);
            Assert.Null(shell.HeroPlayTime);                         // the Library tile has no game
            Assert.Equal("YOUR LIBRARY", shell.ActivityHeading);

            shell.FocusRibbonItem(apex);
            Assert.NotNull(shell.HeroPlayTime);
            Assert.Same(apex, shell.HeroPlayTime!.Game);
            Assert.Equal("ABOUT THIS GAME", shell.ActivityHeading);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void DetailsForAGame_SplitTheFactsLineIntoPlayTimeDriveAndSize()
    {
        var game = Game("1", "Apex", installDir: @"E:\Games\Apex");
        using var details = new GameDetailsViewModel(game, null);

        Assert.Equal("E:", details.DriveLetter);
        Assert.True(details.HasDriveLetter);
        Assert.Equal("Not played yet", details.PlayMetaText);
        Assert.Contains("Steam", details.MetaLine);                  // the one-line form (Axis) still names the launcher
    }
}
