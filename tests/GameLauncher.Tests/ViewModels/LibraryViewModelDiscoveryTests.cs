using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Deciding what to play (the "Not played yet" view, "Pick a game for me", the Stats page), the drive storage page and the game
/// details page with its notes - everything driven through the library view model, with the dialogs replaced by test seams.</summary>
public class LibraryViewModelDiscoveryTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
    private readonly LibraryViewModel _sut;

    public LibraryViewModelDiscoveryTests()
    {
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
        // Never measure real folders from a test: a size is whatever the test says it is.
        _sut.InstallSizeEstimatorForTest = (_, _) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, string name, long playSeconds = 0, string drive = "C:") => new()
    {
        Id = id, Name = name, ExecutablePath = drive + @"\Games\" + id + @"\game.exe", InstallDir = drive + @"\Games\" + id,
        Source = GameSource.Manual,
        TotalPlaySeconds = playSeconds,
        LastPlayedUtc = playSeconds > 0 ? DateTime.UtcNow.AddHours(-1) : null,
    };

    /// <summary>Stands in for a scan, then runs the production filter (SimulateRefreshResult alone does not repaint the grid).</summary>
    private void Load(List<GameEntry> games)
    {
        _sut.SimulateRefreshResult(games);
        _sut.SelectViewCommand.Execute("all");
    }

    private (GameEntry A, GameEntry B, GameEntry C) ThreeGames(long aPlay = 0, long bPlay = 0, long cPlay = 0)
    {
        var (a, b, c) = (MakeGame("a", "Apex", aPlay), MakeGame("b", "Borderlands", bPlay), MakeGame("c", "Celeste", cPlay));
        Load([a, b, c]);
        return (a, b, c);
    }

    // ---- Not played yet ----------------------------------------------------------------------------

    [Fact]
    public void TheNotPlayedView_ShowsOnlyGamesWithNoTrackedPlayTime_AndRetitlesTheGrid()
    {
        ThreeGames(aPlay: 3600);

        _sut.SelectViewCommand.Execute("unplayed");

        Assert.Equal(["Borderlands", "Celeste"], _sut.Games.Select(g => g.Name));
        Assert.Equal("Not played yet", _sut.LibraryHeaderText);
        Assert.Equal("2 games with no tracked play time", _sut.LibrarySubheaderText);
        Assert.True(_sut.IsNeverPlayedViewSelected);
        Assert.False(_sut.IsAllViewSelected);
        Assert.False(_sut.HasFeaturedGame);
    }

    [Fact]
    public void LeavingTheNotPlayedView_ClearsItsSelection_AndRestoresEveryGame()
    {
        ThreeGames(aPlay: 3600);
        _sut.SelectViewCommand.Execute("unplayed");

        _sut.SelectViewCommand.Execute("all");

        Assert.False(_sut.IsNeverPlayedViewSelected);
        Assert.True(_sut.IsAllViewSelected);
        Assert.Equal(3, _sut.Games.Count);
    }

    [Fact]
    public void TheNotPlayedView_WhenEverythingIsPlayed_SaysSo()
    {
        ThreeGames(1, 1, 1);

        _sut.SelectViewCommand.Execute("unplayed");

        Assert.Empty(_sut.Games);
        Assert.True(_sut.HasNoGames);
        Assert.Equal("Every game has been played", _sut.EmptyStateTitle);
        Assert.False(_sut.ShowEmptyDiscoveryActions);
    }

    [Fact]
    public void TheNotPlayedView_StillHonoursSearch()
    {
        ThreeGames();
        _sut.SelectViewCommand.Execute("unplayed");

        _sut.SearchText = "cel";

        Assert.Equal(["Celeste"], _sut.Games.Select(g => g.Name));
        Assert.Equal("1 game with no tracked play time", _sut.LibrarySubheaderText);
    }

    // ---- Pick a game for me ------------------------------------------------------------------------

    [Fact]
    public void Picking_ChoosesFromWhatIsOnScreen_AndNothingLaunchesWithoutPlay()
    {
        ThreeGames(aPlay: 3600);
        _sut.SelectViewCommand.Execute("unplayed");
        _sut.RandomIndex = max => max - 1;
        PickGameViewModel? shown = null;
        _sut.PickGameDialogForTest = vm => { shown = vm; return false; };

        _sut.PickGameCommand.Execute(null);

        Assert.Equal("Celeste", shown!.Game.Name);
        Assert.Equal("One of 2 games in this view", shown.PoolText);
        Assert.DoesNotContain("Failed to launch", _sut.StatusText);
    }

    [Fact]
    public void Picking_AnotherNeverRepeatsTheCurrentGame()
    {
        ThreeGames();
        _sut.RandomIndex = _ => 0;
        PickGameViewModel? shown = null;
        _sut.PickGameDialogForTest = vm =>
        {
            shown = vm;
            var seen = new List<string> { vm.Game.Name };
            for (var i = 0; i < 5; i++)
            {
                vm.PickAnotherCommand.Execute(null);
                seen.Add(vm.Game.Name);
            }

            Assert.Equal("Apex", seen[0]);
            Assert.All(seen.Zip(seen.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
            return false;
        };

        _sut.PickGameCommand.Execute(null);

        Assert.NotNull(shown);
    }

    [Fact]
    public void Picking_WithOneGame_OffersNoOtherChoice()
    {
        Load([MakeGame("a", "Apex")]);
        _sut.PickGameDialogForTest = vm =>
        {
            Assert.False(vm.PickAnotherCommand.CanExecute(null));
            Assert.Equal("The only game in this view", vm.PoolText);
            return false;
        };

        _sut.PickGameCommand.Execute(null);
    }

    [Fact]
    public void Picking_WithPlay_LaunchesTheShownGame()
    {
        ThreeGames();
        _sut.RandomIndex = _ => 0;
        _sut.PickGameDialogForTest = _ => true;

        _sut.PickGameCommand.Execute(null);

        // The fake exe does not exist, so a launch attempt is visible as the launch failure it produces.
        Assert.StartsWith("Failed to launch Apex", _sut.StatusText);
    }

    [Fact]
    public void Picking_WithNothingOnScreen_SaysSoInsteadOfOpeningADialog()
    {
        var opened = false;
        _sut.PickGameDialogForTest = _ => { opened = true; return false; };

        _sut.PickGameCommand.Execute(null);

        Assert.False(opened);
        Assert.Equal("There are no games in this view to pick from.", _sut.StatusText);
    }

    [Fact]
    public void Picking_NeverChoosesAHiddenGame()
    {
        var (a, b, c) = ThreeGames();
        _sut.ToggleHiddenCommand.Execute(a);
        _sut.ToggleHiddenCommand.Execute(b);
        PickGameViewModel? shown = null;
        _sut.PickGameDialogForTest = vm => { shown = vm; return false; };

        _sut.PickGameCommand.Execute(null);

        Assert.Same(c, shown!.Game);
    }

    // ---- Stats -------------------------------------------------------------------------------------

    [Fact]
    public void TheStatsPage_IsGivenTheLibrarysFigures()
    {
        ThreeGames(aPlay: 7200);
        LibraryStatsSnapshot? shown = null;
        _sut.StatsDialogForTest = stats => { shown = stats; return false; };

        _sut.ShowStatsCommand.Execute(null);

        Assert.Equal(3, shown!.GameCount);
        Assert.Equal(7200, shown.TotalPlaySeconds);
        Assert.Equal(2, shown.NotPlayedCount);
        Assert.True(_sut.IsAllViewSelected);
    }

    [Fact]
    public void ShowThem_OnTheStatsPage_OpensTheNotPlayedView()
    {
        ThreeGames(aPlay: 7200);
        _sut.StatsDialogForTest = _ => true;

        _sut.ShowStatsCommand.Execute(null);

        Assert.True(_sut.IsNeverPlayedViewSelected);
        Assert.Equal(["Borderlands", "Celeste"], _sut.Games.Select(g => g.Name));
    }

    // ---- What's using this drive -------------------------------------------------------------------

    [Fact]
    public void TheStoragePage_ListsOnlyTheGamesOnThatDrive_HiddenOnesIncluded()
    {
        var a = MakeGame("a", "Apex", drive: "C:");
        var b = MakeGame("b", "Borderlands", drive: "D:");
        var c = MakeGame("c", "Celeste", drive: "C:");
        Load([a, b, c]);
        _sut.ToggleHiddenCommand.Execute(c);
        a.InstallSizeBytes = 10L << 30;
        c.InstallSizeBytes = 30L << 30;
        StorageViewModel? shown = null;
        IReadOnlyList<string>? ranked = null;
        _sut.StorageDialogForTest = vm => { shown = vm; ranked = vm.Snapshot.Rows.Select(r => r.Name).ToList(); };

        _sut.ShowStorageCommand.Execute("C:");

        Assert.Equal("What's using C:", shown!.Title);
        Assert.Equal(["Celeste", "Apex"], ranked);
    }

    [Fact]
    public void TheStoragePage_WithNoLetter_UsesTheDriveFilteringTheGrid_AndDoesNothingOtherwise()
    {
        var a = MakeGame("a", "Apex", drive: "C:");
        Load([a]);
        var opened = new List<string>();
        _sut.StorageDialogForTest = vm => opened.Add(vm.DriveLetter);

        _sut.ShowStorageCommand.Execute(null);
        Assert.Empty(opened);

        _sut.SelectedDriveLetter = "C:";
        _sut.ShowStorageCommand.Execute(null);
        Assert.Equal(["C:"], opened);
    }

    [Fact]
    public void TheStoragePage_ReRanksAsSizesAreMeasured_AndStopsListeningWhenClosed()
    {
        var a = MakeGame("a", "Apex");
        var b = MakeGame("b", "Borderlands");
        Load([a, b]);
        StorageViewModel? shown = null;
        _sut.StorageDialogForTest = vm =>
        {
            shown = vm;
            Assert.Equal(2, vm.Snapshot.UnmeasuredCount);

            a.InstallSizeBytes = 5L << 30;
            Assert.Equal(1, vm.Snapshot.UnmeasuredCount);

            b.InstallSizeBytes = 9L << 30;
            Assert.Equal(["Borderlands", "Apex"], vm.Snapshot.Rows.Select(r => r.Name));
        };

        _sut.ShowStorageCommand.Execute("C:");

        // Closed: later measurements no longer touch the (now disposed) page.
        var finalSnapshot = shown!.Snapshot;
        a.InstallSizeBytes = 1L << 30;
        Assert.Same(finalSnapshot, shown.Snapshot);
    }

    /// <summary>A game whose install folder really exists (the exe in it does not), so "Open install location" has somewhere to go.</summary>
    private GameEntry RealFolderGame()
    {
        var installDir = Path.Combine(_dataDir, "Apex");
        Directory.CreateDirectory(installDir);
        var game = new GameEntry
        {
            Id = "real", Name = "Real", ExecutablePath = Path.Combine(installDir, "missing.exe"), InstallDir = installDir, Source = GameSource.Manual,
        };
        Load([game]);
        return game;
    }

    [Fact]
    public void AStorageRowsFolder_IsOpenedByTheLibrary()
    {
        var game = RealFolderGame();
        string? opened = null;
        _sut.OpenFolderInExplorerForTest = path => opened = path;
        _sut.StorageDialogForTest = vm => vm.RequestOpenInstallLocation(game);

        _sut.ShowStorageCommand.Execute(Path.GetPathRoot(game.InstallDir)!.TrimEnd('\\'));

        Assert.Equal(game.InstallDir, opened);
    }

    [Fact]
    public void TheDetailsPage_OpenInstallLocation_GoesThroughTheLibrary()
    {
        var game = RealFolderGame();
        string? opened = null;
        _sut.OpenFolderInExplorerForTest = path => opened = path;
        _sut.GameDetailsDialogForTest = vm => vm.RequestOpenInstallLocation();

        _sut.ShowGameDetailsCommand.Execute(game);

        Assert.Equal(game.InstallDir, opened);
    }

    // ---- Game details and notes --------------------------------------------------------------------

    [Fact]
    public void Notes_TypedOnTheDetailsPage_AreSavedWhenItCloses_AndComeBack()
    {
        var (a, _, _) = ThreeGames();
        _sut.GameDetailsDialogForTest = vm =>
        {
            Assert.Equal("", vm.Notes);
            vm.Notes = "  Left off at the second boss.  ";
        };

        _sut.ShowGameDetailsCommand.Execute(a);

        Assert.Equal("Left off at the second boss.", _sut.GetGameNotes("a"));
        Assert.Equal("Left off at the second boss.", new SettingsService(_dataDir).Load().Overrides["a"].Notes);

        string? reopened = null;
        _sut.GameDetailsDialogForTest = vm => reopened = vm.Notes;
        _sut.ShowGameDetailsCommand.Execute(a);
        Assert.Equal("Left off at the second boss.", reopened);
    }

    [Fact]
    public void ClearingTheNotes_RemovesThem_AndWhitespaceAloneIsNotSaved()
    {
        var (a, _, _) = ThreeGames();
        _sut.SetGameNotes("a", "something");

        _sut.GameDetailsDialogForTest = vm => vm.Notes = "   ";
        _sut.ShowGameDetailsCommand.Execute(a);
        Assert.Equal("", _sut.GetGameNotes("a"));
        Assert.Null(_sut.GetOverride("a")!.Notes);

        _sut.SetGameNotes("b", "   ");
        Assert.Null(_sut.GetOverride("b"));
    }

    [Fact]
    public void Notes_AreCappedAtTheLimit()
    {
        ThreeGames();

        _sut.SetGameNotes("a", new string('x', GameDetailsViewModel.MaxNotesLength + 500));

        Assert.Equal(GameDetailsViewModel.MaxNotesLength, _sut.GetGameNotes("a").Length);
    }

    [Fact]
    public void ClosingTheDetailsPageUnchanged_WritesNothing()
    {
        var (a, _, _) = ThreeGames();
        _sut.GameDetailsDialogForTest = _ => { };

        _sut.ShowGameDetailsCommand.Execute(a);

        Assert.Null(_sut.GetOverride("a"));
    }

    [Fact]
    public void TheDetailsPage_Play_LaunchesTheGame()
    {
        var (a, _, _) = ThreeGames();
        _sut.GameDetailsDialogForTest = vm => vm.PlayRequested = true;

        _sut.ShowGameDetailsCommand.Execute(a);

        Assert.StartsWith("Failed to launch Apex", _sut.StatusText);
    }

    [Fact]
    public void TheDetailsPage_Collections_GoThroughTheLibrary()
    {
        var (a, _, _) = ThreeGames();
        _sut.CollectionsDialogForTest = dialog => ["Backlog"];
        _sut.GameDetailsDialogForTest = vm =>
        {
            Assert.Equal("None", vm.CollectionsText);
            vm.RequestEditCollections();
            Assert.Equal("Backlog", vm.CollectionsText);
        };

        _sut.ShowGameDetailsCommand.Execute(a);

        Assert.Equal(["Backlog"], a.Collections);
    }

    [Fact]
    public void TheDetailsPage_DescribesTheGame()
    {
        var game = MakeGame("a", "Apex", playSeconds: 5400);
        game.InstallSizeBytes = 12L << 30;
        Load([game]);
        GameDetailsViewModel? shown = null;
        _sut.GameDetailsDialogForTest = vm => shown = vm;

        _sut.ShowGameDetailsCommand.Execute(game);

        Assert.Equal("No launcher", shown!.SourceText); // a game with no launcher reads "No launcher" everywhere now
        Assert.Equal("12 GB", shown.SizeText);
        Assert.StartsWith("1.5 h tracked, last played ", shown.PlayTimeText);
    }

    [Fact]
    public void TheDetailsPage_ForAnUnplayedUnmeasuredGame_SaysSoPlainly()
    {
        var (a, _, _) = ThreeGames();
        GameDetailsViewModel? shown = null;
        _sut.GameDetailsDialogForTest = vm => shown = vm;

        _sut.ShowGameDetailsCommand.Execute(a);

        Assert.Equal("Not measured yet", shown!.SizeText);
        Assert.StartsWith("Not played yet", shown.PlayTimeText);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("mine", null, "mine")]
    [InlineData(null, "theirs", "theirs")]
    [InlineData("same", " same ", "same")]
    [InlineData("mine", "theirs", "mine\n\ntheirs")]
    [InlineData("mine\n\ntheirs", "theirs", "mine\n\ntheirs")]
    public void MergingTwoGamesNotes_KeepsBoth(string? winner, string? loser, string? expected) =>
        Assert.Equal(expected, LibraryViewModel.MergeNotes(winner, loser));

    [Fact]
    public void ADedupMerge_CarriesTheLosersNotesOver()
    {
        ThreeGames();
        _sut.SetGameNotes("a", "winner notes");
        _sut.SetGameNotes("b", "loser notes");

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["b"] = "a" });

        Assert.Equal("winner notes\n\nloser notes", _sut.GetGameNotes("a"));
        Assert.Null(_sut.GetOverride("b"));
    }
}
