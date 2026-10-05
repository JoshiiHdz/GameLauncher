using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Collections: the user's own groups. Membership is saved on each game's override, the sidebar list is derived from the
/// games, and a collection with no games left simply disappears.</summary>
public class LibraryViewModelCollectionsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
    private readonly LibraryViewModel _sut;

    public LibraryViewModelCollectionsTests() =>
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, string name) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\Games\" + id + @"\game.exe", InstallDir = @"C:\Games\" + id, Source = GameSource.Manual,
    };

    private (GameEntry A, GameEntry B, GameEntry C) ThreeGames()
    {
        var (a, b, c) = (MakeGame("a", "Apex"), MakeGame("b", "Borderlands"), MakeGame("c", "Celeste"));
        _sut.SimulateRefreshResult([a, b, c]);
        return (a, b, c);
    }

    // ---- names -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("  Co-op  ", "Co-op")]
    [InlineData("Late   night\tgames", "Late night games")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ANameIsTrimmedAndCollapsed_AndBlankIsRejected(string? raw, string? expected) =>
        Assert.Equal(expected, LibraryViewModel.NormalizeCollectionName(raw));

    [Fact]
    public void ALongNameIsCapped() =>
        Assert.Equal(LibraryViewModel.MaxCollectionNameLength, LibraryViewModel.NormalizeCollectionName(new string('x', 200))!.Length);

    [Fact]
    public void MergingMembership_IsACaseInsensitiveUnion_KeepingTheFirstSpelling() =>
        Assert.Equal(["Co-op", "Backlog"], LibraryViewModel.MergeCollectionNames(["Co-op"], ["co-OP", "Backlog"]));

    // ---- membership + sidebar list -----------------------------------------------------------------

    [Fact]
    public void AddingAGameToACollection_ListsItWithACount_AndSavesItOnTheOverride()
    {
        var (a, b, _) = ThreeGames();

        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SetGameCollections(b, ["Co-op", "Backlog"]);

        Assert.Equal(["Backlog", "Co-op"], _sut.Collections.Select(c => c.Name));
        Assert.Equal(2, _sut.Collections.Single(c => c.Name == "Co-op").Count);
        Assert.True(_sut.HasCollections);
        Assert.Equal(["Co-op"], a.Collections);

        var reloaded = new SettingsService(_dataDir).Load();
        Assert.Equal(["Co-op", "Backlog"], reloaded.Overrides["b"].Collections);
    }

    [Fact]
    public void ATypedNameThatDiffersOnlyInCase_JoinsTheExistingCollection()
    {
        var (a, b, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);

        _sut.SetGameCollections(b, ["CO-OP"]);

        Assert.Equal(["Co-op"], _sut.Collections.Select(c => c.Name));
        Assert.Equal(["Co-op"], b.Collections);
    }

    [Fact]
    public void HiddenGames_AreNotCounted()
    {
        var (a, b, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SetGameCollections(b, ["Co-op"]);

        _sut.ToggleHiddenCommand.Execute(b);

        Assert.Equal(1, _sut.Collections.Single().Count);
    }

    [Fact]
    public void ARemovedCollection_DisappearsWhenItsLastGameLeaves()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);

        _sut.SetGameCollections(a, []);

        Assert.Empty(_sut.Collections);
        Assert.False(_sut.HasCollections);
    }

    // ---- filtering ---------------------------------------------------------------------------------

    [Fact]
    public void SelectingACollection_ShowsOnlyItsGames_AndRetitlesTheGrid()
    {
        var (a, b, c) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SetGameCollections(c, ["Co-op"]);

        _sut.SelectCollectionCommand.Execute("Co-op");

        Assert.Equal(["Apex", "Celeste"], _sut.Games.Select(g => g.Name));
        Assert.Equal("Co-op", _sut.LibraryHeaderText);
        Assert.Null(_sut.FeaturedGame); // the hero only belongs on the unfiltered home view
        Assert.True(_sut.Collections.Single().IsSelected);
        Assert.DoesNotContain(_sut.Games, g => g == b);
    }

    [Fact]
    public void ClickingTheSelectedCollectionAgain_ClearsTheFilter()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SelectCollectionCommand.Execute("Co-op");

        _sut.SelectCollectionCommand.Execute("Co-op");

        Assert.Null(_sut.SelectedCollection);
        Assert.Equal(3, _sut.Games.Count);
        Assert.Equal("All games", _sut.LibraryHeaderText);
    }

    [Fact]
    public void ClickingARailView_ClearsTheCollectionFilter()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SelectCollectionCommand.Execute("Co-op");

        _sut.SelectViewCommand.Execute("all");

        Assert.Null(_sut.SelectedCollection);
        Assert.Equal(3, _sut.Games.Count);
    }

    [Fact]
    public void ASelectedCollection_ThatLosesItsLastGame_ClearsItselfInsteadOfShowingAnEmptyGrid()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SelectCollectionCommand.Execute("Co-op");

        _sut.SetGameCollections(a, []);

        Assert.Null(_sut.SelectedCollection);
        Assert.Equal(3, _sut.Games.Count);
    }

    [Fact]
    public void ACollectionCombinesWithTheSearchBox()
    {
        var (a, _, c) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.SetGameCollections(c, ["Co-op"]);
        _sut.SelectCollectionCommand.Execute("Co-op");

        _sut.SearchText = "cel";

        Assert.Equal(["Celeste"], _sut.Games.Select(g => g.Name));
    }

    // ---- deleting ----------------------------------------------------------------------------------

    [Fact]
    public void DeletingACollection_RemovesItFromEveryGame_ButKeepsTheGames()
    {
        var (a, b, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op", "Backlog"]);
        _sut.SetGameCollections(b, ["Co-op"]);
        _sut.SelectCollectionCommand.Execute("Co-op");

        _sut.DeleteCollectionCommand.Execute("Co-op");

        Assert.Equal(["Backlog"], _sut.Collections.Select(c => c.Name));
        Assert.Empty(b.Collections);
        Assert.Equal(["Backlog"], a.Collections);
        Assert.Null(_sut.SelectedCollection);
        Assert.Equal(3, _sut.Games.Count);

        var reloaded = new SettingsService(_dataDir).Load();
        Assert.Equal(["Backlog"], reloaded.Overrides["a"].Collections);
        Assert.Empty(reloaded.Overrides["b"].Collections);
    }

    // ---- the dialog --------------------------------------------------------------------------------

    [Fact]
    public void EditCollections_AppliesWhatTheDialogReturns()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        CollectionsViewModel? shown = null;
        _sut.CollectionsDialogForTest = d => { shown = d; return ["Co-op", "Finished"]; };

        _sut.EditCollectionsCommand.Execute(a);

        Assert.Equal("Apex", shown!.GameName);
        Assert.Equal(["Co-op", "Finished"], a.Collections);
    }

    [Fact]
    public void EditCollections_Cancelled_ChangesNothing()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Co-op"]);
        _sut.CollectionsDialogForTest = _ => null;

        _sut.EditCollectionsCommand.Execute(a);

        Assert.Equal(["Co-op"], a.Collections);
    }

    [Fact]
    public void TheDialog_TicksWhatTheGameIsAlreadyIn_AndOffersEveryExistingCollection()
    {
        var vm = new CollectionsViewModel("Apex", ["Backlog", "Co-op"], ["co-op"]);

        Assert.Equal(["Backlog", "Co-op"], vm.Choices.Select(c => c.Name));
        Assert.Equal([false, true], vm.Choices.Select(c => c.IsMember));
        Assert.Equal(["Co-op"], vm.ChosenNames());
    }

    [Fact]
    public void TheDialog_AddingANewName_TicksItAndClearsTheBox()
    {
        var vm = new CollectionsViewModel("Apex", ["Co-op"], []);
        vm.NewName = "  Weekend  ";

        Assert.True(vm.AddNewCommand.CanExecute(null));
        vm.AddNewCommand.Execute(null);

        Assert.Equal("", vm.NewName);
        Assert.Equal(["Co-op", "Weekend"], vm.Choices.Select(c => c.Name));
        Assert.Equal(["Weekend"], vm.ChosenNames());
    }

    [Fact]
    public void TheDialog_ATypedNameNotYetAdded_IsStillSavedWithTheTicks()
    {
        var vm = new CollectionsViewModel("Apex", ["Co-op"], ["Co-op"]);
        vm.NewName = "Weekend";

        Assert.Equal(["Co-op", "Weekend"], vm.ChosenNames());
    }

    [Fact]
    public void TheDialog_AlwaysOffersThePresets_ThenTheUsersOwn()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameCollections(a, ["Weekend", "co-op"]);

        var offered = _sut.DialogCollectionNames();

        Assert.Equal(LibraryViewModel.PresetCollections, offered.Take(LibraryViewModel.PresetCollections.Length));
        Assert.Equal("Weekend", offered[LibraryViewModel.PresetCollections.Length]);
        Assert.Equal(LibraryViewModel.PresetCollections.Length + 1, offered.Count);
        Assert.Equal(["Weekend", "Co-op"], a.Collections); // the preset's own spelling wins
    }

    [Fact]
    public void APresetThatNoGameIsIn_IsNotListedInTheSidebar()
    {
        ThreeGames();

        Assert.Empty(_sut.Collections);
    }

    [Fact]
    public void TheDialog_ABlankName_CannotBeAdded()
    {
        var vm = new CollectionsViewModel("Apex", [], []);
        vm.NewName = "   ";

        Assert.False(vm.AddNewCommand.CanExecute(null));
        Assert.Empty(vm.ChosenNames());
    }
}
