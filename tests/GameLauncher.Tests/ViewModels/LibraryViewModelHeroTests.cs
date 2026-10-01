using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for the "Jump back in" hero: LibraryViewModel.FeaturedGame is whichever VISIBLE
/// game has the newest DateAdded - real, persisted data, deliberately not a stand-in for "most played"
/// (this app has no way to know a game's playtime before it was added here - see FeaturedGame's own
/// remarks). It hides under a search or drive filter rather than pinning a game the rest of the grid no
/// longer shows.
/// </summary>
public class LibraryViewModelHeroTests : IDisposable
{
    private readonly string _dataDir;
    private readonly LibraryViewModel _sut;

    public LibraryViewModelHeroTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, DateTime dateAdded, string name = "Test Game") => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = @"C:\Games\" + id + @"\game.exe",
        InstallDir = @"C:\Games\" + id,
        Source = GameSource.Manual,
        DateAdded = dateAdded,
    };

    [Fact]
    public void FeaturedGame_IsWhicheverGameWasAddedMostRecently()
    {
        var older = MakeGame("old", new DateTime(2024, 1, 1));
        var newer = MakeGame("new", new DateTime(2024, 6, 1));
        _sut.SimulateRefreshResult([older, newer]);
        _sut.ToggleFavoriteCommand.Execute(older); // any ApplyFilter-triggering call recomputes it

        Assert.Same(newer, _sut.FeaturedGame);
        Assert.True(_sut.HasFeaturedGame);
    }

    [Fact]
    public void FeaturedGame_IsNull_WhenTheLibraryIsEmpty()
    {
        _sut.SimulateRefreshResult([]);
        _sut.SortOption = GameSortOption.NameDesc; // forces an ApplyFilter pass; unrelated to the hero itself

        Assert.Null(_sut.FeaturedGame);
        Assert.False(_sut.HasFeaturedGame);
    }

    [Fact]
    public void FeaturedGame_HidesWhileSearching_EvenWhenTheSearchStillMatchesTheGameItself()
    {
        // The search text here deliberately still matches the game's own name - isolating the hero's
        // OWN "hide while searching" rule from the unrelated, already-covered case where a search
        // simply empties the grid (see FeaturedGame_IsNull_WhenTheLibraryIsEmpty). If this test used a
        // search that matched nothing, it would pass even without the rule it's meant to prove, since
        // an empty `ordered` list produces a null FeaturedGame on its own either way.
        var game = MakeGame("g", DateTime.UtcNow, name: "Distinctive Game Name");
        _sut.SimulateRefreshResult([game]);
        _sut.ToggleFavoriteCommand.Execute(game);
        Assert.NotNull(_sut.FeaturedGame);

        _sut.SearchText = "Distinctive";

        Assert.Null(_sut.FeaturedGame);
        Assert.False(_sut.HasFeaturedGame);
    }

    [Fact]
    public void FeaturedGame_HidesWhileADriveFilterIsActive()
    {
        var game = MakeGame("g", DateTime.UtcNow);
        _sut.SimulateRefreshResult([game]);
        _sut.ToggleFavoriteCommand.Execute(game);
        Assert.NotNull(_sut.FeaturedGame);

        _sut.SelectDriveCommand.Execute("C:");

        Assert.Null(_sut.FeaturedGame);
        Assert.False(_sut.HasFeaturedGame);
    }

    [Fact]
    public void FeaturedGame_ReappearsOnceTheFilterClears()
    {
        var game = MakeGame("g", DateTime.UtcNow);
        _sut.SimulateRefreshResult([game]);
        _sut.SelectDriveCommand.Execute("C:");
        Assert.Null(_sut.FeaturedGame);

        _sut.SelectDriveCommand.Execute("C:"); // toggles the same drive again - clears the filter

        Assert.Same(game, _sut.FeaturedGame);
        Assert.True(_sut.HasFeaturedGame);
    }

    [Fact]
    public void FeaturedGame_ExcludesHiddenGames()
    {
        var hidden = MakeGame("hidden", new DateTime(2024, 6, 1));
        var visible = MakeGame("visible", new DateTime(2024, 1, 1));
        _sut.SimulateRefreshResult([hidden, visible]);
        _sut.ToggleHiddenCommand.Execute(hidden); // newer, but hidden - must never be featured

        Assert.Same(visible, _sut.FeaturedGame);
    }

    [Fact]
    public void HeroAccentColor_FallsBackToTheDefault_WhenThereIsNoFeaturedGame()
    {
        _sut.SimulateRefreshResult([]);
        _sut.SortOption = GameSortOption.NameDesc; // forces an ApplyFilter pass

        // Never a transparent/default(Color) value - always a real, usable accent colour.
        Assert.True(_sut.HeroAccentColor.A > 0);
    }
}
