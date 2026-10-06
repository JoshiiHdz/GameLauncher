using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>One test for each defect the bug check found, so none of them can come back.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class BugCheckRegressionTests(WpfStaFixture sta)
{
    private static readonly DateTime Noon = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private static LibraryViewModel NewLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-BugCheck-" + Guid.NewGuid());
        return new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
    }

    private static GameEntry Game(string id, string name = "Game", string exe = @"C:\G\g.exe") =>
        new() { Id = id, Name = name, Source = GameSource.Manual, ExecutablePath = exe, InstallDir = Path.GetDirectoryName(exe)! };

    // ---- the ribbon is rebuilt once per list change, not once per game ----

    [Fact]
    public void ALibraryOfManyGames_RefreshesTheRibbonOnce_NotOncePerGame() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary();
        var changes = 0;
        vm.ListsChanged += () => changes++;

        vm.SimulateRefreshResult(Enumerable.Range(0, 300).Select(i => Game("g" + i, "Game " + i)).ToList());
        vm.SearchText = "Game"; // a search change runs the filter, which rebuilds the lists

        Assert.InRange(changes, 1, 2); // the lists are replaced as a whole; never one event per game
        Assert.Equal(300, vm.Games.Count);

        await Task.CompletedTask;
    });

    [Fact]
    public void TheRibbonStillFollowsTheLibrary_AfterTheChange() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary();
        var shell = new ShellState(vm);

        vm.SimulateRefreshResult([Game("a", "Alpha"), Game("b", "Beta")]);
        vm.SearchText = "a";
        vm.SearchText = "";

        Assert.Equal(3, shell.RibbonItems.Count); // the Library tile and two games

        await Task.CompletedTask;
    });

    // ---- Focus play: a crash after the switch but before it was recorded is still undone ----

    private sealed class Plans : IPowerPlans
    {
        public Guid? Active { get; set; } = FocusPlayService.Balanced;
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan) { Active = plan; return true; }
        public Guid? Duplicate(Guid source, string name) => null;
        public bool Delete(Guid plan) => true;
        public bool IsOnMains() => true;
    }

    [Fact]
    public void ACrashRightAfterTheSwitch_BeforeTheAppliedPlanWasSaved_IsUndoneAtTheNextStart()
    {
        var plans = new Plans { Active = FocusPlayService.UltimatePerformance }; // switched, then the app died
        var settings = new AppSettings { FocusPlayRestorePlan = FocusPlayService.Balanced.ToString(), FocusPlayAppliedPlan = null };

        new FocusPlayService(plans, settings, () => { }).End();

        Assert.Equal(FocusPlayService.Balanced, plans.Active);
        Assert.Null(settings.FocusPlayRestorePlan);
    }

    [Fact]
    public void ACrashBeforeTheSwitchHappened_LeavesThePcAloneAtTheNextStart()
    {
        var plans = new Plans(); // never switched: still on Balanced
        var settings = new AppSettings { FocusPlayRestorePlan = FocusPlayService.Balanced.ToString() };

        new FocusPlayService(plans, settings, () => { }).End();

        Assert.Equal(FocusPlayService.Balanced, plans.Active);
        Assert.Null(settings.FocusPlayRestorePlan);
    }

    [Fact]
    public void ACrashRightAfterSwitchingToAMadePlan_StillKnowsThePlan()
    {
        var made = Guid.NewGuid();
        var plans = new Plans { Active = made };
        var settings = new AppSettings { FocusPlayRestorePlan = FocusPlayService.Balanced.ToString(), FocusPlayOwnPlan = made.ToString() };

        new FocusPlayService(plans, settings, () => { }).End();

        Assert.Equal(FocusPlayService.Balanced, plans.Active);
    }

    // ---- a failed launch only undoes a switch it made itself ----

    [Fact]
    public void AFailedLaunch_DoesNotUndoThePowerPlanOfAGameThatIsStillRunning() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary();
        var plans = new Plans();
        vm.PowerPlansService = plans;
        vm.FocusPlayEnabled = true;
        vm.FocusPlayOnlyWhenPluggedIn = false;
        var broken = Game("broken", exe: @"C:\definitely\not\here\missing.exe");
        vm.SimulateRefreshResult([broken]);

        // An earlier game was launched and is still being played: its launch switched the plan.
        Assert.True(vm.BeginFocusPlay());
        Assert.Equal(FocusPlayService.UltimatePerformance, plans.Active);

        vm.LaunchCommand.Execute(broken);

        Assert.Equal(FocusPlayService.UltimatePerformance, plans.Active); // still fast: the other game is still being played

        await Task.CompletedTask;
    });

    [Fact]
    public void AFailedLaunch_ThatSwitchedThePlanItself_PutsItBack() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary();
        var plans = new Plans();
        vm.PowerPlansService = plans;
        vm.FocusPlayEnabled = true;
        vm.FocusPlayOnlyWhenPluggedIn = false;
        var broken = Game("broken", exe: @"C:\definitely\not\here\missing.exe");
        vm.SimulateRefreshResult([broken]);

        vm.LaunchCommand.Execute(broken);

        Assert.Equal(FocusPlayService.Balanced, plans.Active);

        await Task.CompletedTask;
    });

    // ---- the wide backgrounds held in memory are capped ----

    [Fact]
    public void OnlyTheFewestRecentBackdropsStayLoaded() => sta.RunAsync(async () =>
    {
        var image = BackdropArtService.Decode(SolidPng(600, 300))!;
        var games = Enumerable.Range(0, BackdropArtService.KeepBackdrops + 3).Select(i => Game("steam-" + i)).ToList();
        foreach (var game in games)
        {
            game.Backdrop = image;
            BackdropArtService.Hold(game);
        }

        var kept = games.Count(g => g.Backdrop is not null);
        Assert.Equal(BackdropArtService.KeepBackdrops, kept);
        Assert.All(games.TakeLast(BackdropArtService.KeepBackdrops), g => Assert.NotNull(g.Backdrop)); // the newest are the ones kept

        // Using an old one again makes it the newest: the one used longest ago goes instead.
        var firstKept = games[^BackdropArtService.KeepBackdrops];
        BackdropArtService.Hold(firstKept);
        var another = Game("steam-new");
        another.Backdrop = image;
        BackdropArtService.Hold(another);
        Assert.NotNull(firstKept.Backdrop);

        await Task.CompletedTask;
    });

    private static byte[] SolidPng(int width, int height)
    {
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- the Play time page is reachable from the details page (it used to be only on the PlayStation hero) ----

    [Fact]
    public void TheDetailsPage_CanOpenThePlayTimeBreakdown() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary();
        var game = Game("steam-1", "Elden Ring");
        vm.SimulateRefreshResult([game]);
        vm.ShowGameDetailsCommand.Execute(game);
        var details = Assert.IsType<GameDetailsViewModel>(vm.CurrentPage);

        details.RequestPlayTime();

        Assert.Equal(LibraryViewModel.PlayTimePageKey, vm.CurrentPageKey);
        Assert.Same(game, Assert.IsType<PlayTimeViewModel>(vm.CurrentPage).Game);

        await Task.CompletedTask;
    });

    // ---- the palette highlights the best match ----

    [Fact]
    public void TypingACommandsName_HighlightsTheCommand_EvenWhenAGameSitsInTheFirstGroup() => sta.RunAsync(async () =>
    {
        var items = new List<PaletteItem>
        {
            new("Settlers Online", "Steam", "settlers", PaletteKind.Game, () => { }),
            new("Open settings", "", "preferences options configuration", PaletteKind.Command, () => { }),
        };
        var palette = new CommandPaletteViewModel(items);

        palette.Query = "open settings";

        Assert.Equal("Open settings", palette.Results[palette.SelectedIndex].Title);

        await Task.CompletedTask;
    });

    // ---- backups keep the sessions and the per-game priority ----

    private static AppSettings SettingsWithHistory()
    {
        var settings = new AppSettings();
        var over = new GameOverride { TotalPlaySeconds = 3600, RaiseGamePriority = true };
        over.Sessions.Add(new PlaySessionRecord { StartUtc = Noon, Seconds = 1800 });
        over.Sessions.Add(new PlaySessionRecord { StartUtc = Noon.AddDays(1), Seconds = 1800 });
        settings.Overrides["steam-1"] = over;
        return settings;
    }

    [Fact]
    public void ABackup_CarriesTheSessionsAndThePriorityChoice_ThroughExportAndImport()
    {
        var json = LibraryBackup.ToJson(LibraryBackup.Build(SettingsWithHistory(), "1.20.0", Noon));
        var (file, error) = LibraryBackup.Parse(json);
        Assert.Null(error);

        var fresh = new AppSettings();
        LibraryBackup.Merge(fresh, file!, new HashSet<string> { "steam-1" });

        var over = fresh.Overrides["steam-1"];
        Assert.True(over.RaiseGamePriority);
        Assert.Equal(2, over.Sessions.Count);
        Assert.Equal(3600, over.Sessions.Sum(s => s.Seconds));
    }

    [Fact]
    public void ImportingTheSameBackupTwice_DoesNotDoubleTheSessions()
    {
        var (file, _) = LibraryBackup.Parse(LibraryBackup.ToJson(LibraryBackup.Build(SettingsWithHistory(), "1.20.0", Noon)));
        var fresh = new AppSettings();

        LibraryBackup.Merge(fresh, file!, new HashSet<string>());
        var again = LibraryBackup.Merge(fresh, file!, new HashSet<string>());

        Assert.Equal(2, fresh.Overrides["steam-1"].Sessions.Count);
        Assert.Equal(0, again.GamesUpdated);
    }

    [Fact]
    public void ABackupFromBeforeSessionsExisted_StillImports()
    {
        var json = "{\"Kind\":\"" + BackupFile.Marker + "\",\"Version\":1,\"Games\":{\"steam-1\":{\"Favorite\":true,\"TotalPlaySeconds\":600}}}";

        var (file, error) = LibraryBackup.Parse(json);

        Assert.Null(error);
        Assert.Empty(file!.Games["steam-1"].Sessions);
    }

    // ---- a damaged settings file cannot crash play-time code ----

    [Fact]
    public void ANullOrBrokenSessionsList_IsCleanedWhenSettingsLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-SessionsLoad-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            """{"Overrides":{"a":{"Sessions":null},"b":{"Sessions":[null,{"StartUtc":"2026-10-06T10:00:00Z","Seconds":-5},{"StartUtc":"2026-10-06T10:00:00Z","Seconds":90}]}}}""");

        var settings = new SettingsService(directory).Load();

        Assert.Empty(settings.Overrides["a"].Sessions);
        Assert.Equal(90, Assert.Single(settings.Overrides["b"].Sessions).Seconds);
    }
}
