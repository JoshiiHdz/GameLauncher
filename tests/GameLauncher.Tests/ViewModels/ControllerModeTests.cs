using System.IO;
using System.Windows;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Controller mode: only in the PlayStation theme, entered from Start or a button, and driven by the router through the shell state's own focus and
/// menu logic. The window is replaced by a fake that records what the router asked of it.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerModeTests(WpfStaFixture sta)
{
    private sealed class FakeSurface : IControllerSurface
    {
        public bool TileFocus = true;
        public bool EnsureResult;
        public readonly List<string> Calls = new();

        public bool GameTileHasFocus => TileFocus;
        public bool HeroFocus;
        public bool TopBarFocus;
        public bool KeyboardIsOpen;
        public bool KeyboardOpen => KeyboardIsOpen;
        public readonly List<PadKeyboardAction> Typed = new();
        public bool PaletteTyped;
        public void KeyboardAction(PadKeyboardAction action) => Typed.Add(action);
        public void CloseKeyboard() { Calls.Add("close keyboard"); KeyboardIsOpen = false; }
        public bool TypeIntoPalette() { Calls.Add("type into palette"); return PaletteTyped; }
        public bool TopBarHasFocus => TopBarFocus;
        public bool FocusTopBar() { Calls.Add("focus top bar"); return true; }
        public bool TryFocusGameTile() { Calls.Add("try tile"); return true; }
        public bool TryFocusLetterRail() { Calls.Add("letter rail"); return true; }
        public bool HeroHasFocus => HeroFocus;
        public bool MoveFocus(ShellDirection direction) { Calls.Add("move " + direction); return true; }
        public bool Activate() { Calls.Add("activate"); return true; }
        public void FocusGameTile() => Calls.Add("focus tile");
        public bool PlayResult = true;
        public bool FocusPlay() { Calls.Add("focus play"); return PlayResult; }
        public bool EnsureFocus() { Calls.Add("ensure"); return EnsureResult; }
        public void Scroll(int pages) => Calls.Add("scroll " + pages);
        public void CloseModal() => Calls.Add("close modal");
        public Rect FocusedAnchor() => new(10, 20, 30, 40);
    }

    private static string _directory = "";

    /// <summary>The theme in the settings file of the library the last <see cref="Create"/> made (the tests of this class run one at a time).</summary>
    private static string SavedTheme() => new SettingsService(_directory).Load().AppearanceTheme;

    private static (LibraryViewModel Library, ShellState Shell, FakeSurface Surface, ControllerRouter Router, List<GameEntry> Games) Create(bool ribbon = true)
    {
        var directory = _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Controller-" + Guid.NewGuid());
        var library = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        var games = new List<GameEntry>
        {
            new() { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new() { Id = "2", Name = "Borderlands", Source = GameSource.Steam, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
            new() { Id = "3", Name = "Celeste", Source = GameSource.Epic, ExecutablePath = @"C:\G\c.exe", InstallDir = @"C:\G\c" },
        };
        library.SimulateRefreshResult(games);
        library.SearchText = "x"; // runs the production filter
        library.SearchText = "";
        if (ribbon)
            library.AppearanceTheme = ThemeId.ConsoleRibbon;

        var shell = new ShellState(library);
        var surface = new FakeSurface();
        // The library keeps its own entries, so the tests use those (not the ones they passed in).
        var held = library.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (library, shell, surface, new ControllerRouter(library, shell, surface), held);
    }

    private void InRibbon(Func<Task> body) => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try { await body(); }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    // ---- availability and entry --------------------------------------------------------------------------------

    [Fact]
    public void Controller_IsOnlyAvailableInThePlayStationTheme() => sta.RunAsync(async () =>
    {
        var (library, _, _, _, _) = Create(ribbon: false);
        Assert.False(library.ControllerModeAvailable);

        library.AppearanceTheme = ThemeId.ConsoleTile;
        Assert.False(library.ControllerModeAvailable);
        Assert.False(library.EnterControllerModeFromAxisCommand.CanExecute(null)); // the Xbox style has no way in, and Axis's button does nothing there

        library.AppearanceTheme = ThemeId.ConsoleRibbon;
        Assert.True(library.ControllerModeAvailable);
        library.EnterControllerMode();
        Assert.True(library.IsControllerMode);
        Assert.False(library.ControllerModeBorrowsTheme); // the user was already in the PlayStation theme: nothing to hand back
        await Task.CompletedTask;
    });

    [Fact]
    public void EnteringFromAxis_PutsThePlayStationThemeOnForTheSession_AndLeavingBringsAxisBack() => sta.RunAsync(async () =>
    {
        var (library, _, _, _, _) = Create(ribbon: false);
        Assert.True(library.EnterControllerModeFromAxisCommand.CanExecute(null));

        library.EnterControllerModeFromAxisCommand.Execute(null);
        Assert.True(library.IsControllerMode);
        Assert.Equal(ThemeId.ConsoleRibbon, library.AppearanceTheme);
        Assert.True(library.ControllerModeBorrowsTheme);
        Assert.Equal(nameof(ThemeId.Axis), SavedTheme()); // never saved: a restart opens Axis
        Assert.False(library.EnterControllerModeFromAxisCommand.CanExecute(null));

        library.ExitControllerMode();
        Assert.False(library.IsControllerMode);
        Assert.Equal(ThemeId.Axis, library.AppearanceTheme);
        Assert.False(library.ControllerModeBorrowsTheme);
        Assert.Equal(nameof(ThemeId.Axis), SavedTheme());
        Assert.True(library.EnterControllerModeFromAxisCommand.CanExecute(null));
        await Task.CompletedTask;
    });

    [Fact]
    public void ChoosingThePlayStationThemeDuringTheSession_KeepsItWhenControllerModeEnds() => sta.RunAsync(async () =>
    {
        var (library, _, _, _, _) = Create(ribbon: false);
        library.EnterControllerModeFromAxisCommand.Execute(null);

        library.ChangeThemeCommand.Execute("ConsoleRibbon"); // the user picks the theme the mode is wearing: it is theirs now
        Assert.False(library.ControllerModeBorrowsTheme);
        Assert.Equal(nameof(ThemeId.ConsoleRibbon), SavedTheme());

        library.ExitControllerMode();
        Assert.Equal(ThemeId.ConsoleRibbon, library.AppearanceTheme);
        await Task.CompletedTask;
    });

    [Fact]
    public void LeavingControllerModeByChoosingAxis_ReturnsToAxisAndSavesIt() => sta.RunAsync(async () =>
    {
        var (library, _, _, _, _) = Create(ribbon: false);
        library.EnterControllerModeFromAxisCommand.Execute(null);

        library.ChangeThemeCommand.Execute("Axis");

        Assert.False(library.IsControllerMode);
        Assert.Equal(ThemeId.Axis, library.AppearanceTheme);
        Assert.False(library.ControllerModeBorrowsTheme);
        await Task.CompletedTask;
    });

    [Fact]
    public void StartInAxis_AsksFirst_AndOnlyAYesSwitches_AndNotWhileADialogIsOpen() => sta.RunAsync(async () =>
    {
        var (library, shell, _, router, _) = Create(ribbon: false);
        var asked = 0;
        router.EnterRequested += () => asked++;

        shell.ModalOpen = true;
        router.Handle(PadButton.Start);
        Assert.Equal(0, asked);
        shell.ModalOpen = false;

        router.Handle(PadButton.Start);
        Assert.Equal(1, asked);
        Assert.False(library.IsControllerMode); // asking is not entering: the window enters on a yes

        router.Handle(PadButton.Accept);
        Assert.Equal(1, asked); // nothing else starts it
        await Task.CompletedTask;
    });

    [Fact]
    public void SwitchingToAnotherTheme_EndsControllerMode() => sta.RunAsync(async () =>
    {
        var (library, _, _, _, _) = Create();
        library.EnterControllerMode();
        Assert.True(library.IsControllerMode);

        library.AppearanceTheme = ThemeId.Axis;

        Assert.False(library.IsControllerMode);
        Assert.False(library.ControllerModeAvailable);
        await Task.CompletedTask;
    });

    [Fact]
    public void AGameRunningInTheBackground_DoesNotStopControllerMode() => sta.RunAsync(async () =>
    {
        // What blocks the pad is not being the window in front (a game that has the screen), never a game merely running somewhere.
        var (library, shell, surface, router, games) = Create();
        library.MarkGameRunning(games[0]);
        Assert.True(library.HasRunningGame);

        router.Handle(PadButton.Start);
        Assert.True(library.IsControllerMode);

        shell.FocusRibbonItem(games[1]);
        router.Handle(PadButton.Right);
        Assert.Contains("focus tile", surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void OutsideTheMode_OnlyStartDoesAnything_AndItTurnsTheModeOn() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();

        foreach (var button in new[] { PadButton.Accept, PadButton.Back, PadButton.Down, PadButton.Y, PadButton.RB, PadButton.X })
            router.Handle(button);

        Assert.Empty(surface.Calls);
        Assert.False(library.IsControllerMode);

        router.Handle(PadButton.Start);

        Assert.True(library.IsControllerMode);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheToggleCommand_TurnsItOnAndOff() => InRibbon(async () =>
    {
        var (library, _, _, _, _) = Create();

        library.ToggleControllerModeCommand.Execute(null);
        Assert.True(library.IsControllerMode);

        library.ToggleControllerModeCommand.Execute(null);
        Assert.False(library.IsControllerMode);
        await Task.CompletedTask;
    });

    // ---- moving ------------------------------------------------------------------------------------------------

    [Fact]
    public void OnATile_TheShellStateMovesTheFocus_AndTheTileTakesKeyboardFocus() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        shell.FocusRibbonItem(games[0]);

        router.Handle(PadButton.Right);

        Assert.Same(games[1], shell.FocusedGame);
        Assert.Equal(["ensure", "focus tile"], surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void DownFromAnyTileOnTheHome_LandsOnPlay_WhicheverGameItIs() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();

        foreach (var tile in new GameEntry?[] { games[0], games[1], games[2], null }) // every game, and the Library tile
        {
            shell.FocusRibbonItem(tile);
            surface.Calls.Clear();

            router.Handle(PadButton.Down);

            Assert.Equal(["ensure", "focus play"], surface.Calls);
        }

        await Task.CompletedTask;
    });

    [Fact]
    public void DownOnTheLibraryTab_StaysInTheGrid_AndDownFromANonTile_UsesWpf() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        router.Handle(PadButton.RB); // the Library tab
        shell.FocusRibbonItem(games[0]);
        surface.Calls.Clear();

        router.Handle(PadButton.Down); // the last row of the grid has nowhere below: WPF decides, not Play
        Assert.DoesNotContain("focus play", surface.Calls);

        surface.TileFocus = false;
        surface.Calls.Clear();
        router.Handle(PadButton.Down);
        Assert.Equal(["ensure", "move Down"], surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void OnAnyOtherControl_WpfMovesTheFocus() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();
        library.EnterControllerMode();
        surface.TileFocus = false;

        router.Handle(PadButton.Left);

        Assert.Equal(["ensure", "move Left"], surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void TheFirstPress_WhenNothingHasFocus_OnlyShowsWhereYouAre() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        shell.FocusRibbonItem(games[0]);
        surface.EnsureResult = true;

        router.Handle(PadButton.Right);
        router.Handle(PadButton.Accept);

        Assert.Same(games[0], shell.FocusedGame);
        Assert.Equal(["ensure", "ensure"], surface.Calls); // neither moved nor pressed anything
        await Task.CompletedTask;
    });

    [Fact]
    public void A_PressesTheFocusedControl_AndTheTriggersPage() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();
        library.EnterControllerMode();

        router.Handle(PadButton.Accept);
        router.Handle(PadButton.RT);
        router.Handle(PadButton.LT);

        Assert.Equal(["ensure", "activate", "scroll 1", "scroll -1"], surface.Calls);
        await Task.CompletedTask;
    });

    // ---- the home buttons -------------------------------------------------------------------------------------

    [Fact]
    public void Y_OpensSettings_ByDefault_AndFavoriteIsNoLongerOnThePad() => InRibbon(async () =>
    {
        var (library, shell, _, router, games) = Create();
        library.EnterControllerMode();
        shell.FocusRibbonItem(games[1]);

        router.Handle(PadButton.Y);

        Assert.True(library.IsPageOpen);
        Assert.All(games, g => Assert.False(g.Favorite)); // favorites are on the game menu and the hero's star
        await Task.CompletedTask;
    });

    [Fact]
    public void Start_OpensTheGamesMenu_WithTheFirstRowLit() => InRibbon(async () =>
    {
        var (library, shell, _, router, games) = Create();
        library.EnterControllerMode();
        shell.FocusRibbonItem(games[0]);

        router.Handle(PadButton.Start);

        Assert.True(shell.IsMenuOpen);
        Assert.Same(games[0], shell.MenuGame);
        Assert.Equal(new Rect(10, 20, 30, 40), shell.MenuAnchor);
        Assert.Equal(0, shell.MenuActiveIndex);
        await Task.CompletedTask;
    });

    [Fact]
    public void InAMenu_TheDpadMovesTheRow_AGoes_AndBClosesIt() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        var sorted = false;
        shell.OpenSortMenu(new Rect(0, 0, 10, 10));
        var first = shell.MenuActiveIndex;

        router.Handle(PadButton.Down);
        Assert.Equal(first + 1, shell.MenuActiveIndex);
        router.Handle(PadButton.Up);
        Assert.Equal(first, shell.MenuActiveIndex);

        router.Handle(PadButton.Back);
        Assert.False(shell.IsMenuOpen);

        shell.OpenSortMenu(new Rect(0, 0, 10, 10));
        var before = library.SortOption;
        router.Handle(PadButton.Down);
        router.Handle(PadButton.Accept);
        sorted = !Equals(before, library.SortOption);

        Assert.True(sorted);
        Assert.False(shell.IsMenuOpen);
        Assert.Empty(surface.Calls); // a menu is the shell's own: the window was not asked to do anything
        await Task.CompletedTask;
    });

    [Fact]
    public void TheShoulders_SwitchBetweenHomeAndTheLibraryTab() => InRibbon(async () =>
    {
        var (library, shell, _, router, _) = Create();
        library.EnterControllerMode();

        router.Handle(PadButton.RB);
        Assert.Equal(ShellTab.Library, shell.Tab);

        router.Handle(PadButton.RB);
        Assert.Equal(ShellTab.Library, shell.Tab);

        router.Handle(PadButton.LB);
        Assert.Equal(ShellTab.Home, shell.Tab);
        await Task.CompletedTask;
    });

    // ---- going back --------------------------------------------------------------------------------------------

    [Fact]
    public void B_GoesBackOneLayerAtATime_AndAsksBeforeLeaving() => InRibbon(async () =>
    {
        var (library, shell, surface, router, _) = Create();
        library.EnterControllerMode();
        var leaveAsked = 0;
        router.LeaveRequested += () => leaveAsked++;

        router.Handle(PadButton.RB);                 // the Library tab
        router.Handle(PadButton.Back);
        Assert.Equal(ShellTab.Home, shell.Tab);
        Assert.Equal(0, leaveAsked);

        surface.TileFocus = false;                        // on a hero button: back to the strip first
        surface.Calls.Clear();
        router.Handle(PadButton.Back);
        Assert.Equal(["focus tile"], surface.Calls);
        Assert.Equal(0, leaveAsked);

        surface.TileFocus = true;                         // on the strip at the top level: ask
        router.Handle(PadButton.Back);
        Assert.Equal(1, leaveAsked);
        Assert.True(library.IsControllerMode);            // asking does not leave by itself
        await Task.CompletedTask;
    });

    [Fact]
    public void B_ClosesAPage_AMenu_AndADialog() => InRibbon(async () =>
    {
        var (library, shell, surface, router, _) = Create();
        library.EnterControllerMode();

        library.ShowStatsCommand.Execute(null);
        Assert.True(library.IsPageOpen);
        router.Handle(PadButton.Down);                    // a page: WPF focus
        Assert.Contains("move Down", surface.Calls);
        router.Handle(PadButton.Back);
        Assert.False(library.IsPageOpen);

        shell.ModalOpen = true;
        router.Handle(PadButton.Back);
        Assert.Contains("close modal", surface.Calls);
        shell.ModalOpen = false;

        shell.OpenSortMenu(new Rect(0, 0, 1, 1));
        router.Handle(PadButton.Back);
        Assert.False(shell.IsMenuOpen);
        await Task.CompletedTask;
    });

    // ---- the hints line -----------------------------------------------------------------------------------------

    [Fact]
    public void TheHints_AreShownOnlyInTheMode_AndFollowWhatIsOnScreen() => InRibbon(async () =>
    {
        var (library, shell, _, _, _) = Create();
        Assert.False(shell.HasPadHints);
        Assert.Equal(string.Empty, shell.PadHints);

        library.SetControllerStatus(true, null); // a controller is there
        library.EnterControllerMode();
        Assert.True(shell.HasPadHints);
        Assert.Contains("Leave", shell.PadHints);

        shell.Tab = ShellTab.Library;
        Assert.Contains("Home", shell.PadHints);

        shell.OpenSortMenu(new Rect(0, 0, 1, 1));
        Assert.Contains("Choose", shell.PadHints);
        shell.CloseMenu();

        library.ExitControllerMode();
        Assert.False(shell.HasPadHints);
        await Task.CompletedTask;
    });

    [Fact]
    public void WhenTheControllerGoesAway_TheModeStaysOn_ButTheHintSaysHowToCarryOnOrLeave() => InRibbon(async () =>
    {
        var (library, shell, _, _, _) = Create();
        library.SetControllerStatus(true, null);
        library.EnterControllerMode();
        Assert.Contains("Leave", shell.PadHints);

        library.SetControllerStatus(false, null); // it went to sleep

        Assert.True(library.IsControllerMode);                   // still on: a button press wakes it
        Assert.Contains("Controller disconnected", shell.PadHints);
        Assert.Contains("wake", shell.PadHints);
        Assert.Contains("click the controller button", shell.PadHints);

        library.SetControllerStatus(true, null);                 // and it came back
        Assert.Contains("Leave", shell.PadHints);
        Assert.DoesNotContain("disconnected", shell.PadHints);
        await Task.CompletedTask;
    });

    [Fact]
    public void DownFromTheTopBar_LandsOnTheGameTile_AndUpFromTheTile_LandsOnTheTab() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        shell.FocusRibbonItem(games[0]);

        surface.TileFocus = false;
        surface.TopBarFocus = true;
        surface.Calls.Clear();
        router.Handle(PadButton.Down);
        Assert.Equal(["ensure", "try tile"], surface.Calls); // whichever top bar control it was: the focused game's tile

        surface.TopBarFocus = false;
        surface.TileFocus = true;
        surface.Calls.Clear();
        router.Handle(PadButton.Up); // nothing above the strip in the shell state: the tab of this screen
        Assert.Equal(["ensure", "focus top bar"], surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void UpFromTheHeroButtons_ReturnsToTheTile_SoDownThenUpComesBack() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();
        library.EnterControllerMode();
        surface.TileFocus = false;
        surface.HeroFocus = true;

        router.Handle(PadButton.Up);

        Assert.Equal(["ensure", "focus tile"], surface.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public void OnAPage_TheTopBarAndHeroRulesStepAside_SoTheyCannotPullTheFocusBehindThePage() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();
        library.EnterControllerMode();
        library.ShowStatsCommand.Execute(null);
        surface.TileFocus = false;
        surface.TopBarFocus = true;
        surface.HeroFocus = true;

        router.Handle(PadButton.Down);
        router.Handle(PadButton.Up);

        Assert.Equal(["ensure", "move Down", "ensure", "move Up"], surface.Calls); // plain movement on the page
        await Task.CompletedTask;
    });

    [Fact]
    public void OnTheLibraryTab_ThePageButtonsMoveTheFocusAPageOfRows_NotJustTheScrollBar() => InRibbon(async () =>
    {
        var (library, shell, surface, router, games) = Create();
        library.EnterControllerMode();
        router.Handle(PadButton.RB); // the Library tab: one game per row in this little library
        shell.FocusRibbonItem(games[0]);
        surface.Calls.Clear();

        router.Handle(PadButton.RT); // a page down: three rows, but there are only three games
        Assert.Same(games[2], shell.FocusedGame);
        Assert.Contains("focus tile", surface.Calls);
        Assert.DoesNotContain("scroll 1", surface.Calls);

        router.Handle(PadButton.LT); // a page up: back to the first
        Assert.Same(games[0], shell.FocusedGame);
        await Task.CompletedTask;
    });

    [Fact]
    public void OnAPage_ThePageButtonsStillScrollThePage() => InRibbon(async () =>
    {
        var (library, _, surface, router, _) = Create();
        library.EnterControllerMode();
        library.ShowStatsCommand.Execute(null);
        surface.Calls.Clear();

        router.Handle(PadButton.RT);
        router.Handle(PadButton.LT);

        Assert.Equal(["scroll 1", "scroll -1"], surface.Calls);
        await Task.CompletedTask;
    });
}
