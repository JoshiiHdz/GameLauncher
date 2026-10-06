using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

public sealed class FocusPlayTests
{
    private static readonly Guid Ultimate = FocusPlayService.UltimatePerformance;
    private static readonly Guid High = FocusPlayService.HighPerformance;
    private static readonly Guid Balanced = FocusPlayService.Balanced;
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>A PC's power plans without a PC. "Accepts" is what Windows will switch to; a plan it won't switch to is simply refused.</summary>
    private sealed class FakePlans : IPowerPlans
    {
        public Guid? Active { get; set; } = Balanced;
        public HashSet<Guid> Accepts { get; } = new() { Balanced, PowerSaver, High, Ultimate };
        public bool OnMains { get; set; } = true;
        public bool CanDuplicate { get; set; } = true;
        public List<Guid> Switches { get; } = new();
        public List<Guid> Duplicates { get; } = new();

        public Guid? GetActive() => Active;
        public bool IsOnMains() => OnMains;

        public bool SetActive(Guid plan)
        {
            if (!Accepts.Contains(plan))
                return false;

            Active = plan;
            Switches.Add(plan);
            return true;
        }

        public Guid? Duplicate(Guid source, string name)
        {
            if (!CanDuplicate)
                return null;

            var made = Guid.NewGuid();
            Duplicates.Add(made);
            Accepts.Add(made);
            return made;
        }

        public List<Guid> Deleted { get; } = new();

        public bool Delete(Guid plan)
        {
            Deleted.Add(plan);
            Accepts.Remove(plan);
            return true;
        }
    }

    private static (FocusPlayService Service, FakePlans Plans, AppSettings Settings, Func<int> Saves) Build(Action<FakePlans>? arrange = null)
    {
        var plans = new FakePlans();
        arrange?.Invoke(plans);
        var settings = new AppSettings();
        var saves = 0;
        return (new FocusPlayService(plans, settings, () => saves++), plans, settings, () => saves);
    }

    [Fact]
    public void Begin_PrefersUltimatePerformance_AndEndPutsTheOriginalPlanBack()
    {
        var (service, plans, _, _) = Build();

        var result = service.Begin(onlyWhenPluggedIn: true);

        Assert.Equal(FocusPlayOutcome.Switched, result.Outcome);
        Assert.Equal(Ultimate, plans.Active);
        Assert.True(service.IsActive);

        service.End();

        Assert.Equal(Balanced, plans.Active);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void Begin_FallsBackToHighPerformance_WhenWindowsRefusesUltimate()
    {
        var (service, plans, _, _) = Build(p => p.Accepts.Remove(Ultimate));

        Assert.Equal(FocusPlayOutcome.Switched, service.Begin(true).Outcome);
        Assert.Equal(High, plans.Active);
        Assert.Empty(plans.Duplicates);
    }

    [Fact]
    public void Begin_MakesItsOwnPlanOnce_WhenNoBuiltInPlanCanBeSwitchedTo_AndReusesItAfter()
    {
        var (service, plans, settings, _) = Build(p =>
        {
            p.Accepts.Remove(Ultimate);
            p.Accepts.Remove(High);
        });

        Assert.Equal(FocusPlayOutcome.Switched, service.Begin(true).Outcome);
        var own = Assert.Single(plans.Duplicates);
        Assert.Equal(own, plans.Active);
        Assert.Equal(own.ToString(), settings.FocusPlayOwnPlan);
        service.End();

        // The second game must reuse the plan, not make another one - even though Windows' own list can hide it.
        Assert.Equal(FocusPlayOutcome.Switched, service.Begin(true).Outcome);
        Assert.Single(plans.Duplicates);
        Assert.Equal(own, plans.Active);
    }

    [Fact]
    public void Begin_ReportsNotAvailable_AndChangesNothing_WhenNothingCanBeSwitchedToOrMade()
    {
        var (service, plans, settings, _) = Build(p =>
        {
            p.Accepts.Remove(Ultimate);
            p.Accepts.Remove(High);
            p.CanDuplicate = false;
        });

        var result = service.Begin(true);

        Assert.Equal(FocusPlayOutcome.NotAvailable, result.Outcome);
        Assert.NotEmpty(result.Message);
        Assert.Equal(Balanced, plans.Active);
        Assert.False(service.IsActive);
        Assert.Null(settings.FocusPlayRestorePlan);
    }

    [Fact]
    public void Begin_ReportsNotAvailable_WhenWindowsWillNotSayWhichPlanIsInUse()
    {
        var (service, plans, _, _) = Build(p => p.Active = null);

        Assert.Equal(FocusPlayOutcome.NotAvailable, service.Begin(true).Outcome);
        Assert.Empty(plans.Switches);
    }

    [Fact]
    public void Begin_OnBattery_IsSkipped_OnlyWhenTheSettingSaysSo()
    {
        var (service, plans, _, _) = Build(p => p.OnMains = false);

        Assert.Equal(FocusPlayOutcome.SkippedOnBattery, service.Begin(onlyWhenPluggedIn: true).Outcome);
        Assert.Empty(plans.Switches);
        Assert.False(service.IsActive);

        Assert.Equal(FocusPlayOutcome.Switched, service.Begin(onlyWhenPluggedIn: false).Outcome);
        Assert.Equal(Ultimate, plans.Active);
    }

    [Theory]
    [InlineData("e9a42b02-d5df-448d-aa00-03f14749eb61")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    public void Begin_ChangesNothing_WhenThePcIsAlreadyOnAHighPerformancePlan(string already)
    {
        var (service, plans, _, _) = Build(p => p.Active = Guid.Parse(already));

        Assert.Equal(FocusPlayOutcome.AlreadyFast, service.Begin(true).Outcome);
        Assert.Empty(plans.Switches);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void Begin_ChangesNothing_WhenThePcIsAlreadyOnThePlanItMadeEarlier()
    {
        var own = Guid.NewGuid();
        var (service, plans, settings, _) = Build(p => p.Active = own);
        settings.FocusPlayOwnPlan = own.ToString();

        Assert.Equal(FocusPlayOutcome.AlreadyFast, service.Begin(true).Outcome);
        Assert.Empty(plans.Switches);
    }

    [Fact]
    public void Begin_WhileASwitchIsStillInForce_KeepsTheOriginalPlanToGoBackTo()
    {
        var (service, plans, _, _) = Build();
        service.Begin(true);

        Assert.Equal(FocusPlayOutcome.AlreadyActive, service.Begin(true).Outcome);

        service.End();
        Assert.Equal(Balanced, plans.Active); // not Ultimate: the second Begin must not have recorded Ultimate as "the original"
    }

    [Fact]
    public void End_NeverOverwritesAPlanThePersonChoseThemselvesMeanwhile()
    {
        var (service, plans, _, _) = Build();
        service.Begin(true);
        plans.Active = PowerSaver; // chosen by hand while the game ran

        service.End();

        Assert.Equal(PowerSaver, plans.Active);
        Assert.False(service.IsActive); // and the record is cleared, so the next game starts clean
    }

    [Fact]
    public void End_WhenNothingWasSwitched_DoesNothing()
    {
        var (service, plans, _, saves) = Build();

        service.End();

        Assert.Empty(plans.Switches);
        Assert.Equal(0, saves());
    }

    [Fact]
    public void End_WhenTheOriginalPlanWasDeletedMeanwhile_FallsBackToBalanced_InsteadOfStayingOnTheFastPlan()
    {
        var custom = Guid.NewGuid();
        var (service, plans, _, _) = Build(p =>
        {
            p.Accepts.Add(custom);
            p.Active = custom;
        });
        service.Begin(true);
        plans.Accepts.Remove(custom); // deleted while the game ran

        service.End();

        Assert.Equal(Balanced, plans.Active);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void End_WhenWindowsRefusesToSwitchBack_KeepsTheRecord_SoTheNextStartTriesAgain()
    {
        var (service, plans, _, _) = Build();
        service.Begin(true);
        plans.Accepts.Remove(Balanced);

        service.End();

        Assert.Equal(Ultimate, plans.Active);
        Assert.True(service.IsActive);

        plans.Accepts.Add(Balanced);
        service.End();

        Assert.Equal(Balanced, plans.Active);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void ACrashLeavesTheRecordOnDisk_AndTheNextStartPutsThePlanBack()
    {
        var plans = new FakePlans();
        var settings = new AppSettings();
        new FocusPlayService(plans, settings, () => { }).Begin(true);
        Assert.Equal(Ultimate, plans.Active);

        // The app dies here. A new run builds a new service from the saved settings alone.
        var nextRun = new FocusPlayService(plans, settings, () => { });
        Assert.True(nextRun.IsActive);
        nextRun.End();

        Assert.Equal(Balanced, plans.Active);
        Assert.False(nextRun.IsActive);
    }

    [Fact]
    public void TheRestorePlan_IsSavedBeforeTheSwitch()
    {
        var plans = new FakePlans();
        var settings = new AppSettings();
        string? recordedWhenSwitching = null;
        var spy = new SpyPlans(plans, () => recordedWhenSwitching = settings.FocusPlayRestorePlan);

        new FocusPlayService(spy, settings, () => { }).Begin(true);

        Assert.Equal(Balanced.ToString(), recordedWhenSwitching);
    }

    private sealed class SpyPlans : IPowerPlans
    {
        private readonly FakePlans _inner;
        private readonly Action _onSwitch;

        public SpyPlans(FakePlans inner, Action onSwitch)
        {
            _inner = inner;
            _onSwitch = onSwitch;
        }

        public Guid? GetActive() => _inner.GetActive();
        public bool IsOnMains() => _inner.IsOnMains();
        public Guid? Duplicate(Guid source, string name) => _inner.Duplicate(source, name);
        public bool Delete(Guid plan) => _inner.Delete(plan);

        public bool SetActive(Guid plan)
        {
            _onSwitch();
            return _inner.SetActive(plan);
        }
    }

    // ---- Is Focus play possible here? Found out by trying, and nothing is left changed ----

    [Fact]
    public void CheckAvailability_OnAPcThatCanSwitch_SaysYes_AndLeavesThePlanAsItWas()
    {
        var (service, plans, settings, _) = Build();

        var result = service.CheckAvailability();

        Assert.True(result.Available);
        Assert.Equal(Balanced, plans.Active);
        Assert.False(service.IsActive);
        Assert.Null(settings.FocusPlayRestorePlan);
        Assert.Empty(plans.Duplicates);
    }

    [Fact]
    public void CheckAvailability_WhenOnlyAMadePlanWorks_MakesItJustToTest_ThenDeletesIt()
    {
        var (service, plans, settings, _) = Build(p =>
        {
            p.Accepts.Remove(Ultimate);
            p.Accepts.Remove(High);
        });

        var result = service.CheckAvailability();

        Assert.True(result.Available);
        Assert.Equal(Balanced, plans.Active);
        var made = Assert.Single(plans.Duplicates);
        Assert.Equal(new[] { made }, plans.Deleted); // the test plan is gone again
        Assert.Null(settings.FocusPlayOwnPlan);      // and was never kept
        Assert.False(service.IsActive);
    }

    [Fact]
    public void CheckAvailability_WhenNothingCanBeSwitchedToOrMade_SaysNo_WithAReason_AndChangesNothing()
    {
        var (service, plans, settings, _) = Build(p =>
        {
            p.Accepts.Remove(Ultimate);
            p.Accepts.Remove(High);
            p.CanDuplicate = false;
        });

        var result = service.CheckAvailability();

        Assert.False(result.Available);
        Assert.Contains("no high performance power plan", result.Message);
        Assert.Equal(Balanced, plans.Active);
        Assert.Empty(plans.Switches);
        Assert.False(service.IsActive);
        Assert.Null(settings.FocusPlayRestorePlan);
    }

    [Fact]
    public void CheckAvailability_WhenWindowsWontSayWhichPlanIsInUse_SaysNo()
    {
        var (service, _, _, _) = Build(p => p.Active = null);

        Assert.False(service.CheckAvailability().Available);
    }

    [Fact]
    public void CheckAvailability_WhenAlreadyOnAFastPlan_SaysYes_WithoutSwitchingAnything()
    {
        var (service, plans, _, _) = Build(p => p.Active = High);

        Assert.True(service.CheckAvailability().Available);
        Assert.Empty(plans.Switches);
    }

    [Fact]
    public void CheckAvailability_WhileAGameHasTheFastPlanOn_SaysYes_AndDoesNotDisturbIt()
    {
        var (service, plans, _, _) = Build();
        service.Begin(true);

        Assert.True(service.CheckAvailability().Available);
        Assert.Equal(Ultimate, plans.Active);
        Assert.True(service.IsActive);
    }

    [Fact]
    public void CheckAvailability_SavesTheWayBackBeforeSwitching_SoACrashMidCheckIsUndoneAtTheNextStart()
    {
        var plans = new FakePlans();
        var settings = new AppSettings();
        string? recorded = null;
        var spy = new SpyPlans(plans, () => recorded ??= settings.FocusPlayRestorePlan);

        new FocusPlayService(spy, settings, () => { }).CheckAvailability();

        Assert.Equal(Balanced.ToString(), recorded);
    }

    [Fact]
    public void ThePretendPlansUsedByEveryTest_SoNoTestTouchesARealPc()
    {
        // The module initializer in TestSupport installs these; a view model built here must not be using the real thing.
        Assert.IsNotType<PowerPlans>(((Func<IPowerPlans>)GameLauncher.ViewModels.LibraryViewModel.PowerPlansFactory)());
    }

    [Fact]
    public void GamePriority_CountsOnlyTheProcessesThatAcceptedIt_AndNeverThrows()
    {
        var before = GamePriority.RaiseForTest;
        try
        {
            GamePriority.RaiseForTest = id => id % 2 == 0;

            Assert.Equal(2, GamePriority.Raise(new[] { 2, 3, 4, 5 }));
            Assert.Equal(0, GamePriority.Raise(Array.Empty<int>()));
        }
        finally
        {
            GamePriority.RaiseForTest = before;
        }
    }

    [Fact]
    public void GamePriority_ARealProcessThatDoesNotExist_IsReportedAsRefused_NotAsAnError()
    {
        Assert.Equal(0, GamePriority.Raise(new[] { int.MaxValue }));
    }

    [Fact]
    public void GameOverride_RaiseGamePriority_IsOffByDefault_AndSurvivesSavingAndLoading()
    {
        Assert.False(new GameOverride().RaiseGamePriority);

        var json = System.Text.Json.JsonSerializer.Serialize(new GameOverride { RaiseGamePriority = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<GameOverride>(json)!;

        Assert.True(back.RaiseGamePriority);
    }
}

[Collection(WpfStaCollection.Name)]
public sealed class FocusPlayAvailabilityViewModelTests(WpfStaFixture sta)
{
    private sealed class NoFastPlans : IPowerPlans
    {
        public Guid? GetActive() => FocusPlayService.Balanced;
        public bool SetActive(Guid plan) => plan == FocusPlayService.Balanced;
        public Guid? Duplicate(Guid source, string name) => null;
        public bool Delete(Guid plan) => false;
        public bool IsOnMains() => true;
    }

    private static LibraryViewModel Create(IPowerPlans? plans = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-FocusPlay-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        if (plans is not null)
            vm.PowerPlansService = plans;

        return vm;
    }

    [Fact]
    public void BeforeTheCheckHasRun_TheSwitchIsOffLimits_AndSaysItIsChecking() => sta.RunAsync(async () =>
    {
        var vm = Create();

        Assert.False(vm.FocusPlayAvailable);
        Assert.False(vm.FocusPlayChecked);
        Assert.True(vm.HasFocusPlayStatus);
        Assert.Contains("Checking", vm.FocusPlayStatusText);

        await Task.CompletedTask;
    });

    [Fact]
    public void OnAPcThatCanSwitch_TheCheckEnablesTheSwitch_AndShowsNoWarning() => sta.RunAsync(async () =>
    {
        var vm = Create();

        await vm.CheckFocusPlayAvailabilityAsync();

        Assert.True(vm.FocusPlayChecked);
        Assert.True(vm.FocusPlayAvailable);
        Assert.False(vm.HasFocusPlayStatus);
    });

    [Fact]
    public void OnAPcWithNoHighPerformancePlan_TheSwitchStaysDisabled_WithTheReason() => sta.RunAsync(async () =>
    {
        var vm = Create(new NoFastPlans());

        await vm.CheckFocusPlayAvailabilityAsync();

        Assert.True(vm.FocusPlayChecked);
        Assert.False(vm.FocusPlayAvailable);
        Assert.True(vm.HasFocusPlayStatus);
        Assert.Contains("no high performance power plan", vm.FocusPlayStatusText);
    });

    [Fact]
    public void TheCheckRunsOncePerSession_NoMatterHowOftenSettingsIsOpened() => sta.RunAsync(async () =>
    {
        var vm = Create();

        var first = vm.CheckFocusPlayAvailabilityAsync();
        var second = vm.CheckFocusPlayAvailabilityAsync();
        await first;

        Assert.Same(first, second);
    });
}
