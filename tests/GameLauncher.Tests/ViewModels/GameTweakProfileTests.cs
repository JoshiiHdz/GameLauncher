using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Game tweaks are one list for every game (the per-game window is gone), and the Windows.old card.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class GameTweakProfileTests(WpfStaFixture sta)
{
    private static (LibraryViewModel Vm, PretendRegistry Registry, GameEntry Game) Make()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Profiles-" + Guid.NewGuid());
        var registry = new PretendRegistry();
        var previous = LibraryViewModel.UserRegistryFactory;
        LibraryViewModel.UserRegistryFactory = () => registry;
        try
        {
            var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
            var game = new GameEntry { Id = "g1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G" };
            return (vm, registry, game);
        }
        finally
        {
            LibraryViewModel.UserRegistryFactory = previous;
        }
    }

    private static bool Has(PretendRegistry r, string key, string name) => r.Values.ContainsKey((key, name));

    [Fact]
    public void EveryGameFollowsTheOneList_AndALeftOverListOnAGameChangesNothing() => sta.RunAsync(async () =>
    {
        var (vm, registry, game) = Make();
        vm.GameTweakRows.First(r => r.Tweak.Id == "capture-off").IsOn = true;
        Assert.Equal(["capture-off"], vm.TweaksForGame(game.Id));

        vm.EnsureOverrideForTest(game.Id).GameTweaks = ["game-mode"];                      // saved by an earlier build: left alone, not used
        Assert.Equal(["capture-off"], vm.TweaksForGame(game.Id));

        vm.BeginGameTweaks(game);
        Assert.True(Has(registry, @"System\GameConfigStore", "GameDVR_Enabled"));
        Assert.False(Has(registry, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"));
        vm.EndGameTweaks();
        await Task.CompletedTask;
    });

    [Fact]
    public void EveryGameTweaksButton_OpensSettingsOnPerformance_NotAWindowOfItsOwn() => sta.RunAsync(async () =>
    {
        var (vm, _, game) = Make();
        vm.SimulateRefreshResult([game]);

        vm.EditGameTweaksCommand.Execute(game);                                            // the menu row, the details page button

        Assert.True(vm.IsSettingsPageOpen);
        Assert.True(vm.IsOptimizePageOpen);                                                // Settings, on its Performance category
        vm.ClosePage();

        // The quick view's button closes the panel first, then opens the same place.
        vm.GameQuickViewDialogForTest = details => details.TweaksRequested = true;
        vm.ShowGameQuickViewCommand.Execute(game);
        Assert.True(vm.IsOptimizePageOpen);
        vm.ClosePage();

        // The menus still offer it.
        var shell = new ShellState(vm);
        shell.OpenCardMenu(game, new System.Windows.Rect(0, 0, 10, 10));
        Assert.Contains(shell.MenuItems, i => i.Header == "Game tweaks...");
        await Task.CompletedTask;
    });

    [Fact]
    public void ABackupStillCarriesALeftOverGameList_SoNothingSavedIsLost() => sta.RunAsync(async () =>
    {
        var settings = new AppSettings();
        settings.Overrides["g1"] = new GameOverride { GameTweaks = ["game-mode", "svc-search"], Favorite = true };
        var built = LibraryBackup.Build(settings, "1.0.0", DateTime.UtcNow);
        Assert.Equal(["game-mode", "svc-search"], built.Games["g1"].GameTweaks);

        var target = new AppSettings();
        built.Games["g1"].GameTweaks!.Add("not-a-tweak");
        LibraryBackup.Merge(target, built, new HashSet<string> { "g1" });
        Assert.Equal(["game-mode", "svc-search"], target.Overrides["g1"].GameTweaks);     // unknown ids dropped on the way in
        await Task.CompletedTask;
    });

    [Fact]
    public void TheWindowsOldCard_ShowsTheSize_OrHidesWhenThereIsNone_AndOpensWindowsOwnCleanup() => sta.RunAsync(async () =>
    {
        var opened = 0;
        OptimizeViewModel Page(Func<long?> sizer) => new(new NoMemory(), [], [], _ => true) { WindowsOldSizer = sizer, OpenWindowsCleanupAction = () => opened++ };

        var present = Page(() => 14L << 30);
        await present.RefreshWindowsOldAsync();
        Assert.True(present.HasWindowsOld);
        Assert.Contains("14 GB", present.WindowsOldText);
        Assert.Contains("ten days", present.WindowsOldText);
        present.OpenWindowsCleanupCommand.Execute(null);
        Assert.Equal(1, opened);

        var none = Page(() => null);
        await none.RefreshWindowsOldAsync();
        Assert.False(none.HasWindowsOld);

        var broken = Page(() => throw new IOException("denied"));
        await broken.RefreshWindowsOldAsync();                                            // never throws into the page
        Assert.False(broken.HasWindowsOld);
    });

    private sealed class NoMemory : IMemoryOptimizer
    {
        public MemoryStatus GetStatus() => new(16L << 30, 8L << 30);
        public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders) => new(0, 0, 0, 0);
    }
}
