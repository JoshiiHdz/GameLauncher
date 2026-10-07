using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Controller mode in the real window: the entry button, the mode's look (full screen over the taskbar, scaled, no caption), the Settings card, and the pad
/// reaching the real controls through WPF focus.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerWindowTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary(bool ribbon)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-ControllerWindow-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Steam, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        if (ribbon)
            vm.AppearanceTheme = ThemeId.ConsoleRibbon;

        return vm;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    [Fact]
    public void TheTopBar_HasAControllerButton_OnlyInThePlayStationTheme_AndItTurnsTheModeOnAndOff() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            var button = window.RibbonView.ControllerButton;
            Assert.True(button.IsVisible);

            button.Command.Execute(null);
            Assert.True(vm.IsControllerMode);

            button.Command.Execute(null);
            Assert.False(vm.IsControllerMode);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheMode_GoesFullScreen_Scales_AndHidesTheCaption_ThenPutsEverythingBack() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            var before = new Rect(window.Left, window.Top, window.Width, window.Height);
            var monitor = WindowWorkArea.MonitorOf(window);
            Assert.NotNull(monitor);

            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            Assert.Equal(Visibility.Collapsed, window.ConsoleCaption.Visibility);
            Assert.True(window.Topmost);
            Assert.Equal(monitor!.Value.Width, window.Width, 1);
            Assert.Equal(monitor.Value.Height, window.Height, 1);
            var scale = Assert.IsType<ScaleTransform>(window.RootGrid.LayoutTransform);
            Assert.Equal(ControllerScale.For(monitor.Value.Width, monitor.Value.Height), scale.ScaleX);
            Assert.True(window.Shell.HasPadHints);

            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();

            Assert.Equal(Visibility.Visible, window.ConsoleCaption.Visibility);
            Assert.False(window.Topmost);
            Assert.Equal(before.Width, window.Width, 1);
            Assert.Equal(before.Height, window.Height, 1);
            Assert.Equal(before.Left, window.Left, 1);
            Assert.Equal(Transform.Identity.Value, window.RootGrid.LayoutTransform.Value);
            Assert.False(window.Shell.HasPadHints);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void SwitchingToAxis_WhileInTheMode_PutsTheWindowBack() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            vm.ChangeThemeCommand.Execute("Axis");
            await ShellTestSupport.SettleAsync();

            Assert.False(vm.IsControllerMode);
            Assert.False(window.Topmost);
            Assert.Equal(Transform.Identity.Value, window.RootGrid.LayoutTransform.Value);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void SettingsAppearance_OffersControllerMode_OnlyInThePlayStationTheme() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        try
        {
            foreach (var ribbon in new[] { false, true })
            {
                ThemeManager.Apply(ribbon ? ThemeId.ConsoleRibbon : ThemeId.Axis);
                var vm = NewLibrary(ribbon);
                var window = ShellTestSupport.OpenMain(vm);
                try
                {
                    await ShellTestSupport.SettleAsync();
                    vm.ShowSettingsCommand.Execute(null);
                    await ShellTestSupport.SettleAsync();
                    var page = Descendants<SettingsPage>(window.PageContent).Single();
                    page.CategoryList.SelectedItem = page.AppearanceCategory;
                    await ShellTestSupport.SettleAsync();
                    window.UpdateLayout();

                    Assert.Equal(ribbon, page.ControllerCard.IsVisible);
                    if (ribbon)
                    {
                        page.ControllerSwitch.IsChecked = true;
                        Assert.True(vm.IsControllerMode);
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void ThePadIsRead_WhileTheWindowIsInFront_EvenWithAGameRunningBehindIt() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            Assert.Null(window.PadGateReason(vm));

            vm.MarkGameRunning(vm.Games.First());
            Assert.True(vm.HasRunningGame);
            Assert.Null(window.PadGateReason(vm)); // the launcher is in front: the pad is for it

            window.WindowState = WindowState.Minimized; // a game that took the screen leaves the launcher out of the front
            Assert.Equal("window minimized", window.PadGateReason(vm));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ThePillWords_FollowTheControllerAndItsBattery() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(ribbon: false);
        Assert.Equal("Not connected", vm.ControllerPillText);
        Assert.Contains("No controller", vm.ControllerPillTooltip);
        Assert.False(vm.ControllerBatteryLow);

        vm.SetControllerStatus(true, null);
        Assert.Equal("Connected", vm.ControllerPillText);
        Assert.Contains("reports no battery", vm.ControllerPillTooltip);

        vm.SetControllerStatus(true, new PadBattery(false, PadBatteryLevel.Medium));
        Assert.Equal("Connected · Medium", vm.ControllerPillText);
        Assert.Contains("Battery medium", vm.ControllerPillTooltip);
        Assert.Contains("signal strength", vm.ControllerPillTooltip); // says plainly that Windows does not give it
        Assert.False(vm.ControllerBatteryLow);

        vm.SetControllerStatus(true, new PadBattery(false, PadBatteryLevel.Low));
        Assert.True(vm.ControllerBatteryLow);

        vm.SetControllerStatus(true, new PadBattery(true, PadBatteryLevel.Full));
        Assert.Equal("Connected", vm.ControllerPillText);
        Assert.DoesNotContain("Wired", vm.ControllerPillText);
        Assert.Contains("dongle", vm.ControllerPillTooltip); // a receiver that hides the battery is not "wired"
        Assert.False(vm.ControllerBatteryLow);

        vm.SetControllerStatus(false, new PadBattery(false, PadBatteryLevel.Low)); // gone: the battery goes with it
        Assert.Equal("Not connected", vm.ControllerPillText);
        Assert.Null(vm.ControllerBattery);
        await Task.CompletedTask;
    });

    [Fact]
    public void ThePill_IsAlwaysOnScreen_InAxisAndInThePlayStationTheme_EvenInASmallWindow() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        try
        {
            foreach (var ribbon in new[] { false, true })
            {
                foreach (var (width, height) in new[] { (1100d, 760d), (760d, 480d) })
                {
                    ThemeManager.Apply(ribbon ? ThemeId.ConsoleRibbon : ThemeId.Axis);
                    var vm = NewLibrary(ribbon);
                    var window = ShellTestSupport.OpenMain(vm, width, height);
                    try
                    {
                        await ShellTestSupport.SettleAsync();
                        var pill = ribbon ? (FrameworkElement)window.RibbonView.RibbonControllerPill : window.AxisControllerPill;
                        var where = $"{(ribbon ? "PlayStation" : "Axis")} {width}x{height}";

                        Assert.True(pill.IsVisible, where);
                        Assert.Contains("No controller", (string)pill.ToolTip);

                        vm.SetControllerStatus(true, new PadBattery(false, PadBatteryLevel.Full));
                        await ShellTestSupport.SettleAsync();
                        Assert.Contains("Battery full", (string)pill.ToolTip);

                        var bounds = pill.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(0, 0, pill.ActualWidth, pill.ActualHeight));
                        var root = (FrameworkElement)window.Content;
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= root.ActualWidth + 0.5, $"{where}: the pill is cut off sideways ({bounds})");
                        Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= root.ActualHeight + 0.5, $"{where}: the pill is cut off vertically ({bounds})");

                        // Never on top of the window's other buttons.
                        foreach (var other in Descendants<System.Windows.Controls.Primitives.ButtonBase>(window).Where(b => b.IsVisible && b.ActualWidth > 8 && b.TemplatedParent is null))
                        {
                            var rect = other.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(0, 0, other.ActualWidth, other.ActualHeight));
                            Assert.False(rect.IntersectsWith(Rect.Inflate(bounds, -1, -1)), $"{where}: the pill overlaps {other.GetType().Name} {other.Name}");
                        }
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void ThePad_DrivesTheSearchPalette_AndChoosesAGame() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            // With no keyboard to type with, the palette still lists the games (favorites and the most recent), so a pad can pick one.
            vm.Games.First(g => g.Name == "Borderlands").Favorite = true;
            var items = vm.BuildPaletteItems();
            Assert.Contains(items, i => i.Title == "Borderlands" && i.ShowWhenEmpty);

            var model = new CommandPaletteViewModel(
            [
                new PaletteItem("Apex", "sub", "", PaletteKind.Game, () => { }),
                new PaletteItem("Hades", "sub", "", PaletteKind.Game, () => { }),
                new PaletteItem("Celeste", "sub", "", PaletteKind.Game, () => { }),
            ]);
            model.Query = "e";
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Search", new CommandPaletteDialog(model));
            var router = window.ControllerRouterForTest;
            await ShellTestSupport.SettleAsync();
            Assert.True(window.PadKeyboard.IsOpen); // the palette comes up with its keyboard
            router.Handle(PadButton.Start);        // Done: now the pad drives the results
            await ShellTestSupport.SettleAsync();
            Assert.False(window.PadKeyboard.IsOpen);
            Assert.Equal(0, model.SelectedIndex);

            router.Handle(PadButton.Down);
            Assert.Equal(1, model.SelectedIndex);
            router.Handle(PadButton.Down);
            router.Handle(PadButton.Up);
            Assert.Equal(1, model.SelectedIndex);

            router.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();

            Assert.NotNull(model.Chosen);
            Assert.False(window.IsModalOpen); // choosing closed it
            await shown.Operation;
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void WhenNoTileCanBeFocused_ThePressIsNotSwallowed() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            window.Shell.ShowLibraryCommand.Execute(null);
            await ShellTestSupport.SettleAsync();

            vm.SearchText = "zzz-matches-nothing"; // an empty Library tab: no poster to focus, but the filters are still there
            await ShellTestSupport.SettleAsync();
            Keyboard.ClearFocus();

            window.ControllerRouterForTest.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();

            Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(Keyboard.FocusedElement); // focus landed on a control instead of being lost
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void LeavingControllerMode_ThenSwitchingToAxis_DoesNotDrawTheConsoleCaptionOverAxis() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            var minWidth = window.MinWidth;
            var minHeight = window.MinHeight;

            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.Equal(Visibility.Collapsed, window.ConsoleCaption.Visibility); // full screen: no caption
            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.Equal(Visibility.Visible, window.ConsoleCaption.Visibility);   // the PlayStation caption is back
            Assert.Equal(minWidth, window.MinWidth);                               // and the window's own minimum size, not a copy of a number
            Assert.Equal(minHeight, window.MinHeight);

            vm.ChangeThemeCommand.Execute("Axis");
            await ShellTestSupport.SettleAsync();
            Assert.Equal(Visibility.Collapsed, window.ConsoleCaption.Visibility); // Axis has its own title bar: the binding still decides
            Assert.False(window.ConsoleCaption.IsVisible);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ComingBackFromAGame_KeepsControllerModeFullScreenAndOn() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            var width = window.Width;

            window.WindowState = WindowState.Minimized; // the launcher puts itself away while the game runs
            window.RestoreFromTray();                   // and the game closes
            await ShellTestSupport.SettleAsync();

            Assert.True(vm.IsControllerMode);
            Assert.True(window.Topmost);
            Assert.Equal(width, window.Width, 1);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ThePad_ReachesTheRealControls_ThroughWpfFocus() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            var router = window.ControllerRouterForTest;

// Turning the mode on puts the pad on the focused game's tile: no press is needed to find where it is.            Assert.True(window.RibbonView.GameTileHasFocus, $"focus is on {Keyboard.FocusedElement}");            Assert.False(window.Shell.LibraryTileFocused);            var first = window.Shell.FocusedGame;            // Right moves along the strip to the next game, and its tile has the keyboard focus; at the end of the strip it stays put.            router.Handle(PadButton.Right);            await ShellTestSupport.SettleAsync();            Assert.NotSame(first, window.Shell.FocusedGame);            Assert.True(window.RibbonView.GameTileHasFocus);            var last = window.Shell.FocusedGame;            router.Handle(PadButton.Right);            await ShellTestSupport.SettleAsync();            Assert.Same(last, window.Shell.FocusedGame);            Assert.True(window.RibbonView.GameTileHasFocus);

            // Down leaves the strip for the hero's buttons, and A presses the one that has focus.
            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(Keyboard.FocusedElement);
            Assert.False(window.RibbonView.GameTileHasFocus);

            // B returns to the strip.
            router.Handle(PadButton.Back);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ThePad_SwitchingOffInTheMode_ShowsThePointerAndAToast_AndComingBackHidesItAgain() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            window.HandlePadConnection(vm, true);
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.Contains("Leave", window.Shell.PadHints);

            Mouse.OverrideCursor = Cursors.None; // the pointer is hidden while the pad is in use

            window.HandlePadConnection(vm, false); // the pad switches itself off
            await ShellTestSupport.SettleAsync();

            Assert.True(vm.IsControllerMode);                                    // the mode stays on
            Assert.False(vm.ControllerConnected);                                // the pill says so
            Assert.Null(Mouse.OverrideCursor);                                   // the pointer is back, so the mouse works
            Assert.Equal("Controller disconnected", window.Shell.FeedbackText);  // and a toast says why nothing responds
            Assert.Contains("Controller disconnected", window.Shell.PadHints);

            window.HandlePadConnection(vm, true); // a press woke it
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.ControllerConnected);
            Assert.Equal("Controller connected", window.Shell.FeedbackText);
            Assert.Contains("Leave", window.Shell.PadHints);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void StartingTheModeWithNoPad_SaysSo_AndKeepsThePointer() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ribbon: true);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            Assert.False(vm.ControllerConnected);

            vm.EnterControllerMode(); // from the top-bar button, with the mouse
            await ShellTestSupport.SettleAsync();

            Assert.Equal("No controller connected", window.Shell.FeedbackText);
            Assert.Null(Mouse.OverrideCursor);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the way in from Axis ----------------------------------------------------------------------------------------

    private static async Task OpenAskAsync(MainWindow window)
    {
        window.ControllerRouterForTest.Handle(PadButton.Start);
        for (var i = 0; i < 40 && !window.IsModalOpen; i++)
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

        await ShellTestSupport.SettleAsync();
    }

    [Fact]
    public void Axis_HasAControllerButton_F11_AndAPaletteCommand_AndEachPutsThePlayStationThemeOnForTheSession() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = NewLibrary(ribbon: false);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            Assert.True(window.AxisControllerModeButton.IsVisible);
            Assert.Contains(window.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.F11 && b.Command == vm.EnterControllerModeFromAxisCommand);
            var palette = vm.BuildPaletteItems().Single(i => i.Title.StartsWith("Controller mode"));

            window.AxisControllerModeButton.Command.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsControllerMode);
            Assert.Equal(ThemeId.ConsoleRibbon, vm.AppearanceTheme);

            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.Equal(ThemeId.Axis, vm.AppearanceTheme);
            Assert.True(window.AxisControllerModeButton.IsVisible);

            palette.Execute();
            Assert.True(vm.IsControllerMode);
            vm.ExitControllerMode();
            Assert.Equal(ThemeId.Axis, vm.AppearanceTheme);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void StartOnThePad_InAxis_AsksFirst_TheDefaultIsStay_AndThePadCanAnswerYes() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = NewLibrary(ribbon: false);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            Assert.DoesNotContain("PlayStation", window.PadGateReason(vm) ?? ""); // the pad is read in Axis too

            await OpenAskAsync(window);
            Assert.True(window.IsModalOpen);
            Assert.False(vm.IsControllerMode);
            Assert.True(window.ControllerRouterForTest.AskingToEnter);

            window.ControllerRouterForTest.Handle(PadButton.Start); // a second Start does not stack another question
            await ShellTestSupport.SettleAsync();

            window.ControllerRouterForTest.Handle(PadButton.Accept); // the focus starts on "Stay", so A is the safe answer
            await ShellTestSupport.SettleAsync();
            Assert.False(window.IsModalOpen);
            Assert.False(vm.IsControllerMode);
            Assert.Equal(ThemeId.Axis, vm.AppearanceTheme);

            await OpenAskAsync(window);
            window.ControllerRouterForTest.Handle(PadButton.Back); // B is "no" as well
            await ShellTestSupport.SettleAsync();
            Assert.False(window.IsModalOpen);
            Assert.False(vm.IsControllerMode);

            await OpenAskAsync(window);
            Descendants<ConfirmDialog>(window).Single().YesButton.Focus();
            window.ControllerRouterForTest.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsControllerMode);
            Assert.Equal(ThemeId.ConsoleRibbon, vm.AppearanceTheme);
            Assert.False(window.ControllerRouterForTest.AskingToEnter);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
