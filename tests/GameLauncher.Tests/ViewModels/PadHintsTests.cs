using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GameLauncher.Controls;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The controller guide: real-looking buttons (A green, B red, X blue, Y yellow, Start a circle with three lines), what it says, and where it sits - at the bottom centre,
/// directly above the status pill.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class PadHintsTests(WpfStaFixture sta)
{
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        // The logical tree, so controls that have not been through layout yet (and so have no visuals) can be inspected too.
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Walk(child))
                yield return d;
    }

    /// <summary>The visual tree, for things inside templates (a page, a list's rows) that the logical tree does not reach.</summary>
    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var d in VisualWalk(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static Color FillOf(PadGlyphView view) => ((SolidColorBrush)Walk(view).OfType<Ellipse>().First().Fill).Color;

    // ---- the buttons ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFaceButtons_AreXboxColoured_WithTheirLetter() => sta.RunAsync(async () =>
    {
        var expected = new Dictionary<PadGlyph, Color>
        {
            [PadGlyph.A] = Color.FromRgb(0x3D, 0xB5, 0x49), // green
            [PadGlyph.B] = Color.FromRgb(0xE5, 0x39, 0x2B), // red
            [PadGlyph.X] = Color.FromRgb(0x2D, 0x7F, 0xF9), // blue
            [PadGlyph.Y] = Color.FromRgb(0xF5, 0xB8, 0x00), // yellow
        };

        foreach (var (glyph, colour) in expected)
        {
            var view = new PadGlyphView { Glyph = glyph };
            view.Measure(new Size(100, 100));

            Assert.Equal(colour, FillOf(view));
            Assert.Equal(glyph.ToString(), Walk(view).OfType<TextBlock>().Single().Text);
            Assert.Equal(22, view.DesiredSize.Width, 1);
        }

        // Yellow carries a dark letter so it can be read; the others a white one.
        Assert.Equal(Colors.White, ((SolidColorBrush)Walk(new PadGlyphView { Glyph = PadGlyph.A }).OfType<TextBlock>().Single().Foreground).Color);
        Assert.NotEqual(Colors.White, ((SolidColorBrush)Walk(new PadGlyphView { Glyph = PadGlyph.Y }).OfType<TextBlock>().Single().Foreground).Color);
        await Task.CompletedTask;
    });

    [Fact]
    public void Start_IsACircleWithThreeHorizontalLines() => sta.RunAsync(async () =>
    {
        var view = new PadGlyphView { Glyph = PadGlyph.Start };
        view.Measure(new Size(100, 100));

        Assert.Single(Walk(view).OfType<Ellipse>());                          // the circle
        var lines = Walk(view).OfType<Line>().ToList();
        Assert.Equal(3, lines.Count);                                          // three of them
        Assert.All(lines, l => Assert.Equal(l.Y1, l.Y2));                      // each horizontal
        Assert.Equal(3, lines.Select(l => l.Y1).Distinct().Count());           // one above the other
        Assert.All(lines, l => Assert.True(l.X2 > l.X1));
        Assert.Empty(Walk(view).OfType<TextBlock>());                          // no word: the picture is the button
        await Task.CompletedTask;
    });

    [Fact]
    public void TheBumpersAndTriggers_AreLabelledBadges_AndTheRightStickIsARingWithItsCap() => sta.RunAsync(async () =>
    {
        foreach (var glyph in new[] { PadGlyph.LB, PadGlyph.RB, PadGlyph.LT, PadGlyph.RT })
        {
            var view = new PadGlyphView { Glyph = glyph };
            Assert.Equal(glyph.ToString(), Walk(view).OfType<TextBlock>().Single().Text);
            Assert.Single(Walk(view).OfType<Border>());
        }

        var stick = new PadGlyphView { Glyph = PadGlyph.RightStick };
        Assert.Equal(2, Walk(stick).OfType<Ellipse>().Count()); // the ring and its cap
        await Task.CompletedTask;
    });

    // ---- the strip -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStrip_DrawsAButtonBesideEachLabel_TwoForAPair_NoneForASentence() => sta.RunAsync(async () =>
    {
        var strip = new PadHintsStrip
        {
            Items =
            [
                PadHintItem.Of(PadGlyph.A, "Select"),
                PadHintItem.Of(PadGlyph.LB, PadGlyph.RB, "Move cursor"),
                PadHintItem.Sentence("Controller disconnected"),
            ],
        };

        var glyphs = Walk(strip).OfType<PadGlyphView>().Select(g => g.Glyph).ToList();
        Assert.Equal([PadGlyph.A, PadGlyph.LB, PadGlyph.RB], glyphs);
        Assert.Equal(["Select", "Move cursor", "Controller disconnected"], Walk(strip).OfType<TextBlock>().Where(t => t.Parent is StackPanel).Select(t => t.Text));
        await Task.CompletedTask;
    });

    [Fact]
    public void TheTextForm_StillReadsAsBefore() => sta.RunAsync(async () =>
    {
        Assert.Equal("A  Select", PadHintItem.Of(PadGlyph.A, "Select").Text);
        Assert.Equal("LB RB  Move cursor", PadHintItem.Of(PadGlyph.LB, PadGlyph.RB, "Move cursor").Text);
        Assert.Equal("Start  Done", PadHintItem.Of(PadGlyph.Start, "Done").Text);
        Assert.Equal("Right stick  Scroll", PadHintItem.Of(PadGlyph.RightStick, "Scroll").Text);
        Assert.Equal("A  Type      X  Backspace", PadHintItem.Join([PadHintItem.Of(PadGlyph.A, "Type"), PadHintItem.Of(PadGlyph.X, "Backspace")]));
        Assert.Contains("Start  Done", KeyboardLayout.Help);
        await Task.CompletedTask;
    });

    // ---- what it says -----------------------------------------------------------------------------------------------------------

    private static LibraryViewModel NewLibrary()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameLauncherTests-PadHints-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" }]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        vm.SetControllerStatus(true, null);
        return vm;
    }

    private static List<(string Buttons, string Label)> Say(ShellState shell) =>
        shell.PadHintItems.Select(i => (string.Join(" ", i.Glyphs), i.Label)).ToList();

    [Fact]
    public void OnTheHome_TheHintsAreTheButtonsAndTheirJobs_InTheOrderOnThePad() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            Assert.Empty(shell.PadHintItems);          // nothing outside controller mode
            Assert.False(shell.HasPadHints);

            vm.EnterControllerMode();

            Assert.Equal(
            [
                ("A", "Select"), ("X", "Search"), ("Y", "Settings"), ("Start", "Menu"), ("RB", "Library"), ("RightStick", "Scroll"), ("B", "Leave"),
            ], Say(shell));
            Assert.Equal("A  Select      X  Search      Y  Settings      Start  Menu      RB  Library      Right stick  Scroll      B  Leave", shell.PadHints);

            vm.ControllerMap.Set(PadButton.Y, PadFunction.Optimize); // a remapped button is still drawn as itself, with its new job
            Assert.Contains(("Y", "Optimize"), Say(shell));
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void OnAPage_InAMenu_InTheSearch_AndWhenThePadIsAsleep_TheHintsChange() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            vm.EnterControllerMode();

            vm.ShowStatsCommand.Execute(null);
            Assert.Equal([("A", "Select"), ("B", "Back"), ("RightStick", "Scroll"), ("LT RT", "Page")], Say(shell));
            vm.ClosePage();

            shell.OpenSortMenu(new Rect(0, 0, 1, 1));
            Assert.Equal([("A", "Choose"), ("B", "Close")], Say(shell));
            shell.CloseMenu();

            shell.ModalOpen = true;
            shell.PaletteOpen = true;
            Assert.Equal([("A", "Choose"), ("X", "Type"), ("B", "Close")], Say(shell));
            shell.KeyboardOpen = true;
            Assert.Empty(shell.PadHintItems);          // the keyboard has its own help
            shell.KeyboardOpen = false;
            shell.PaletteOpen = false;
            shell.ModalOpen = false;

            vm.SetControllerStatus(false, null);
            var asleep = Assert.Single(shell.PadHintItems);
            Assert.Empty(asleep.Glyphs);               // a sentence, no buttons
            Assert.Contains("Controller disconnected", asleep.Label);
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void TheKeyboardsHelpLine_UsesTheSameButtons() => sta.RunAsync(async () =>
    {
        var keyboard = new OnScreenKeyboard();
        var strip = (PadHintsStrip)keyboard.FindName("HelpLine");

        Assert.Equal(
            [PadGlyph.A, PadGlyph.X, PadGlyph.Y, PadGlyph.LB, PadGlyph.RB, PadGlyph.LT, PadGlyph.RT, PadGlyph.Start, PadGlyph.B],
            Walk(strip).OfType<PadGlyphView>().Select(g => g.Glyph));
        await Task.CompletedTask;
    });

    // ---- where it sits ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGuide_IsAtTheBottomCentre_DirectlyAboveTheStatusPill() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm, 1280, 900);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            window.Width = 1280;
            window.Height = 900;
            window.Left = -5000;
            window.Top = -5000;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var guide = window.RibbonView.PadHintsPill;
            var status = window.RibbonView.StatusPill;
            Assert.True(guide.IsVisible);
            Assert.True(status.IsVisible);

            var g = FocusNavigator.BoundsIn(guide, window.RootGrid);
            var s = FocusNavigator.BoundsIn(status, window.RootGrid);
            var middle = window.RootGrid.ActualWidth / 2;

            Assert.Equal(middle, g.Left + g.Width / 2, 0);   // the guide is centred
            Assert.Equal(middle, s.Left + s.Width / 2, 0);   // like the status pill
            Assert.True(g.Bottom <= s.Top + 0.5, $"the guide ({g}) should sit on top of the status pill ({s})");
            Assert.True(s.Top - g.Bottom < 14, "and directly on top of it, not far above");
            Assert.True(g.Left > 100, "no longer in the bottom-left corner");

            // And it is drawn with the real buttons: a green A, a blue X, a yellow Y, a red B and the Start circle.
            var glyphs = Walk(guide).OfType<PadGlyphView>().ToList();
            Assert.Contains(glyphs, v => v.Glyph == PadGlyph.A && FillOf(v) == Color.FromRgb(0x3D, 0xB5, 0x49));
            Assert.Contains(glyphs, v => v.Glyph == PadGlyph.X && FillOf(v) == Color.FromRgb(0x2D, 0x7F, 0xF9));
            Assert.Contains(glyphs, v => v.Glyph == PadGlyph.Y && FillOf(v) == Color.FromRgb(0xF5, 0xB8, 0x00));
            Assert.Contains(glyphs, v => v.Glyph == PadGlyph.B && FillOf(v) == Color.FromRgb(0xE5, 0x39, 0x2B));
            Assert.Contains(glyphs, v => v.Glyph == PadGlyph.Start && Walk(v).OfType<Line>().Count() == 3);

            // Without a status line the guide still sits at the very bottom, centred.
            vm.StatusText = string.Empty;
            window.Shell.ClearFeedback();
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            Assert.Equal(middle, FocusNavigator.BoundsIn(guide, window.RootGrid).Left + FocusNavigator.BoundsIn(guide, window.RootGrid).Width / 2, 0);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the first-open bug: a guide that was a speck until something forced a new layout ----------------------------------------------

    [Fact]
    public void WhenTheLauncherIsOpenedAndTheModeIsSwitchedOnAtOnce_TheGuideIsFullSize_NotASpeck() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        vm.SetControllerStatus(false, null);
        // Opened the way the app opens it (maximized per the setting) and put into controller mode at once, as Start on a pad does right at launch.
        var window = new MainWindow(vm, startRuntimeServices: false, openPerSetting: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.SetControllerStatus(true, null);

            window.ControllerRouterForTest.Handle(PadButton.Start);
            await ShellTestSupport.SettleAsync();

            var pill = window.RibbonView.PadHintsPill;
            var strip = window.RibbonView.PadHintsStrip;
            Assert.True(vm.IsControllerMode);
            Assert.True(pill.IsVisible);
            Assert.True(window.Shell.PadHintItems.Count >= 5);
            Assert.True(strip.ActualWidth > 300, $"the guide's own size is {strip.ActualWidth} x {strip.ActualHeight}");
            Assert.True(pill.ActualWidth > 300 && pill.ActualHeight > 25, $"the guide is {pill.ActualWidth} x {pill.ActualHeight}: a speck, not the guide");
            Assert.Equal(window.Shell.PadHintItems.Count, Walk(strip).OfType<TextBlock>().Count(t => t.Parent is StackPanel));

            // And it stays right as the hints change (a page opens, the pad goes to sleep).
            vm.ShowStatsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(pill.ActualWidth > 300, $"after a page opens the guide is {pill.ActualWidth} wide");
            vm.SetControllerStatus(false, null);
            await ShellTestSupport.SettleAsync();
            Assert.True(pill.ActualWidth > 300, $"with the pad asleep the guide is {pill.ActualWidth} wide");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the buttons in Settings ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void InSettings_EveryButtonIsDrawnAsItLooksOnThePad_AAndBAreShownAsFixed() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm, 1280, 900);
        try
        {
            await ShellTestSupport.SettleAsync();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = VisualWalk(window.PageHost).OfType<SettingsPage>().Single();
            page.CategoryList.SelectedItem = page.AppearanceCategory;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            // The seven buttons that can be given another job: X, Y, Start, LB, RB, LT, RT - each a real-looking button beside its row.
            var rows = VisualWalk(page.ControllerButtonList).OfType<PadGlyphView>().Select(g => g.Glyph).ToList();
            Assert.Equal([PadGlyph.X, PadGlyph.Y, PadGlyph.Start, PadGlyph.LB, PadGlyph.RB, PadGlyph.LT, PadGlyph.RT], rows);

            // The ones that never change are listed too, as what they are: A, B and the right stick.
            var fixedOnes = VisualWalk(page.ControllerFixedButtons).OfType<PadGlyphView>().Select(g => g.Glyph).ToList();
            Assert.Equal([PadGlyph.A, PadGlyph.B, PadGlyph.RightStick], fixedOnes);

            // Drawn properly there too: the Y row has the yellow disc, the Start row the circle with three lines.
            var all = VisualWalk(page.ControllerButtonList).OfType<PadGlyphView>().ToList();
            Assert.Equal(Color.FromRgb(0xF5, 0xB8, 0x00), FillOf(all.Single(g => g.Glyph == PadGlyph.Y)));
            Assert.Equal(3, VisualWalk(all.Single(g => g.Glyph == PadGlyph.Start)).OfType<Line>().Count());
            Assert.Equal(Color.FromRgb(0x3D, 0xB5, 0x49), FillOf(VisualWalk(page.ControllerFixedButtons).OfType<PadGlyphView>().First()));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
