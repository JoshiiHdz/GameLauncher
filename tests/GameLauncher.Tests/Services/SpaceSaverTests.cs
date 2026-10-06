using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

public sealed class SpaceSaverTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static GameEntry Game(string id, long? size, int? playedDaysAgo, int addedDaysAgo = 400) =>
        new()
        {
            Id = id, Name = id, Source = GameSource.Steam, ExecutablePath = @"C:\G\g.exe", InstallDir = @"C:\G",
            InstallSizeBytes = size, DateAdded = Now.AddDays(-addedDaysAgo), LastPlayedUtc = playedDaysAgo is { } d ? Now.AddDays(-d) : null,
        };

    [Fact]
    public void SuggestsTheBiggestGamesNotPlayedForSixtyDays_BiggestFirst()
    {
        var games = new[]
        {
            Game("small-old", 5_000_000_000, 90),
            Game("big-old", 80_000_000_000, 200),
            Game("big-recent", 100_000_000_000, 5),
            Game("mid-old", 30_000_000_000, 61),
        };

        var result = SpaceSaver.Suggest(games, Now);

        Assert.Equal(new[] { "big-old", "mid-old", "small-old" }, result.Select(r => r.Game.Id));
    }

    [Fact]
    public void AGameWithNoMeasuredSize_IsLeftOut_AndCountedAsStillMeasuring()
    {
        var games = new[] { Game("measured", 1_000, 100), Game("waiting", null, 100) };

        Assert.Equal(new[] { "measured" }, SpaceSaver.Suggest(games, Now).Select(r => r.Game.Id));
        Assert.Equal(1, SpaceSaver.Unmeasured(games));
    }

    [Fact]
    public void ANeverLaunchedGame_CountsFromTheDayItWasAdded_AndSaysSo()
    {
        var games = new[] { Game("old-unplayed", 10_000, null, addedDaysAgo: 200), Game("new-unplayed", 20_000, null, addedDaysAgo: 3) };

        var result = SpaceSaver.Suggest(games, Now);

        var only = Assert.Single(result);
        Assert.Equal("old-unplayed", only.Game.Id);
        Assert.Equal("Not played from Axis", only.LastPlayedText);
    }

    [Fact]
    public void TheRunningGame_IsNeverSuggested()
    {
        var games = new[] { Game("running", 10_000, 100), Game("idle", 5_000, 100) };

        Assert.Equal(new[] { "idle" }, SpaceSaver.Suggest(games, Now, runningGameId: "running").Select(r => r.Game.Id));
    }

    [Fact]
    public void TheListIsCapped()
    {
        var games = Enumerable.Range(0, 30).Select(i => Game("g" + i, 1_000 + i, 100)).ToList();

        Assert.Equal(10, SpaceSaver.Suggest(games, Now).Count);
    }

    [Fact]
    public void OptimizePage_ShowsTheSuggestions_AndUninstallHandsTheGameToTheUninstaller()
    {
        var games = new List<GameEntry> { Game("big-old", 50_000_000_000, 120) };
        GameEntry? uninstalled = null;
        var page = new OptimizeViewModel(new NoMemory(), [], [], _ => true, () => games, g => uninstalled = g);

        var row = Assert.Single(page.SpaceSaverRows);
        Assert.True(page.HasSpaceSaver);
        Assert.Contains("free", page.SpaceSaverSummary);

        row.UninstallCommand.Execute(null);
        Assert.Same(games[0], uninstalled);

        games.Clear();
        page.RefreshSpaceSaverCommand.Execute(null);
        Assert.Empty(page.SpaceSaverRows);
        Assert.StartsWith("Nothing found", page.SpaceSaverSummary);
    }

    private sealed class NoMemory : IMemoryOptimizer
    {
        public MemoryStatus GetStatus() => new(16L << 30, 4L << 30);

        public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders) => new(0, 0, 0, 0);
    }
}
