using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Export and import of the user's library data: what goes in, what must never, and that importing is a safe merge.</summary>
public class LibraryBackupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static AppSettings Settings() => new()
    {
        SteamGridDbApiKey = "SECRET-SGDB-KEY",
        IgdbClientId = "SECRET-IGDB-ID",
        DetectGog = false,
        OptimizeBeforeLaunch = true,
        WatchedFolders = [new WatchedFolder { Path = @"D:\MyGames" }],
        Overrides =
        {
            ["steam-1"] = new GameOverride
            {
                Favorite = true, Collections = ["Backlog"], Notes = "second boss", CustomName = "Elden", TotalPlaySeconds = 7200,
                DateAdded = Now.AddDays(-30), LastPlayedUtc = Now.AddDays(-1),
                Artwork = new ArtworkSelection { AssetId = "asset-should-not-travel", IsUserSelected = true },
            },
            ["steam-2"] = new GameOverride(), // nothing worth saving
        },
    };

    // ---- export ------------------------------------------------------------------------------------

    [Fact]
    public void AnExport_CarriesTheUsersChoices_AndLeavesOutAnythingEmpty()
    {
        var file = LibraryBackup.Build(Settings(), "1.20.0", Now);

        var game = Assert.Single(file.Games);
        Assert.Equal("steam-1", game.Key);
        Assert.True(game.Value.Favorite);
        Assert.Equal(["Backlog"], game.Value.Collections);
        Assert.Equal("second boss", game.Value.Notes);
        Assert.Equal("Elden", game.Value.CustomName);
        Assert.Equal(7200, game.Value.TotalPlaySeconds);
        Assert.False(file.Settings.DetectGog);
        Assert.True(file.Settings.OptimizeBeforeLaunch);
        Assert.Equal("1.20.0", file.AppVersion);
    }

    [Fact]
    public void AnExport_NeverContainsKeys_Artwork_OrPcSpecificFolders()
    {
        var json = LibraryBackup.ToJson(LibraryBackup.Build(Settings(), "1.20.0", Now));

        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("asset-should-not-travel", json);
        Assert.DoesNotContain("MyGames", json);
        Assert.DoesNotContain("Artwork", json);
        Assert.DoesNotContain("WatchedFolders", json);
    }

    // ---- reading -----------------------------------------------------------------------------------

    [Fact]
    public void ABackup_RoundTripsThroughItsFile()
    {
        var json = LibraryBackup.ToJson(LibraryBackup.Build(Settings(), "1.20.0", Now));

        var (file, error) = LibraryBackup.Parse(json);

        Assert.Null(error);
        Assert.Equal("second boss", file!.Games["steam-1"].Notes);
        Assert.Equal(Now.AddDays(-30), file.Games["steam-1"].DateAdded);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{ \"Games\": ")]
    public void DamagedText_IsRejectedWithAPlainReason(string text)
    {
        var (file, error) = LibraryBackup.Parse(text);

        Assert.Null(file);
        Assert.Equal("That file isn't a valid backup - it may be damaged or not a backup at all.", error);
    }

    [Fact]
    public void SomeOtherJson_IsNotMistakenForABackup()
    {
        var (file, error) = LibraryBackup.Parse("{\"Overrides\":{\"a\":{}}}");

        Assert.Null(file);
        Assert.Equal("That file isn't an Axis Game Launcher backup.", error);
    }

    [Fact]
    public void ABackupFromANewerVersion_IsRefusedRatherThanMisread()
    {
        var (file, error) = LibraryBackup.Parse("{\"Kind\":\"Axis Game Launcher backup\",\"Version\":99}");

        Assert.Null(file);
        Assert.Contains("newer version", error);
    }

    [Fact]
    public void MissingSections_AreTolerated()
    {
        var (file, error) = LibraryBackup.Parse("{\"Kind\":\"Axis Game Launcher backup\",\"Version\":1,\"Games\":null,\"Settings\":null}");

        Assert.Null(error);
        Assert.Empty(file!.Games);
        Assert.NotNull(file.Settings);
    }

    // ---- merging -----------------------------------------------------------------------------------

    private static BackupFile Incoming(Action<BackupGame> shape, string id = "steam-1")
    {
        var game = new BackupGame();
        shape(game);
        return new BackupFile { Games = { [id] = game } };
    }

    [Fact]
    public void Merging_IntoAnEmptyLibrary_CreatesTheOverrides()
    {
        var settings = new AppSettings();
        var file = Incoming(g => { g.Favorite = true; g.Collections = ["Co-op"]; g.Notes = "n"; g.TotalPlaySeconds = 100; });

        var summary = LibraryBackup.Merge(settings, file, new HashSet<string> { "steam-1" });

        Assert.Equal(new ImportSummary(1, 1, 1, 0), summary);
        Assert.True(settings.Overrides["steam-1"].Favorite);
        Assert.Equal(["Co-op"], settings.Overrides["steam-1"].Collections);
    }

    [Fact]
    public void Merging_NeverRemovesOrReplacesWhatIsAlreadyThere()
    {
        var settings = new AppSettings
        {
            Overrides =
            {
                ["steam-1"] = new GameOverride
                {
                    Favorite = true, Hidden = true, CustomName = "Mine", Collections = ["Playing"], Notes = "my note",
                    TotalPlaySeconds = 9000, DateAdded = Now.AddDays(-5), LastPlayedUtc = Now,
                },
            },
        };
        var file = Incoming(g =>
        {
            g.Favorite = false; g.Hidden = false; g.CustomName = "Theirs"; g.Collections = ["Backlog"]; g.Notes = "their note";
            g.TotalPlaySeconds = 100; g.DateAdded = Now.AddDays(-50); g.LastPlayedUtc = Now.AddDays(-9);
        });

        LibraryBackup.Merge(settings, file, new HashSet<string>());
        var over = settings.Overrides["steam-1"];

        Assert.True(over.Favorite);
        Assert.True(over.Hidden);
        Assert.Equal("Mine", over.CustomName);
        Assert.Equal(["Playing", "Backlog"], over.Collections);
        Assert.Equal("my note\n\ntheir note", over.Notes);
        Assert.Equal(9000, over.TotalPlaySeconds);          // the larger figure, not a sum
        Assert.Equal(Now.AddDays(-50), over.DateAdded);     // the earlier date
        Assert.Equal(Now, over.LastPlayedUtc);              // the later play
    }

    [Fact]
    public void Merging_FillsInWhatIsMissing()
    {
        var settings = new AppSettings { Overrides = { ["steam-1"] = new GameOverride { Favorite = true } } };
        var file = Incoming(g => { g.CustomName = "Elden"; g.TotalPlaySeconds = 500; g.Hidden = true; });

        LibraryBackup.Merge(settings, file, new HashSet<string>());

        Assert.Equal("Elden", settings.Overrides["steam-1"].CustomName);
        Assert.Equal(500, settings.Overrides["steam-1"].TotalPlaySeconds);
        Assert.True(settings.Overrides["steam-1"].Hidden);
    }

    [Fact]
    public void ImportingTheSameFileTwice_ChangesNothingTheSecondTime()
    {
        var settings = new AppSettings
        {
            Overrides = { ["steam-1"] = new GameOverride { Collections = ["Playing"], Notes = "mine", TotalPlaySeconds = 50 } },
        };
        var file = Incoming(g => { g.Collections = ["Backlog"]; g.Notes = "theirs"; g.TotalPlaySeconds = 500; g.Favorite = true; });

        var first = LibraryBackup.Merge(settings, file, new HashSet<string>());
        var snapshot = JsonSerializer.Serialize(settings.Overrides);
        var second = LibraryBackup.Merge(settings, file, new HashSet<string>());

        Assert.Equal(1, first.GamesUpdated);
        Assert.Equal(0, second.GamesUpdated);
        Assert.Equal(0, second.GamesAddedAsNew);
        Assert.Equal(snapshot, JsonSerializer.Serialize(settings.Overrides));
    }

    [Fact]
    public void Merging_CapsNotes_NormalisesCollectionNames_AndIgnoresBlankIds()
    {
        var settings = new AppSettings();
        var file = new BackupFile
        {
            Games =
            {
                ["steam-1"] = new BackupGame { Notes = new string('x', 9000), Collections = ["  Co-op  ", "co-op", ""], TotalPlaySeconds = -5 },
                [" "] = new BackupGame { Favorite = true },
            },
        };

        LibraryBackup.Merge(settings, file, new HashSet<string>());

        Assert.Single(settings.Overrides);
        Assert.Equal(GameDetailsViewModel.MaxNotesLength, settings.Overrides["steam-1"].Notes!.Length);
        Assert.Equal(["Co-op"], settings.Overrides["steam-1"].Collections);
        Assert.Equal(0, settings.Overrides["steam-1"].TotalPlaySeconds);
    }

    [Fact]
    public void TheSummary_SaysHowManyAreInThisLibrary()
    {
        var file = new BackupFile { Games = { ["a"] = new BackupGame { Favorite = true }, ["b"] = new BackupGame { Favorite = true } } };

        var summary = LibraryBackup.Merge(new AppSettings(), file, new HashSet<string> { "a" });

        Assert.Equal(2, summary.GamesInFile);
        Assert.Equal(1, summary.GamesInThisLibrary);
        Assert.Contains("2 games", summary.Describe());
        Assert.Contains("1 in your library right now", summary.Describe());
    }

    // ---- through the library view model ------------------------------------------------------------

    private LibraryViewModel Library(string? dir = null) =>
        new(new SettingsService(dir ?? _dataDir), new PendingUpdateNotesService(dir ?? _dataDir)) { InstallSizeEstimatorForTest = (_, _) => null };

    private static GameEntry Game(string id, string name) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = GameSource.Manual,
    };

    [Fact]
    public void ExportThenImportOnAnotherInstall_BringsEverythingAcross_AndUpdatesTheGamesOnScreen()
    {
        var path = Path.Combine(_dataDir, "backup.json");
        Directory.CreateDirectory(_dataDir);

        var source = Library();
        var a = Game("a", "Apex");
        source.SimulateRefreshResult([a]);
        source.ToggleFavoriteCommand.Execute(a);
        source.SetGameCollections(a, ["Co-op"]);
        source.SetGameNotes("a", "left off at level 4");
        source.OptimizeBeforeLaunch = true;
        source.ExportPathPickerForTest = _ => path;
        source.ExportLibraryDataCommand.Execute(null);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var otherDir = Path.Combine(_dataDir, "other");
        var target = Library(otherDir);
        var onScreen = Game("a", "Apex");
        target.SimulateRefreshResult([onScreen, Game("b", "Borderlands")]);
        target.SelectViewCommand.Execute("all");
        target.ImportPathPickerForTest = () => path;
        string? asked = null;
        target.ConfirmImportForTest = q => { asked = q; return true; };

        target.ImportLibraryDataCommand.Execute(null);

        Assert.Contains("Import data for 1 game", asked);
        Assert.True(onScreen.Favorite);
        Assert.Equal(["Co-op"], onScreen.Collections);
        Assert.Equal("left off at level 4", target.GetGameNotes("a"));
        Assert.True(target.OptimizeBeforeLaunch);
        Assert.Equal(["Co-op"], target.Collections.Select(c => c.Name));
        Assert.Single(target.FavoriteGames);
        Assert.StartsWith("Imported data for 1 game", target.StatusText);

        var saved = new SettingsService(otherDir).Load();
        Assert.True(saved.Overrides["a"].Favorite);
        Assert.True(saved.OptimizeBeforeLaunch);
    }

    [Fact]
    public void DecliningTheImport_ChangesNothing()
    {
        var path = Path.Combine(_dataDir, "backup.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(path, LibraryBackup.ToJson(new BackupFile { Games = { ["a"] = new BackupGame { Favorite = true } } }));
        var vm = Library();
        var game = Game("a", "Apex");
        vm.SimulateRefreshResult([game]);
        vm.ImportPathPickerForTest = () => path;
        vm.ConfirmImportForTest = _ => false;

        vm.ImportLibraryDataCommand.Execute(null);

        Assert.False(game.Favorite);
        Assert.Null(vm.GetOverride("a"));
        Assert.Equal("Nothing was imported.", vm.StatusText);
    }

    [Fact]
    public void ANonBackupFile_IsRejectedWithAReason_AndNothingIsChanged()
    {
        var path = Path.Combine(_dataDir, "other.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(path, "{\"hello\": 1}");
        var vm = Library();
        vm.ImportPathPickerForTest = () => path;
        var asked = false;
        vm.ConfirmImportForTest = _ => { asked = true; return true; };

        vm.ImportLibraryDataCommand.Execute(null);

        Assert.False(asked);
        Assert.Equal("That file isn't an Axis Game Launcher backup.", vm.StatusText);
    }

    [Fact]
    public void CancellingEitherDialog_DoesNothing()
    {
        var vm = Library();
        vm.ExportPathPickerForTest = _ => null;
        vm.ImportPathPickerForTest = () => null;
        var status = vm.StatusText;

        vm.ExportLibraryDataCommand.Execute(null);
        vm.ImportLibraryDataCommand.Execute(null);

        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public void AnExportThatCannotBeWritten_IsReported_NotThrown()
    {
        var vm = Library();
        vm.ExportPathPickerForTest = _ => Path.Combine(_dataDir, "missing-folder", "x", "backup.json");

        vm.ExportLibraryDataCommand.Execute(null);

        Assert.StartsWith("Couldn't export your library data", vm.StatusText);
    }

    [Fact]
    public void TheSuggestedFileName_CarriesTheDate() =>
        Assert.Equal("axis-library-backup-2026-10-04.json", LibraryViewModel.DefaultBackupFileName(new DateTime(2026, 10, 4)));
}
