using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for clicking a drive in the sidebar: it filters Games/FavoriteGames/HiddenGames
/// down to that drive (toggling the same drive again clears the filter, exactly like a sidebar source
/// switch), and kicks off InstallSizeEstimator for just the games on that drive - never the whole
/// library, so merely browsing never pays that cost (see EstimateSizesForSelectedDriveAsync's own
/// remarks). InstallSizeEstimatorForTest is a plain synchronous delegate, not a Task - when it's set,
/// EstimateSizesForSelectedDriveAsync's own "await Task.Run(...)" branch is never evaluated (the other
/// side of its ternary), so the whole size-estimation pass for a SelectDriveCommand.Execute(...) call
/// completes synchronously before Execute returns. Tests below rely on that to stay deterministic -
/// no real disk I/O, no timing races.
/// </summary>
public class LibraryViewModelDriveFilterTests : IDisposable
{
    private readonly string _dataDir;
    private readonly LibraryViewModel _sut;

    public LibraryViewModelDriveFilterTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, string installDir, string name = "Test Game") => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = installDir + @"\game.exe",
        InstallDir = installDir,
        Source = GameSource.Manual,
    };

    private static DriveSpaceInfo MakeDrive(string letter) => new()
    {
        Letter = letter,
        Label = "Test Drive",
        TotalBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
    };

    // ---- Filtering ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "≈ 2 KB")]
    [InlineData(524288000L, "≈ 500 MB")]
    [InlineData(2147483648L, "≈ 2 GB")]
    [InlineData(2199023255552L, "≈ 2 TB")]
    public void InstallSizes_UseReadableUnits(long bytes, string expected)
    {
        var game = MakeGame("sized", @"G:\Games\Sized");
        game.InstallSizeBytes = bytes;
        Assert.Equal(expected, game.InstallSizeDisplay);
    }

    [Fact]
    public void SelectingADrive_ShowsOnlyGamesOnThatDrive()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        var onS = MakeGame("s-game", @"S:\Games\SOnly");
        _sut.SimulateRefreshResult([onG, onS]);
        _sut.Drives.Add(MakeDrive("G:"));
        _sut.Drives.Add(MakeDrive("S:"));

        _sut.SelectDriveCommand.Execute("G:");

        Assert.Contains(_sut.Games, g => g.Id == "g-game");
        Assert.DoesNotContain(_sut.Games, g => g.Id == "s-game");
    }

    [Fact]
    public void SelectingADrive_AlsoFiltersFavoritesAndHiddenGames()
    {
        var favOnG = MakeGame("fav-g", @"G:\Games\Fav");
        var favOnS = MakeGame("fav-s", @"S:\Games\Fav");
        var hiddenOnG = MakeGame("hidden-g", @"G:\Games\Hidden");
        var hiddenOnS = MakeGame("hidden-s", @"S:\Games\Hidden");
        _sut.SimulateRefreshResult([favOnG, favOnS, hiddenOnG, hiddenOnS]);
        _sut.ToggleFavoriteCommand.Execute(favOnG);
        _sut.ToggleFavoriteCommand.Execute(favOnS);
        _sut.ToggleHiddenCommand.Execute(hiddenOnG);
        _sut.ToggleHiddenCommand.Execute(hiddenOnS);

        _sut.SelectDriveCommand.Execute("G:");

        Assert.Contains(_sut.FavoriteGames, g => g.Id == "fav-g");
        Assert.DoesNotContain(_sut.FavoriteGames, g => g.Id == "fav-s");
        Assert.Contains(_sut.HiddenGames, g => g.Id == "hidden-g");
        Assert.DoesNotContain(_sut.HiddenGames, g => g.Id == "hidden-s");
    }

    [Fact]
    public void ClickingTheSameDriveTwice_ClearsTheFilter()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        var onS = MakeGame("s-game", @"S:\Games\SOnly");
        _sut.SimulateRefreshResult([onG, onS]);
        _sut.Drives.Add(MakeDrive("G:"));

        _sut.SelectDriveCommand.Execute("G:");
        Assert.DoesNotContain(_sut.Games, g => g.Id == "s-game");

        _sut.SelectDriveCommand.Execute("G:"); // same drive again

        Assert.Null(_sut.SelectedDriveLetter);
        Assert.False(_sut.HasSelectedDriveFilter);
        Assert.Contains(_sut.Games, g => g.Id == "g-game");
        Assert.Contains(_sut.Games, g => g.Id == "s-game");
    }

    [Fact]
    public void SelectingADrive_MarksItsOwnRowSelected_AndNoOtherRow()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        _sut.SimulateRefreshResult([onG]);
        var g = MakeDrive("G:");
        var s = MakeDrive("S:");
        _sut.Drives.Add(g);
        _sut.Drives.Add(s);

        _sut.SelectDriveCommand.Execute("G:");

        Assert.True(g.IsSelected);
        Assert.False(s.IsSelected);
    }

    [Fact]
    public void SelectingADrive_UpdatesTheLibraryHeaderToNameTheDrive()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        _sut.SimulateRefreshResult([onG]);
        _sut.Drives.Add(MakeDrive("G:"));

        _sut.SelectDriveCommand.Execute("G:");
        Assert.Equal("Games on G:", _sut.LibraryHeaderText);

        _sut.SelectDriveCommand.Execute("G:");
        Assert.Equal("All games", _sut.LibraryHeaderText);
    }

    // ---- Size estimation -----------------------------------------------------------------------------

    [Fact]
    public void SelectingADrive_MeasuresThatDrivesGamesFirst_ThenTheRestOfTheLibrary()
    {
        // Sizes are needed library-wide (the hero shows one with no drive selected, and "Largest
        // installed" sorts on them), so selecting a drive no longer SCOPES the work to that drive - it
        // only moves that drive's games to the front of the queue.
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        var onS = MakeGame("s-game", @"S:\Games\SOnly");
        _sut.SimulateRefreshResult([onS, onG]); // S first, so ordering can't pass by accident
        var order = new List<string>();
        _sut.InstallSizeEstimatorForTest = (path, _) => { order.Add(path); return 12_345; };

        _sut.SelectDriveCommand.Execute("G:");

        Assert.Equal(12_345, onG.InstallSizeBytes);
        Assert.Equal(12_345, onS.InstallSizeBytes); // measured too, just afterwards
        Assert.Equal([onG.InstallDir, onS.InstallDir], order);
    }

    [Fact]
    public void ReselectingTheSameDrive_NeverRecomputesAnAlreadyKnownSize()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        _sut.SimulateRefreshResult([onG]);
        var calls = 0;
        _sut.InstallSizeEstimatorForTest = (_, _) => { calls++; return 999; };

        _sut.SelectDriveCommand.Execute("G:"); // select
        _sut.SelectDriveCommand.Execute("G:"); // clear
        _sut.SelectDriveCommand.Execute("G:"); // select again

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SwitchingDrivesWhileAWalkIsStillRunning_NeverLetsTheStaleWalkOverwriteTheNewSelection()
    {
        // gameA's own estimator callback switches the drive filter BEFORE returning gameA's own size -
        // simulating "the user clicked a different drive while gameA's walk was still in flight".
        // EstimateSizesForSelectedDriveAsync must notice its own _sizeEstimationCts was replaced by that
        // nested call and discard gameA's result instead of writing it.
        var gameA = MakeGame("g-game-a", @"G:\Games\A");
        var gameOnS = MakeGame("s-game", @"S:\Games\SOnly");
        _sut.SimulateRefreshResult([gameA, gameOnS]);

        _sut.InstallSizeEstimatorForTest = (path, _) =>
        {
            if (path == gameA.InstallDir)
                _sut.SelectDriveCommand.Execute("S:"); // reentrant: switches drives mid-walk
            return 777;
        };

        _sut.SelectDriveCommand.Execute("G:");

        Assert.Null(gameA.InstallSizeBytes); // the stale G: walk's result for gameA must never land
        Assert.Equal(777, gameOnS.InstallSizeBytes); // the newer S: selection's own result does land
        Assert.Equal("S:", _sut.SelectedDriveLetter);
    }

    [Fact]
    public void ClearingTheFilter_RunsNoEstimationAtAll()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        _sut.SimulateRefreshResult([onG]);
        var calls = 0;
        _sut.InstallSizeEstimatorForTest = (_, _) => { calls++; return 1; };

        _sut.SelectDriveCommand.Execute("G:");
        _sut.SelectDriveCommand.Execute("G:"); // clear

        Assert.Equal(1, calls); // only the one walk from actually selecting G:, none from clearing it
    }

    [Fact]
    public void InstallSizeBytes_PopulatesTheDisplayStringAndTheHasInstallSizeFlag()
    {
        var onG = MakeGame("g-game", @"G:\Games\GOnly");
        _sut.SimulateRefreshResult([onG]);
        _sut.InstallSizeEstimatorForTest = (_, _) => 2L * 1024 * 1024 * 1024; // exactly 2 GB

        _sut.SelectDriveCommand.Execute("G:");

        Assert.True(onG.HasInstallSize);
        Assert.Equal("≈ 2 GB", onG.InstallSizeDisplay);
    }
}
