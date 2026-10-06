using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The Optimize page's logic and its place in the library: sizes first, a question before any deletion, memory trimming that
/// protects the running game, and the optional trim before a launch.</summary>
public class OptimizeViewModelTests : IDisposable
{
    private const long Gb = 1L << 30;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private sealed class FakeMemory : IMemoryOptimizer
    {
        public long Available = 4 * Gb;
        public readonly List<IReadOnlyList<string>> Trims = [];
        public Exception? Throws;

        public MemoryStatus GetStatus() => new(16 * Gb, Available);

        public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders)
        {
            Trims.Add(protectedFolders);
            if (Throws is not null)
                throw Throws;
            var before = Available;
            Available += Gb;
            return new MemoryTrimResult(30, 4, before, Available);
        }
    }

    private static CleanCategory Fake(string id, long bytes, int files, bool selected, List<string> log, int skipped = 0, long leftBehind = 0)
    {
        var cleaned = false;
        return new CleanCategory
        {
            Id = id, Name = id, Description = id + " description", SelectedByDefault = selected,
            // Once cleared, what is measured is what could not be cleared - like a real folder.
            Scan = _ => cleaned ? new ScanOutcome(leftBehind, leftBehind > 0 ? 1 : 0) : new ScanOutcome(bytes, files),
            Clean = _ => { log.Add(id); cleaned = true; return new CleanOutcome(bytes - leftBehind, files, skipped); },
            CleanReporting = (_, report) => { log.Add(id); cleaned = true; report?.Invoke((bytes - leftBehind) / 2); report?.Invoke(bytes - leftBehind - (bytes - leftBehind) / 2); return new CleanOutcome(bytes - leftBehind, files, skipped); },
        };
    }

    private static OptimizeViewModel Page(FakeMemory memory, List<CleanCategory> categories, Func<string, bool>? confirm = null,
        IReadOnlyList<string>? protectedFolders = null) =>
        new(memory, categories, protectedFolders ?? [], confirm ?? (_ => true));

    // ---- memory ------------------------------------------------------------------------------------

    [Fact]
    public void ThePage_OpensShowingCurrentMemoryUse()
    {
        var page = Page(new FakeMemory(), []);

        Assert.StartsWith("12 GB in use of 16 GB", page.MemoryText);
        Assert.Equal(0.75, page.MemoryUsedFraction);
        Assert.False(page.HasMemoryResult);
    }

    [Fact]
    public async Task FreeingMemory_TrimsWithTheProtectedFolders_AndReportsWhatChanged()
    {
        var memory = new FakeMemory();
        var page = Page(memory, [], protectedFolders: [@"C:\Games\Apex"]);

        await page.FreeUpMemoryCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\Games\Apex"], Assert.Single(memory.Trims));
        Assert.Contains("Made about 1 GB available by trimming 30 apps' memory", page.MemoryResultText);
        Assert.StartsWith("11 GB in use of 16 GB", page.MemoryText);
        Assert.True(page.HasMemoryResult);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task AFailedTrim_IsReported_NotThrown_AndTheButtonWorksAgain()
    {
        var memory = new FakeMemory { Throws = new InvalidOperationException("boom") };
        var page = Page(memory, []);

        await page.FreeUpMemoryCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't free up memory: boom", page.MemoryResultText);
        Assert.False(page.IsBusy);
        Assert.True(page.FreeUpMemoryCommand.CanExecute(null));
    }

    // ---- storage -----------------------------------------------------------------------------------

    [Fact]
    public async Task Scanning_ShowsEachSize_PreTicksTheSafeOnes_AndDeletesNothing()
    {
        var log = new List<string>();
        var page = Page(new FakeMemory(),
        [
            Fake("temp", 3 * Gb, 1200, true, log),
            Fake("bin", 500_000_000, 1, false, log),
            Fake("empty", 0, 0, true, log),
        ]);
        Assert.Equal("Not scanned yet", page.Items[0].SizeText);

        await page.ScanCommand.ExecuteAsync(null);

        Assert.Equal("3 GB in 1,200 files", page.Items[0].SizeText);
        Assert.Equal("476.8 MB in 1 file", page.Items[1].SizeText);
        Assert.Equal("Nothing to clear", page.Items[2].SizeText);
        Assert.Equal([true, false, true], page.Items.Select(i => i.IsSelected));
        Assert.Equal("3 GB selected to clear.", page.StorageText);
        Assert.Empty(log);
    }

    [Fact]
    public async Task AScanThatFails_IsShownOnItsRow_AndTheOthersStillScan()
    {
        var log = new List<string>();
        var broken = new CleanCategory { Id = "x", Name = "x", Description = "d", Scan = _ => throw new IOException("denied"), Clean = _ => new CleanOutcome(0, 0, 0) };
        var page = Page(new FakeMemory(), [broken, Fake("ok", Gb, 5, true, log)]);

        await page.ScanCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't be scanned", page.Items[0].SizeText);
        Assert.Equal("1 GB in 5 files", page.Items[1].SizeText);
    }

    [Fact]
    public async Task Clearing_AsksFirst_NamingExactlyWhatWillGo_AndDeletesNothingOnNo()
    {
        var log = new List<string>();
        string? asked = null;
        var page = Page(new FakeMemory(), [Fake("temp", Gb, 10, true, log), Fake("bin", Gb, 1, false, log)],
            confirm: q => { asked = q; return false; });
        await page.ScanCommand.ExecuteAsync(null);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Contains("temp (1 GB in 10 files)", asked);
        Assert.DoesNotContain("bin (", asked);
        Assert.Contains("cannot be undone", asked);
        Assert.Empty(log);
        Assert.Equal("Nothing was deleted.", page.StorageResultText);
    }

    [Fact]
    public async Task Clearing_OnYes_ClearsOnlyTheTickedKinds_AndReportsWhatWasFreed()
    {
        var log = new List<string>();
        var page = Page(new FakeMemory(), [Fake("temp", 2 * Gb, 10, true, log, skipped: 3), Fake("bin", Gb, 1, false, log)]);
        await page.ScanCommand.ExecuteAsync(null);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(["temp"], log);
        Assert.Equal("Cleared 2 GB", page.Items[0].SizeText);
        Assert.Equal("1 GB in 1 file", page.Items[1].SizeText);
        Assert.Equal("Freed 2 GB. 3 files were in use or protected and were left alone.", page.StorageResultText);
        Assert.Equal(0, page.Items[0].Bytes);
    }

    [Fact]
    public async Task TheStorageBar_FillsOnScan_DrainsWhileClearing_AndEndsOnWhatIsLeft_LikeTheMemoryBar()
    {
        var log = new List<string>();
        // 4 GB of temp files, 1 GB of which is in use and stays; 2 GB of shader cache that goes entirely.
        var page = Page(new FakeMemory(), [Fake("temp", 4 * Gb, 20, true, log, skipped: 2, leftBehind: Gb), Fake("shaders", 2 * Gb, 5, true, log)]);
        Assert.False(page.HasStorageBar);

        await page.ScanCommand.ExecuteAsync(null);
        Assert.True(page.HasStorageBar);
        Assert.Equal(1, page.StorageBarFraction);                       // all of it is there
        Assert.Equal("6 GB selected to clear.", page.StorageText);

        var lines = new List<string>();
        var bar = new List<double>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizeViewModel.StorageText)) lines.Add(page.StorageText);
            if (e.PropertyName == nameof(OptimizeViewModel.StorageBarFraction)) bar.Add(page.StorageBarFraction);
        };

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(["Measuring what is there...", "Clearing temp... (1 of 2)", "Clearing shaders... (2 of 2)", "Checking what is left...", "1 GB left of 6 GB."], lines);
        Assert.Contains(bar, f => f is > 0.2 and < 0.9);                // it drains in steps, not just full then empty
        Assert.Equal(1.0 / 6.0, page.StorageBarFraction, precision: 3); // and ends on what could not be removed
        Assert.Equal("Freed 5 GB. 2 files were in use or protected and were left alone.", page.StorageResultText);
        Assert.Equal("Cleared 3 GB - 1 GB left (in use)", page.Items[0].SizeText);
        Assert.Equal("Cleared 2 GB", page.Items[1].SizeText);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task WhenEverythingGoes_TheBarEmpties_AndTheLineSaysAllOfItIsCleared()
    {
        var emptied = false;
        var oneShot = new CleanCategory
        {
            Id = "bin", Name = "bin", Description = "d", SelectedByDefault = true,
            Scan = _ => emptied ? new ScanOutcome(0, 0) : new ScanOutcome(2 * Gb, 4),
            Clean = _ => { emptied = true; return new CleanOutcome(2 * Gb, 4, 0); }, // no per-file progress, like the Recycle Bin
        };
        var page = Page(new FakeMemory(), [oneShot]);
        await page.ScanCommand.ExecuteAsync(null);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(0, page.StorageBarFraction);
        Assert.Equal("All 2 GB cleared.", page.StorageText);
    }

    [Fact]
    public async Task SayingNo_LeavesTheBarAsItWas()
    {
        var page = Page(new FakeMemory(), [Fake("temp", Gb, 1, true, [])], confirm: _ => false);
        await page.ScanCommand.ExecuteAsync(null);
        var line = page.StorageText;

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(1, page.StorageBarFraction);
        Assert.Equal(line, page.StorageText);
    }

    [Fact]
    public async Task Clearing_WithNothingTicked_AsksNothingAndDoesNothing()
    {
        var log = new List<string>();
        var asked = false;
        var page = Page(new FakeMemory(), [Fake("temp", Gb, 1, false, log)], confirm: _ => { asked = true; return true; });

        await page.CleanCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.Empty(log);
        Assert.Equal("Tick at least one kind of clutter to clear.", page.StorageResultText);
    }

    [Fact]
    public async Task AKindThatFailsToClear_IsShown_AndTheRestStillRun()
    {
        var log = new List<string>();
        var broken = new CleanCategory { Id = "x", Name = "x", Description = "d", SelectedByDefault = true, Scan = _ => new ScanOutcome(1, 1), Clean = _ => throw new IOException("denied") };
        var page = Page(new FakeMemory(), [broken, Fake("ok", Gb, 5, true, log)]);
        await page.ScanCommand.ExecuteAsync(null);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't be cleared", page.Items[0].SizeText);
        Assert.Equal(["ok"], log);
        Assert.Equal("Freed 1 GB.", page.StorageResultText);
    }

    // ---- Select all ----------------------------------------------------------------------------------

    [Fact]
    public void SelectAll_TicksEveryKind_AndThenReadsSelectNoneAndUntickes()
    {
        var log = new List<string>();
        var page = Page(new FakeMemory(), [Fake("temp", Gb, 10, true, log), Fake("browser", 2 * Gb, 20, false, log), Fake("bin", Gb / 2, 2, false, log)]);
        Assert.Equal("Select all", page.SelectAllText);              // only some are ticked

        page.ToggleSelectAllCommand.Execute(null);

        Assert.All(page.Items, i => Assert.True(i.IsSelected));
        Assert.Equal("Select none", page.SelectAllText);

        page.ToggleSelectAllCommand.Execute(null);

        Assert.All(page.Items, i => Assert.False(i.IsSelected));
        Assert.Equal("Select all", page.SelectAllText);
    }

    [Fact]
    public void TickingTheLastBoxByHand_FlipsTheButtonToSelectNone()
    {
        var log = new List<string>();
        var page = Page(new FakeMemory(), [Fake("a", Gb, 1, true, log), Fake("b", Gb, 1, false, log)]);

        page.Items[1].IsSelected = true;

        Assert.Equal("Select none", page.SelectAllText);
    }

    [Fact]
    public async Task SelectAll_AfterAScan_UpdatesWhatWouldBeCleared_AndClearsExactlyWhatIsTicked()
    {
        var log = new List<string>();
        string? asked = null;
        var page = Page(new FakeMemory(), [Fake("temp", Gb, 10, true, log), Fake("browser", 2 * Gb, 20, false, log)], confirm: q => { asked = q; return true; });
        await page.ScanCommand.ExecuteAsync(null);
        Assert.Equal("1 GB selected to clear.", page.StorageText);

        page.ToggleSelectAllCommand.Execute(null);
        Assert.Equal("3 GB selected to clear.", page.StorageText);

        await page.CleanCommand.ExecuteAsync(null);

        Assert.Equal(["temp", "browser"], log);
        Assert.Contains("browser", asked);
    }

    [Fact]
    public async Task NothingIsTickedForYou_BeyondTheUsualDefaults_AndClearSelectedStillAsksFirst()
    {
        var log = new List<string>();
        var asked = false;
        var page = Page(new FakeMemory(), [Fake("browser", 2 * Gb, 20, false, log)], confirm: _ => { asked = true; return false; });
        await page.ScanCommand.ExecuteAsync(null);

        await page.CleanCommand.ExecuteAsync(null);              // nothing ticked: it says so rather than asking
        Assert.False(asked);
        Assert.Equal("Tick at least one kind of clutter to clear.", page.StorageResultText);

        page.Items[0].IsSelected = true;
        await page.CleanCommand.ExecuteAsync(null);

        Assert.True(asked);
        Assert.Empty(log);                                       // refused: nothing deleted
    }

    // ---- in the library ----------------------------------------------------------------------------

    private LibraryViewModel Library(FakeMemory memory)
    {
        var vm = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            MemoryOptimizerService = memory,
            CleanCategoriesFactory = () => [],
        };
        return vm;
    }

    private static GameEntry Game(string id, string name, string installDir) => new()
    {
        Id = id, Name = name, ExecutablePath = installDir + @"\game.exe", InstallDir = installDir, Source = GameSource.Manual,
    };

    [Fact]
    public void TheOptimizePage_ProtectsTheRunningGamesFolder()
    {
        var memory = new FakeMemory();
        var vm = Library(memory);
        var game = Game("a", "Apex", @"C:\Games\Apex");
        vm.SimulateRefreshResult([game]);
        vm.MarkGameRunning(game);
        OptimizeViewModel? shown = null;
        vm.OptimizeDialogForTest = page => shown = page;

        vm.ShowOptimizeCommand.Execute(null);
        shown!.FreeUpMemoryCommand.Execute(null);

        Assert.NotNull(shown);
        // the trim runs on a worker thread; wait for it
        SpinWait.SpinUntil(() => memory.Trims.Count == 1, 3000);
        Assert.Equal([@"C:\Games\Apex"], Assert.Single(memory.Trims));
    }

    [Fact]
    public void TheOptimizePage_WithNoGameRunning_ProtectsNothing()
    {
        var memory = new FakeMemory();
        var vm = Library(memory);
        vm.SimulateRefreshResult([Game("a", "Apex", @"C:\Games\Apex")]);
        OptimizeViewModel? shown = null;
        vm.OptimizeDialogForTest = page => shown = page;
        vm.ShowOptimizeCommand.Execute(null);

        shown!.FreeUpMemoryCommand.Execute(null);

        SpinWait.SpinUntil(() => memory.Trims.Count == 1, 3000);
        Assert.Empty(Assert.Single(memory.Trims));
    }

    [Fact]
    public void TheBeforeLaunchSetting_IsOffByDefault_AndIsRemembered()
    {
        var vm = Library(new FakeMemory());
        Assert.False(vm.OptimizeBeforeLaunch);

        vm.OptimizeBeforeLaunch = true;

        Assert.True(new SettingsService(_dataDir).Load().OptimizeBeforeLaunch);
        Assert.True(Library(new FakeMemory()).OptimizeBeforeLaunch);
    }

    [Fact]
    public void Launching_TrimsMemoryFirst_OnlyWhenTheSettingIsOn()
    {
        var memory = new FakeMemory();
        var vm = Library(memory);
        var game = Game("a", "Apex", @"C:\Games\Apex");
        vm.SimulateRefreshResult([game]);

        vm.LaunchCommand.Execute(game);
        Assert.Empty(memory.Trims);

        vm.OptimizeBeforeLaunch = true;
        vm.LaunchCommand.Execute(game);
        Assert.Single(memory.Trims);
    }

    [Fact]
    public void ATrimThatFails_NeverStopsTheGameFromLaunching()
    {
        var memory = new FakeMemory { Throws = new InvalidOperationException("boom") };
        var vm = Library(memory);
        var game = Game("a", "Apex", @"C:\Games\Apex");
        vm.SimulateRefreshResult([game]);
        vm.OptimizeBeforeLaunch = true;

        vm.LaunchCommand.Execute(game);

        // The fake exe does not exist, so reaching the launch shows as the launch failure - not as the trim's error.
        Assert.StartsWith("Failed to launch Apex", vm.StatusText);
    }
}
