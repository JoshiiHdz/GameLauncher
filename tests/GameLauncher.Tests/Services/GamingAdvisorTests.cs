using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>The gaming health check and the research-aligned tweak catalogue: advice only where there is something real to say, and nothing that trades security for speed.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class GamingAdvisorTests(WpfStaFixture sta)
{
    private sealed class Facts : IAdvisorFacts
    {
        public (int Current, int Max)? Refresh { get; set; } = (144, 144);
        public int? Hags { get; set; } = 2;
        public bool? Mode { get; set; } = true;
        public bool? Windowed { get; set; } = true;
        public bool? Integrity { get; set; } = true;
        public MemoryStatus Mem { get; set; } = new(32L << 30, 20L << 30);
        public bool? Fast { get; set; } = true;
        public List<string> Gpus { get; set; } = [];

        public (int Current, int Max)? DisplayRefresh() => Refresh;
        public int? HardwareScheduling() => Hags;
        public bool? GameMode() => Mode;
        public bool? WindowedOptimizations() => Windowed;
        public bool? MemoryIntegrity() => Integrity;
        public MemoryStatus Memory() => Mem;
        public bool? HighPerformancePlan() => Fast;
        public IReadOnlyList<string> GpuNames() => Gpus;
    }

    [Fact]
    public void AWellSetUpPc_HasNothingToImprove()
    {
        var findings = GamingAdvisor.Check(new Facts());

        Assert.DoesNotContain(findings, f => f.Level == AdviceLevel.Advice);
        Assert.Contains(findings, f => f.Id == "refresh" && f.Level == AdviceLevel.Good);
    }

    [Fact]
    public void ADisplayBelowItsFastestMode_IsFlagged_WithTheWayToFixIt()
    {
        var findings = GamingAdvisor.Check(new Facts { Refresh = (60, 144) });

        var refresh = findings.Single(f => f.Id == "refresh");
        Assert.Equal(AdviceLevel.Advice, refresh.Level);
        Assert.Contains("60 Hz", refresh.Detail);
        Assert.Contains("144 Hz", refresh.Detail);
        Assert.Contains("Advanced display", refresh.Detail);
    }

    [Fact]
    public void GameModeAndWindowedOptimizations_WhenOff_PointAtTheTweaksThatSwitchThemOn()
    {
        var findings = GamingAdvisor.Check(new Facts { Mode = false, Windowed = false });

        Assert.Equal("game-mode", findings.Single(f => f.Id == "game-mode").TweakId);
        Assert.Equal("windowed-opt", findings.Single(f => f.Id == "windowed").TweakId);
        Assert.All(findings.Where(f => f.TweakId is not null), f => Assert.NotNull(GameTweakCatalog.ById(f.TweakId!)));
    }

    [Fact]
    public void MemoryInUse_IsNotCalledAProblem_OnlyRealShortageIs()
    {
        var plenty = GamingAdvisor.Check(new Facts { Mem = new MemoryStatus(32L << 30, 9L << 30) }); // 72% "in use", 28% free
        Assert.Equal(AdviceLevel.Good, plenty.Single(f => f.Id == "memory").Level);

        var short_ = GamingAdvisor.Check(new Facts { Mem = new MemoryStatus(16L << 30, 1L << 30) });
        var finding = short_.Single(f => f.Id == "memory");
        Assert.Equal(AdviceLevel.Advice, finding.Level);
        Assert.Contains("Close apps", finding.Detail);
    }

    [Fact]
    public void MemoryIntegrityAndHardwareScheduling_AreExplained_NeverTurnedIntoAdviceToSwitchThemOff()
    {
        var findings = GamingAdvisor.Check(new Facts());

        var integrity = findings.Single(f => f.Id == "memory-integrity");
        Assert.Equal(AdviceLevel.Info, integrity.Level);
        Assert.Null(integrity.TweakId);
        Assert.Contains("does not change it", integrity.Detail);
        Assert.Equal(AdviceLevel.Info, findings.Single(f => f.Id == "hags").Level);
        Assert.Null(findings.Single(f => f.Id == "hags").TweakId);
        Assert.Contains("stutter", findings.Single(f => f.Id == "shader-cache").Detail);
    }

    [Fact]
    public void TheFrameCapAndReflexAdvice_NamesTheTargetFromTheDisplay_AndOnlyForCardsThatHaveReflex()
    {
        var withNvidia = GamingAdvisor.Check(new Facts { Refresh = (165, 165), Gpus = ["NVIDIA GeForce RTX 4070"] });
        Assert.Contains("162 fps", withNvidia.Single(f => f.Id == "frame-cap").Detail);   // a few below the refresh rate
        Assert.Contains("Reflex", withNvidia.Single(f => f.Id == "reflex").Detail);

        var amd = GamingAdvisor.Check(new Facts { Refresh = (60, 60), Gpus = ["AMD Radeon RX 7800 XT"] });
        Assert.DoesNotContain(amd, f => f.Id == "reflex");                                 // not offered on a card that has no Reflex
        Assert.DoesNotContain(amd, f => f.Id == "frame-cap");                              // and no cap advice for a 60 Hz display
    }

    [Fact]
    public void WhatCannotBeRead_IsLeftOut_NotGuessed()
    {
        var findings = GamingAdvisor.Check(new Facts { Refresh = null, Hags = null, Mode = null, Windowed = null, Integrity = null, Fast = null, Mem = default });

        Assert.DoesNotContain(findings, f => f.Id is "refresh" or "hags" or "game-mode" or "windowed" or "memory-integrity" or "power" or "memory");
        Assert.Single(findings); // the shader-cache note is always true
    }

    [Fact]
    public void TheCatalogue_FollowsTheResearch_NoErrorReporting_NoWindowsUpdateStop_AdvancedMarked()
    {
        Assert.Null(GameTweakCatalog.ById("svc-errors"));
        var all = GameTweakCatalog.All.SelectMany(t => GameTweakCatalog.ServicesFor(t.Id)).ToList();
        Assert.DoesNotContain("WerSvc", all);        // how a crash is diagnosed
        Assert.DoesNotContain("wuauserv", all);      // Windows Update is paused with Windows' own control, not by stopping its services
        Assert.DoesNotContain("UsoSvc", all);
        Assert.DoesNotContain("BITS", all);
        Assert.Contains("DoSvc", all);               // Delivery Optimization is the one that competes with a game's network
        Assert.True(GameTweakCatalog.StopTelemetry.Advanced);
        Assert.True(GameTweakCatalog.StopSpooler.Advanced);
        Assert.False(GameTweakCatalog.StopSearch.Advanced);
        Assert.NotNull(GameTweakCatalog.ById("windowed-opt"));
    }

    [Fact]
    public void TheWindowedGamesOption_IsMergedIntoTheValueWindowsAlreadyKeeps_AndPutBackExactly()
    {
        var registry = new PretendRegistry();
        var settings = new AppSettings();
        var engine = new GameTweakEngine(registry, settings, () => { });
        var key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        registry.Values[(key, "DirectXUserGlobalSettings")] = "string:VRROptimizeEnable=1;AutoHDREnable=2097;SwapEffectUpgradeEnable=0;";

        engine.Begin(["windowed-opt"], null);

        var during = registry.Values[(key, "DirectXUserGlobalSettings")];
        Assert.Contains("VRROptimizeEnable=1", during);          // the person's other options are kept
        Assert.Contains("AutoHDREnable=2097", during);
        Assert.Contains("SwapEffectUpgradeEnable=1", during);
        Assert.DoesNotContain("SwapEffectUpgradeEnable=0", during);

        engine.End();

        Assert.Equal("string:VRROptimizeEnable=1;AutoHDREnable=2097;SwapEffectUpgradeEnable=0;", registry.Values[(key, "DirectXUserGlobalSettings")]);
    }

    [Fact]
    public void TheWindowedGamesOption_WhenNoValueExisted_IsCreatedAndRemoved()
    {
        var registry = new PretendRegistry();
        var engine = new GameTweakEngine(registry, new AppSettings(), () => { });

        engine.Begin(["windowed-opt"], null);
        Assert.Equal("string:SwapEffectUpgradeEnable=1;", registry.Values[(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings")]);

        engine.End();
        Assert.Empty(registry.Values);
    }

    // ---- the page and the memory rule --------------------------------------------------------------------------------------

    private sealed class FakeMemory(long total, long available) : IMemoryOptimizer
    {
        public int Trims;
        public MemoryStatus GetStatus() => new(total, available);

        public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders)
        {
            Trims++;
            return new MemoryTrimResult(3, 0, available, available + (1L << 30));
        }
    }

    private static LibraryViewModel Library(IMemoryOptimizer memory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Advisor-" + Guid.NewGuid());
        return new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
            MemoryOptimizerService = memory,
        };
    }

    [Fact]
    public void TheBeforeLaunchTrim_IsSkippedWhenMemoryIsPlentiful_AndRunsWhenItIsShort() => sta.RunAsync(async () =>
    {
        var plenty = new FakeMemory(32L << 30, 16L << 30);   // half free
        Library(plenty).FreeMemoryBeforeLaunch();
        Assert.Equal(0, plenty.Trims);

        var short_ = new FakeMemory(16L << 30, 2L << 30);    // an eighth free
        Library(short_).FreeMemoryBeforeLaunch();
        Assert.Equal(1, short_.Trims);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheOptimizePage_ListsTheFindings_AdviceFirst_AndTheButtonSwitchesTheTweakOn() => sta.RunAsync(async () =>
    {
        var previous = LibraryViewModel.AdvisorFactsFactory;
        LibraryViewModel.AdvisorFactsFactory = (_, _) => new Facts { Mode = false, Windowed = true };
        try
        {
            var vm = Library(new FakeMemory(32L << 30, 16L << 30));
            OptimizeViewModel? page = null;
            vm.OptimizeDialogForTest = p => page = p;

            vm.ShowOptimizeCommand.Execute(null);

            Assert.NotNull(page);
            Assert.True(page!.HasAdvisor);
            Assert.Equal("Windows Game Mode is off", page.AdvisorRows[0].Title);       // advice first
            Assert.True(page.AdvisorRows[0].HasAction);
            Assert.Equal(AdviceLevel.Good, page.AdvisorRows[1].Finding.Level);

            page.AdvisorRows[0].ApplyCommand.Execute(null);

            Assert.True(vm.GameTweakRows.Single(r => r.Tweak.Id == "game-mode").IsOn);
            Assert.DoesNotContain(page.AdvisorRows, r => r.HasAction);                  // it is on now: nothing left to press
        }
        finally { LibraryViewModel.AdvisorFactsFactory = previous; }

        await Task.CompletedTask;
    });
}
