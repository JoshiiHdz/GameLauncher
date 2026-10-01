using System.Collections.Specialized;
using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>ApplyFilter runs on every keystroke, sort change and refresh. Re-filtering to the SAME result must not rebuild the grid
/// (clearing and re-adding every card is what made each pass cost a full re-render), while a real change must still update it.</summary>
public class LibraryViewModelFilterEfficiencyTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
    private readonly LibraryViewModel _sut;

    public LibraryViewModelFilterEfficiencyTests() =>
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry Game(string id, string name, bool favorite = false) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id,
        Source = GameSource.Manual, Favorite = favorite, DateAdded = new DateTime(2024, 1, 1),
    };

    [Fact]
    public void ReFilteringToTheSameResult_DoesNotTouchTheCollections()
    {
        var a = Game("a", "Alpha", favorite: true);
        var b = Game("b", "Beta");
        _sut.SimulateRefreshResult([a, b]);
        _sut.SortOption = GameSortOption.LargestInstalled;   // runs a pass; with no sizes it orders by name, like the default
        var changes = 0;
        NotifyCollectionChangedEventHandler count = (_, _) => changes++;
        _sut.Games.CollectionChanged += count;
        _sut.FavoriteGames.CollectionChanged += count;
        _sut.RecentlyPlayedGames.CollectionChanged += count;

        _sut.SortOption = GameSortOption.MostPlayed;         // another pass, same visible order (no one has played anything)

        Assert.Equal(0, changes);
        Assert.Equal(new[] { a, b }, _sut.Games);
        Assert.Equal(new[] { a }, _sut.FavoriteGames);
    }

    [Fact]
    public void ARealChange_StillUpdatesTheCollections()
    {
        var a = Game("a", "Alpha");
        var b = Game("b", "Beta");
        _sut.SimulateRefreshResult([a, b]);

        _sut.SearchText = "Bet";
        Assert.Equal(new[] { b }, _sut.Games);

        _sut.SearchText = "";
        _sut.ToggleFavoriteCommand.Execute(a);
        Assert.Equal(new[] { a }, _sut.FavoriteGames);
        Assert.Equal(new[] { a, b }, _sut.Games);
    }

    [Fact]
    public void TheHeroAccentIsKeptWhenTheSameGameStaysFeatured()
    {
        var a = Game("a", "Alpha");
        _sut.SimulateRefreshResult([a]);
        var first = _sut.HeroAccentColor;

        _sut.SortOption = GameSortOption.NameDesc;

        Assert.Same(a, _sut.FeaturedGame);
        Assert.Equal(first, _sut.HeroAccentColor);
    }
}
