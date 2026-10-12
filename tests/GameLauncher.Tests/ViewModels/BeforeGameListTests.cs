using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Everything that can happen before a game starts is one list: memory, the power plan, Windows settings and services, each with what Windows has right now,
/// for every game or picked for one; and the Optimize page is tabs instead of one long scroll.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class BeforeGameListTests(WpfStaFixture sta)
{
    private sealed class Memory(long total, long available) : IMemoryOptimizer
    {
        public int Trims;
        public MemoryStatus GetStatus() => new(total, available);

        public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders)
        {
            Trims++;
            return new MemoryTrimResult(2, 0, available, available + (1L << 30));
        }
    }

    private sealed class Plans : IPowerPlans
    {
        public Guid? Active { get; set; } = FocusPlayService.Balanced;
        public List<Guid> Switched { get; } = [];
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan) { Active = plan; Switched.Add(plan); return true; }
        public Guid? Duplicate(Guid source, string name) => null;
        public bool Delete(Guid plan) => true;
        public bool IsOnMains() => true;
    }

    private const long Gb = 1L << 30;

    private static (LibraryViewModel Vm, PretendRegistry Registry, Memory Memory, Plans Plans) Make(long available = 2 * Gb)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-BeforeGame-" + Guid.NewGuid());
        var registry = new PretendRegistry();
        var memory = new Memory(16 * Gb, available);
        var plans = new Plans();
        var previous = LibraryViewModel.UserRegistryFactory;
        LibraryViewModel.UserRegistryFactory = () => registry;
        try
        {
            var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory))
            {
                InstallSizeEstimatorForTest = (_, _) => null,
                MemoryOptimizerService = memory,
                PowerPlansService = plans,
                FocusPlayOnlyWhenPluggedIn = false,
            };
            return (vm, registry, memory, plans);
        }
        finally
        {
            LibraryViewModel.UserRegistryFactory = previous;
        }
    }

    // A game whose file is not there: launching it fails after everything before the launch has been done, so the list's effects can be seen without starting anything real.
    private static GameEntry Broken() => new() { Id = "g1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\definitely\not\here\missing.exe", InstallDir = @"C:\definitely\not\here" };

    private static TweakRow Row(LibraryViewModel vm, string id) => vm.GameTweakRows.Single(r => r.Tweak.Id == id);

    [Fact]
    public void MemoryAndThePowerPlan_AreInTheSameList_AndAreTheSameSwitchesAsTheirSettings() => sta.RunAsync(async () =>
    {
        var (vm, _, _, _) = Make();

        Assert.Equal([GameTweakCatalog.MemoryTrimId, GameTweakCatalog.PowerPlanId], vm.MemoryAndPowerRows.Select(r => r.Tweak.Id));
        Assert.False(Row(vm, "memory-trim").IsOn);
        Assert.False(Row(vm, "power-plan").IsOn);

        Row(vm, "memory-trim").IsOn = true;          // the list's switch is the setting
        Row(vm, "power-plan").IsOn = true;
        Assert.True(vm.OptimizeBeforeLaunch);
        Assert.True(vm.FocusPlayEnabled);
        Assert.Contains("memory-trim", vm.DefaultTweakIds);
        Assert.Contains("power-plan", vm.DefaultTweakIds);

        vm.OptimizeBeforeLaunch = false;             // and the setting is the list's switch
        vm.FocusPlayEnabled = false;
        Assert.False(Row(vm, "memory-trim").IsOn);
        Assert.False(Row(vm, "power-plan").IsOn);

        vm.TurnAllGameTweaksOnCommand.Execute(null);
        Assert.Equal(GameTweakCatalog.All.Count, vm.GameTweaksOnCount);
        Assert.True(vm.OptimizeBeforeLaunch && vm.FocusPlayEnabled);
        await Task.CompletedTask;
    });

    [Fact]
    public void EveryTweakIsInExactlyOneGroup_ByWhatItDoes() => sta.RunAsync(async () =>
    {
        var (vm, _, _, _) = Make();

        var grouped = vm.MemoryAndPowerRows.Concat(vm.WindowsSettingRows).Concat(vm.ServiceRows).Select(r => r.Tweak.Id).ToList();
        Assert.Equal(GameTweakCatalog.All.Count, grouped.Count);
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.All(vm.ServiceRows, r => Assert.True(r.Tweak.NeedsPermission));
        Assert.All(vm.GameTweakRows, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
        await Task.CompletedTask;
    });

    [Fact]
    public void ALaunch_DoesWhatTheListInSettingsSays_ForEveryGame() => sta.RunAsync(async () =>
    {
        var (vm, _, memory, plans) = Make();
        var game = Broken();
        vm.SimulateRefreshResult([game]);

        vm.LaunchCommand.Execute(game);                                    // nothing on: nothing is done
        Assert.Equal(0, memory.Trims);
        Assert.Empty(plans.Switched);

        vm.OptimizeBeforeLaunch = true;
        vm.FocusPlayEnabled = true;
        vm.LaunchCommand.Execute(game);                                    // the list: both
        Assert.Equal(1, memory.Trims);
        Assert.NotEmpty(plans.Switched);
        Assert.True(vm.UsesPowerPlan(game.Id));
        await Task.CompletedTask;
    });

    [Fact]
    public void AListAGameWasGivenEarlier_IsNoLongerUsed() => sta.RunAsync(async () =>
    {
        var (vm, _, memory, plans) = Make();
        var game = Broken();
        vm.SimulateRefreshResult([game]);
        vm.EnsureOverrideForTest(game.Id).GameTweaks = ["memory-trim", "power-plan"]; // saved by an earlier build

        Assert.Equal(vm.DefaultTweakIds, vm.TweaksForGame(game.Id));       // the list in Settings decides, not what is saved on the game
        Assert.False(vm.UsesPowerPlan(game.Id));

        vm.LaunchCommand.Execute(game);

        Assert.Equal(0, memory.Trims);
        Assert.Empty(plans.Switched);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheMemoryRule_StillSkipsATrimWhenPlentyIsFree_EvenWhenAskedFor() => sta.RunAsync(async () =>
    {
        var (vm, _, memory, _) = Make(available: 12 * Gb);                 // 75% free
        var game = Broken();
        vm.SimulateRefreshResult([game]);
        vm.OptimizeBeforeLaunch = true;

        vm.LaunchCommand.Execute(game);

        Assert.Equal(0, memory.Trims);
        await Task.CompletedTask;
    });

    [Fact]
    public void EachRow_ShowsWhatWindowsHasRightNow_NotOnlyWhatWillBeApplied() => sta.RunAsync(async () =>
    {
        var (vm, registry, _, plans) = Make(available: 12 * Gb);
        registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")] = "dword:1";   // Game Mode is on, as on the user's gaming PC
        plans.Active = FocusPlayService.HighPerformance;

        Assert.False(Row(vm, "game-mode").IsOn);                           // the switch is "apply at launch": off
        await vm.RefreshTweakStatesAsync();

        var gameMode = Row(vm, "game-mode");
        Assert.False(gameMode.IsOn);                                       // still not asked for...
        Assert.True(gameMode.AlreadyApplied);                              // ...and Windows already has it on, said so
        Assert.Contains("Already on", gameMode.WindowsState);
        Assert.True(gameMode.ShownOn);                                     // the switch SHOWS on: it is on while a game runs
        Assert.False(gameMode.CanToggle);                                  // and there is nothing to choose, so it does not move
        Assert.Contains("plenty", Row(vm, "memory-trim").WindowsState);
        Assert.Contains("Already on a high performance", Row(vm, "power-plan").WindowsState);
        Assert.True(Row(vm, "power-plan").ShownOn);
        Assert.Contains("already on in Windows", vm.GameTweaksSummary);
        Assert.All(vm.GameTweakRows.Where(r => r.Tweak.Id != "gpu-high"), r => Assert.True(r.HasWindowsState, r.Tweak.Id));
    });

    [Fact]
    public void ThePowerPlanRow_SaysWhyItCannotBeSwitchedOn_WhenThisPcCannotSwitchPlans() => sta.RunAsync(async () =>
    {
        var (vm, _, _, plans) = Make();
        Assert.True(Row(vm, "power-plan").CanSwitch);                      // until it is known

        vm.PowerPlansService = new NoSwitching();
        await vm.CheckFocusPlayAvailabilityAsync();

        var row = Row(vm, "power-plan");
        Assert.False(row.CanSwitch);
        Assert.Contains("Not available on this PC", row.WindowsState);
        vm.TurnAllGameTweaksOnCommand.Execute(null);
        Assert.False(row.IsOn);                                            // "turn all on" leaves out what cannot work
        Assert.False(vm.FocusPlayEnabled);
        _ = plans;
    });

    private sealed class NoSwitching : IPowerPlans
    {
        public Guid? GetActive() => FocusPlayService.Balanced;
        public bool SetActive(Guid plan) => false;
        public Guid? Duplicate(Guid source, string name) => null;
        public bool Delete(Guid plan) => false;
        public bool IsOnMains() => true;
    }

    [Fact]
    public void TheOptimizePage_IsTabs_StartsOnGameBoost_AndNamesWhatIsInEach() => sta.RunAsync(async () =>
    {
        var (vm, _, _, _) = Make();
        OptimizeViewModel? page = null;
        vm.OptimizeDialogForTest = p => page = p;

        vm.ShowOptimizeCommand.Execute(null);

        Assert.NotNull(page);
        Assert.Equal(OptimizeTab.Boost, page!.Tab);
        Assert.True(page.IsBoostTab);
        Assert.False(page.IsHealthTab || page.IsStartupTab || page.IsCleanUpTab);

        page.IsCleanUpTab = true;
        Assert.Equal(OptimizeTab.CleanUp, page.Tab);
        Assert.False(page.IsBoostTab);

        page.IsStartupTab = true;
        Assert.True(page.IsStartupTab);
        Assert.False(page.IsCleanUpTab);

        Assert.Equal("Startup apps", page.StartupTabText);                 // no programs listed under the test's empty startup list
        await Task.CompletedTask;
    });

    [Fact]
    public void AStartupRow_SaysWhatTheProgramIs_OrThatItsFileDoesNotSay() => sta.RunAsync(async () =>
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var service = new StartupAppsService(new OneEntry("Shell", $"\"{cmd}\" /c exit"));
        var known = new StartupRow(service.List().Single(), service, () => { });
        await known.LoadAboutAsync();
        Assert.Contains("Windows Command Processor", known.About);

        var unknownService = new StartupAppsService(new OneEntry("Mystery", @"C:\definitely\not\here\mystery.exe"));
        var unknown = new StartupRow(unknownService.List().Single(), unknownService, () => { });
        await unknown.LoadAboutAsync();
        Assert.Contains("mystery.exe", unknown.About);                     // never a bare name: it says it does not know, and what to do
    });

    private sealed class OneEntry(string name, string command) : IStartupSource
    {
        public IReadOnlyList<(string Name, string Command)> RunEntries() => [(name, command)];
        public IReadOnlyList<string> FolderEntries() => [];
        public bool? ReadApproved(string area, string name) => null;
        public void WriteApproved(string area, string name, bool enabled) { }
    }
}
