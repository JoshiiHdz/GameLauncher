using System.IO;
using System.Windows;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Button mapping: A and B are fixed, the D-pad and stick always move, and X, Y, Start and the bumpers and triggers can be given one of a short list of jobs, or put back
/// on their defaults. Only the changes are saved.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerMappingTests(WpfStaFixture sta)
{
    // ---- the model ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaults_AreTheOnesTheUserAskedFor()
    {
        var map = new ControllerMap();

        Assert.Equal(PadFunction.Settings, map.Resolve(PadButton.Y)); // Y opens Settings, no longer Favorite
        Assert.Equal(PadFunction.Search, map.Resolve(PadButton.X));
        Assert.Equal(PadFunction.GameMenu, map.Resolve(PadButton.Start));
        Assert.Equal(PadFunction.PreviousTab, map.Resolve(PadButton.LB));
        Assert.Equal(PadFunction.NextTab, map.Resolve(PadButton.RB));
        Assert.Equal(PadFunction.PageUp, map.Resolve(PadButton.LT));
        Assert.Equal(PadFunction.PageDown, map.Resolve(PadButton.RT));
        Assert.True(map.IsAllDefault);
    }

    [Fact]
    public void AAndB_AndTheDpad_CannotBeRemapped()
    {
        var map = new ControllerMap();
        foreach (var fixedButton in new[] { PadButton.Accept, PadButton.Back, PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right })
        {
            Assert.DoesNotContain(fixedButton, ControllerMap.Remappable);
            map.Set(fixedButton, PadFunction.Optimize);
            map.Cycle(fixedButton);
            Assert.Equal(PadFunction.Nothing, map.Resolve(fixedButton)); // never mapped: they are handled as they always are
        }

        Assert.True(map.IsAllDefault);
    }

    [Fact]
    public void TheChoices_AreLimitedToTheFewJobsAsked()
    {
        Assert.Equal([PadFunction.Settings, PadFunction.Library, PadFunction.Optimize, PadFunction.RescanLibrary, PadFunction.Nothing], ControllerMap.Choices);
    }

    [Fact]
    public void PressingAButton_CyclesDefaultThenEachChoice_ThenBackToTheDefault_NeverRepeatingItsOwnJob()
    {
        var map = new ControllerMap();
        var seen = new List<string>();

        for (var i = 0; i < 6; i++)
        {
            seen.Add(map.IsDefault(PadButton.Y) ? "default" : map.Resolve(PadButton.Y).ToString());
            map.Cycle(PadButton.Y);
        }

        // Y's own default job is Settings, so Settings is not offered a second time.
        Assert.Equal(["default", "Library", "Optimize", "RescanLibrary", "Nothing", "default"], seen);
    }

    [Fact]
    public void GivingAButtonItsOwnDefaultJob_IsNotAChange_AndResetForgetsEverything()
    {
        var map = new ControllerMap();
        var changes = 0;
        map.Changed += () => changes++;

        map.Set(PadButton.Y, PadFunction.Settings); // that is already Y's job
        Assert.True(map.IsAllDefault);
        Assert.Equal(0, changes);

        map.Set(PadButton.X, PadFunction.Optimize);
        map.Set(PadButton.RB, PadFunction.Nothing);
        Assert.False(map.IsAllDefault);
        Assert.Equal(PadFunction.Optimize, map.Resolve(PadButton.X));

        map.Reset();

        Assert.True(map.IsAllDefault);
        Assert.Equal(PadFunction.Search, map.Resolve(PadButton.X));
        Assert.Equal(PadFunction.NextTab, map.Resolve(PadButton.RB));
    }

    [Fact]
    public void OnlyChangesAreSaved_AndBadSavedValuesAreIgnored()
    {
        var map = new ControllerMap();
        map.Set(PadButton.X, PadFunction.RescanLibrary);
        Assert.Equal(new Dictionary<string, string> { ["X"] = "RescanLibrary" }, map.ToSettings());

        var loaded = new ControllerMap();
        loaded.Load(new Dictionary<string, string>
        {
            ["X"] = "Optimize",
            ["Accept"] = "Settings",   // A cannot be remapped
            ["Y"] = "NotAJob",
            ["Nonsense"] = "Library",
            ["LT"] = "Search",         // a default job of another button is not one of the choices
            ["Start"] = "Settings",
        });

        Assert.Equal(PadFunction.Optimize, loaded.Resolve(PadButton.X));
        Assert.Equal(PadFunction.GameMenu, loaded.Resolve(PadButton.Start)); // refused: Y still has Settings, and a job is on one button only
        Assert.Equal(PadFunction.Settings, loaded.Resolve(PadButton.Y));   // untouched: still its default
        Assert.Equal(PadFunction.PageUp, loaded.Resolve(PadButton.LT));
        Assert.Equal(PadFunction.Nothing, loaded.Resolve(PadButton.Accept));
    }

    // ---- two buttons never have the same job -----------------------------------------------------------------------------------

    private static List<PadFunction> InUse(ControllerMap map) =>
        ControllerMap.Remappable.Select(map.Resolve).Where(f => f != PadFunction.Nothing).ToList();

    [Fact]
    public void ABusyJob_IsRefused_AndNothingChanges()
    {
        var map = new ControllerMap();
        Assert.True(map.Set(PadButton.X, PadFunction.Library));

        var changes = 0;
        map.Changed += () => changes++;
        Assert.False(map.Set(PadButton.LB, PadFunction.Library)); // X has it

        Assert.Equal(PadFunction.PreviousTab, map.Resolve(PadButton.LB)); // LB is as it was
        Assert.Equal(PadFunction.Library, map.Resolve(PadButton.X));
        Assert.Equal(0, changes);
        Assert.Equal(PadButton.X, map.HolderOf(PadFunction.Library));
        Assert.True(map.IsTakenByAnother(PadButton.LB, PadFunction.Library));
        Assert.False(map.IsTakenByAnother(PadButton.X, PadFunction.Library)); // it is its own
    }

    [Fact]
    public void AButtonsDefaultJob_IsBusyToo_WhileItHasIt_AndFreeOnceItMovesAway()
    {
        var map = new ControllerMap();
        Assert.False(map.Set(PadButton.X, PadFunction.Settings));  // Y has Settings by default

        Assert.True(map.Set(PadButton.Y, PadFunction.Optimize));   // Y moves off it ...
        Assert.True(map.Set(PadButton.X, PadFunction.Settings));   // ... and now X can have it

        Assert.False(map.Set(PadButton.Y, null));                  // Y cannot go back to its default while X has it
        Assert.Equal(PadFunction.Optimize, map.Resolve(PadButton.Y));
        Assert.DoesNotContain(null, map.AvailableOptions(PadButton.Y)); // the default is not on offer

        Assert.True(map.Set(PadButton.X, null));                   // X lets go (its own default is Search) ...
        Assert.True(map.Set(PadButton.Y, null));                   // ... and Y can have its default again
        Assert.Equal(PadFunction.Settings, map.Resolve(PadButton.Y));
    }

    [Fact]
    public void NothingIsNotAJob_ManyButtonsCanHaveIt()
    {
        var map = new ControllerMap();
        foreach (var button in new[] { PadButton.X, PadButton.LB, PadButton.RT })
            Assert.True(map.Set(button, PadFunction.Nothing));

        Assert.Equal(3, ControllerMap.Remappable.Count(b => map.Resolve(b) == PadFunction.Nothing));
        Assert.Null(map.HolderOf(PadFunction.Nothing));
    }

    [Fact]
    public void PressingARow_SkipsAJobAnotherButtonHas_AndComesBackToIt_WhenThatButtonLetsGo()
    {
        var map = new ControllerMap();
        map.Set(PadButton.X, PadFunction.Library);
        map.Set(PadButton.LB, PadFunction.Optimize);

        // Start: default (game menu), then the jobs nobody has, then Nothing - and round again. Library and Optimize are not offered.
        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            seen.Add(map.IsDefault(PadButton.Start) ? "default" : map.Resolve(PadButton.Start).ToString());
            map.Cycle(PadButton.Start);
        }

        Assert.Equal(["default", "RescanLibrary", "Nothing", "default", "RescanLibrary", "Nothing"], seen);

        map.Set(PadButton.X, null); // X lets Library go
        Assert.Contains((PadFunction?)PadFunction.Library, map.AvailableOptions(PadButton.Start));
    }

    [Fact]
    public void NoWayOfPressingTheRows_EverGivesTwoButtonsTheSameJob()
    {
        var map = new ControllerMap();
        var random = new Random(7);
        for (var step = 0; step < 2000; step++)
        {
            var button = ControllerMap.Remappable[random.Next(ControllerMap.Remappable.Count)];
            switch (random.Next(4))
            {
                case 0:
                case 1:
                    map.Cycle(button);
                    break;
                case 2:
                    map.Set(button, ControllerMap.Choices[random.Next(ControllerMap.Choices.Count)]);
                    break;
                default:
                    map.Set(button, null);
                    break;
            }

            var used = InUse(map);
            Assert.True(used.Count == used.Distinct().Count(), $"step {step}: two buttons share a job: {string.Join(", ", ControllerMap.Remappable.Select(b => $"{b}={map.Resolve(b)}"))}");
        }

        map.Reset();
        var defaults = InUse(map);
        Assert.Equal(defaults.Count, defaults.Distinct().Count()); // and the defaults themselves are all different
    }

    [Fact]
    public void ASavedFile_WithTwoButtonsOnOneJob_KeepsTheFirstAndDropsTheRest()
    {
        var map = new ControllerMap();
        map.Load(new Dictionary<string, string>
        {
            ["X"] = "Library",
            ["LB"] = "Library",       // the same job: refused
            ["LT"] = "Optimize",      // LT comes before RT on the pad, so it keeps the job
            ["RT"] = "Optimize",      // refused: LT has it
            ["RB"] = "Nothing",
            ["Start"] = "Nothing",    // Nothing can be shared
        });

        Assert.Equal(PadFunction.Library, map.Resolve(PadButton.X));
        Assert.Equal(PadFunction.PreviousTab, map.Resolve(PadButton.LB)); // back on its default
        Assert.Equal(PadFunction.Optimize, map.Resolve(PadButton.LT));
        Assert.Equal(PadFunction.PageDown, map.Resolve(PadButton.RT)); // refused: back on its default
        Assert.Equal(PadFunction.Nothing, map.Resolve(PadButton.RB));
        Assert.Equal(PadFunction.Nothing, map.Resolve(PadButton.Start));
        var used = InUse(map);
        Assert.Equal(used.Count, used.Distinct().Count());
    }

    [Fact]
    public void InSettings_ARowThatCannotTakeABusyJob_SimplyMovesOnToTheNextOne() => sta.RunAsync(async () =>
    {
        var (library, _) = NewLibrary();
        var x = library.ControllerButtonRows.Single(r => r.Button == PadButton.X);
        var lb = library.ControllerButtonRows.Single(r => r.Button == PadButton.LB);

        library.CycleControllerButtonCommand.Execute(x);   // X: Library (Y has Settings, which is not offered to X)
        Assert.Equal("Library", x.FunctionText);

        library.CycleControllerButtonCommand.Execute(lb);  // LB: default -> the first job nobody has
        Assert.Equal("Optimize", lb.FunctionText);          // Library is X's, so it is skipped
        await Task.CompletedTask;
    });

    // ---- saved with the settings ---------------------------------------------------------------------------------

    private static (LibraryViewModel Library, string Directory) NewLibrary(string? directory = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), "GameLauncherTests-Mapping-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        return (vm, directory);
    }

    [Fact]
    public void TheMapping_IsSaved_AndComesBackAfterARestart() => sta.RunAsync(async () =>
    {
        var (library, directory) = NewLibrary();
        library.ControllerMap.Set(PadButton.X, PadFunction.Optimize);
        library.ControllerMap.Set(PadButton.LB, PadFunction.Nothing);

        var (again, _) = NewLibrary(directory);

        Assert.Equal(PadFunction.Optimize, again.ControllerMap.Resolve(PadButton.X));
        Assert.Equal(PadFunction.Nothing, again.ControllerMap.Resolve(PadButton.LB));
        Assert.Equal(PadFunction.NextTab, again.ControllerMap.Resolve(PadButton.RB));
        Assert.Contains(again.ControllerButtonRows, r => r.Button == PadButton.X && r.FunctionText == "Optimize" && r.IsCustom);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheRows_ShowDefaultsAndChoices_AndTheButtonsCycleAndReset() => sta.RunAsync(async () =>
    {
        var (library, _) = NewLibrary();
        Assert.Equal(["X", "Y", "Start", "LB", "RB", "LT", "RT"], library.ControllerButtonRows.Select(r => r.ButtonName));
        var y = library.ControllerButtonRows.Single(r => r.Button == PadButton.Y);
        Assert.Equal("Default · Settings", y.FunctionText);
        Assert.False(library.ResetControllerButtonsCommand.CanExecute(null)); // nothing to reset yet

        library.CycleControllerButtonCommand.Execute(y);
        Assert.Equal("Library", y.FunctionText);
        Assert.True(library.ResetControllerButtonsCommand.CanExecute(null));

        library.ResetControllerButtonsCommand.Execute(null);
        Assert.Equal("Default · Settings", y.FunctionText);
        Assert.True(library.ControllerMap.IsAllDefault);
        Assert.False(library.ResetControllerButtonsCommand.CanExecute(null));
        await Task.CompletedTask;
    });

    [Fact]
    public void ResetAllSettings_AlsoPutsTheButtonsBack() => sta.RunAsync(async () =>
    {
        var (library, directory) = NewLibrary();
        library.ControllerMap.Set(PadButton.X, PadFunction.Optimize);

        library.ResetSettingsCore();

        Assert.True(library.ControllerMap.IsAllDefault);
        Assert.Empty(new SettingsService(directory).Load().ControllerButtons);
        await Task.CompletedTask;
    });

    // ---- the router follows the mapping ----------------------------------------------------------------------------

    private sealed class FakeSurface : IControllerSurface
    {
        public readonly List<string> Calls = new();
        public bool GameTileHasFocus => true;
        public bool HeroHasFocus => false;
        public bool TopBarHasFocus => false;
        public bool KeyboardOpen => false;
        public void KeyboardAction(PadKeyboardAction action) { }
        public void CloseKeyboard() { }
        public bool TypeIntoPalette() => false;
        public bool FocusTopBar() => true;
        public bool TryFocusGameTile() => true;
        public bool TryFocusLetterRail() => false;
        public bool MoveFocus(ShellDirection direction) { Calls.Add("move " + direction); return true; }
        public bool Activate() { Calls.Add("activate"); return true; }
        public void FocusGameTile() => Calls.Add("focus tile");
        public bool FocusPlay() { Calls.Add("focus play"); return true; }
        public bool EnsureFocus() => false;
        public void Scroll(int pages) => Calls.Add("scroll " + pages);
        public void CloseModal() => Calls.Add("close modal");
        public Rect FocusedAnchor() => new(0, 0, 1, 1);
    }

    private static (LibraryViewModel Library, ShellState Shell, FakeSurface Surface, ControllerRouter Router) Create()
    {
        var (library, _) = NewLibrary();
        library.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" }]);
        library.SearchText = "x"; // runs the production filter
        library.SearchText = "";
        library.AppearanceTheme = ThemeId.ConsoleRibbon;
        var shell = new ShellState(library);
        var surface = new FakeSurface();
        return (library, shell, surface, new ControllerRouter(library, shell, surface));
    }

    private void InRibbon(Func<Task> body) => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try { await body(); }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void ARemappedButton_DoesItsNewJob_AndItsOldJobIsGone() => InRibbon(async () =>
    {
        var (library, shell, surface, router) = Create();
        library.EnterControllerMode();
        var optimizeOpened = 0;
        library.OptimizeDialogForTest = _ => optimizeOpened++; // the Optimize page is not built for real here
        library.ControllerMap.Set(PadButton.LB, PadFunction.Optimize);  // LB used to be "previous tab"
        library.ControllerMap.Set(PadButton.RT, PadFunction.Nothing);   // RT used to page down

        router.Handle(PadButton.LB);
        Assert.Equal(1, optimizeOpened);                        // the job of the new button

        router.Handle(PadButton.LB);
        Assert.Equal(2, optimizeOpened);                        // and not "previous tab" any more
        Assert.False(library.IsPageOpen);
        router.Handle(PadButton.RT);
        Assert.DoesNotContain("scroll 1", surface.Calls);       // nothing happens

        router.Handle(PadButton.RB);                            // an untouched button keeps its job
        Assert.Equal(ShellTab.Library, shell.Tab);
        await Task.CompletedTask;
    });

    [Fact]
    public void SettingsLibraryAndOptimize_EachRunTheirOwnThing() => InRibbon(async () =>
    {
        var (library, shell, _, router) = Create();
        library.EnterControllerMode();

        router.Handle(PadButton.Y); // Settings is Y's own job; another button cannot also have it
        Assert.True(library.IsPageOpen);
        library.ClosePage();

        library.ControllerMap.Set(PadButton.X, PadFunction.Library);
        router.Handle(PadButton.X);
        Assert.Equal(ShellTab.Library, shell.Tab);
        shell.ShowHomeCommand.Execute(null);

        // Rescan library is a job on the list too (it is not run here: that would scan this PC for real).
        library.ControllerMap.Set(PadButton.X, PadFunction.RescanLibrary);
        Assert.Equal(PadFunction.RescanLibrary, library.ControllerMap.Resolve(PadButton.X));
        await Task.CompletedTask;
    });

    [Fact]
    public void AAndB_DoTheirFixedJobs_WhateverElseIsMapped() => InRibbon(async () =>
    {
        var (library, shell, surface, router) = Create();
        library.EnterControllerMode();
        foreach (var button in ControllerMap.Remappable)
            library.ControllerMap.Set(button, PadFunction.Nothing);

        router.Handle(PadButton.Accept);
        Assert.Contains("activate", surface.Calls);

        router.Handle(PadButton.RB); // now nothing: the Library tab is not reachable that way
        Assert.Equal(ShellTab.Home, shell.Tab);
        library.ShowStatsCommand.Execute(null);
        router.Handle(PadButton.Back);
        Assert.False(library.IsPageOpen); // B still goes back
        await Task.CompletedTask;
    });

    [Fact]
    public void Start_AlwaysTurnsControllerModeOn_EvenIfItIsMappedToSomethingElse() => InRibbon(async () =>
    {
        var (library, _, _, router) = Create();
        library.ControllerMap.Set(PadButton.Start, PadFunction.Optimize);
        Assert.False(library.IsControllerMode);

        router.Handle(PadButton.Start);

        Assert.True(library.IsControllerMode);
        Assert.False(library.IsPageOpen); // turning the mode on, not running Optimize
        await Task.CompletedTask;
    });

    [Fact]
    public void TheHints_NameWhatEachButtonDoesNow() => InRibbon(async () =>
    {
        var (library, shell, _, _) = Create();
        library.SetControllerStatus(true, null); // a controller is there
        library.EnterControllerMode();
        Assert.Contains("Y  Settings", shell.PadHints);
        Assert.Contains("X  Search", shell.PadHints);
        Assert.Contains("RB  Library", shell.PadHints);

        library.ControllerMap.Set(PadButton.Y, PadFunction.Optimize);
        library.ControllerMap.Set(PadButton.X, PadFunction.Nothing);

        Assert.Contains("Y  Optimize", shell.PadHints);
        Assert.DoesNotContain("Search", shell.PadHints);

        library.ShowStatsCommand.Execute(null);
        library.ControllerMap.Set(PadButton.LT, PadFunction.Nothing);
        Assert.Contains("RT", shell.PadHints); // only the trigger that still pages is named
        Assert.DoesNotContain("LT", shell.PadHints);
        await Task.CompletedTask;
    });

    [Fact]
    public void AHeldButton_RepeatsOnlyWhenItPagesOrMoves()
    {
        var map = new ControllerMap();
        bool Repeats(PadButton b) => b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right || map.Resolve(b) is PadFunction.PageUp or PadFunction.PageDown;

        Assert.True(Repeats(PadButton.LT));
        map.Set(PadButton.LT, PadFunction.Library);
        Assert.False(Repeats(PadButton.LT)); // held, it must not open Settings again and again
        Assert.True(Repeats(PadButton.Down));
    }
}
