using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>"Usable without a mouse": with the pad's own buttons (the D-pad through the router, in a real window) every control on a page can be reached, and the
/// focus never ends up behind the page.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerReachTests(WpfStaFixture sta)
{
    private static (LibraryViewModel Vm, MainWindow Window) Open()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Reach-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        return (vm, ShellTestSupport.OpenMain(vm, 1280, 720));
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static bool IsControl(FrameworkElement e) =>
        (e is ButtonBase or TextBoxBase or ComboBox or ListBoxItem) && e.IsVisible && e.Focusable && e.IsEnabled && e.ActualWidth > 2 && e.ActualHeight > 2
        && (e.TemplatedParent is null || e.TemplatedParent is ContentPresenter);

    private static bool Within(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static string Name(object? o) => o is FrameworkElement e
        ? $"{e.GetType().Name}#{e.Name}[{(e is ContentControl { Content: string s } ? s : e.ToolTip as string ?? "")}]" : "nothing";

    /// <summary>Presses each direction until it stops moving, like a thumb would,, and returns every control the focus visited. Fails if it ever leaves <paramref name="layer"/>.</summary>
    private static async Task<HashSet<FrameworkElement>> Tour(MainWindow window, FrameworkElement layer, int steps = 90)
    {
        var router = window.ControllerRouterForTest;
        var seen = new HashSet<FrameworkElement>();
        Keyboard.ClearFocus();
        router.Handle(PadButton.Down); // the first press only shows where you are
        await ShellTestSupport.SettleAsync();

        foreach (var press in new[] { PadButton.Down, PadButton.Right, PadButton.Up, PadButton.Left })
        {
            var stuck = 0;
            for (var i = 0; i < steps && stuck < 4; i++)
            {
                var before = Keyboard.FocusedElement;
                router.Handle(press);
                await ShellTestSupport.SettleAsync();
                var after = Keyboard.FocusedElement;
                Assert.True(after is DependencyObject d && Within(d, layer), $"the pad left the page and landed on {Name(after)}");
                if (after is FrameworkElement control)
                    seen.Add(control);

                stuck = ReferenceEquals(before, after) ? stuck + 1 : 0;
            }
        }

        return seen;
    }

    /// <summary>The controls (by the test's own, independent, idea of what a control is) that the pad cannot get to from where it starts, following what each direction really leads
    /// to - the very navigator the window uses. Nothing is pressed, so nothing changes while it runs.</summary>
    private static List<FrameworkElement> Unreachable(MainWindow window, FrameworkElement layer)
    {
        window.UpdateLayout();
        var universe = Walk(layer).OfType<FrameworkElement>().Where(IsControl).ToList();
        var reachable = FocusNavigator.Reachable(layer, window.Shell.FocusedGame, window.Shell.LibraryTileFocused);
        var start = FocusNavigator.FirstInReadingOrder(reachable, window.RootGrid, c => c.Name == "PageBackButton" && reachable.Count > 1);
        if (start is null)
            return universe;

        var seen = new HashSet<FrameworkElement> { start };
        var queue = new Queue<FrameworkElement>(seen);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var direction in new[] { ShellDirection.Down, ShellDirection.Up, ShellDirection.Right, ShellDirection.Left })
            {
                if (FocusNavigator.Next(current, reachable, direction, window.RootGrid) is { } target && seen.Add(target))
                    queue.Enqueue(target);
            }
        }

        return universe.Where(c => !seen.Contains(c)).ToList();
    }

    [Fact]
    public void InSettings_ThePadGetsPastTheCategories_IntoTheOptions_AndPressesThem() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();

            var seen = await Tour(window, window.PageHost);

            Assert.Contains(seen, c => c is ListBoxItem);                                // the categories
            Assert.Contains(seen, c => c is not ListBoxItem and not RadioButton);        // and the options next to them

            // A presses the switch that has focus: from the General category, Right goes in and Down finds "Open maximized".
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();
            page.CategoryList.SelectedItem = page.GeneralCategory;
            await ShellTestSupport.SettleAsync();
            Keyboard.Focus(page.GeneralCategory);
            var router = window.ControllerRouterForTest;
            router.Handle(PadButton.Right);
            await ShellTestSupport.SettleAsync();
            for (var i = 0; i < 30 && Keyboard.FocusedElement is not ToggleButton { Content: "Open maximized" }; i++)
            {
                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();
            }

            var toggle = Assert.IsAssignableFrom<ToggleButton>(Keyboard.FocusedElement);
            var before = toggle.IsChecked;
            router.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.NotEqual(before, toggle.IsChecked);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void EveryControlOnEverySettingsCategory_CanBeReachedWithThePad() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        var problems = new List<string>();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();

            foreach (var category in page.CategoryList.Items.OfType<ListBoxItem>().ToList())
            {
                page.CategoryList.SelectedItem = category;
                await ShellTestSupport.SettleAsync();
                window.UpdateLayout();

                foreach (var missed in Unreachable(window, window.PageHost)) // measured first: a list that follows the pad changes what is showing
                    problems.Add($"{category.Content}: {Name(missed)}");
                await Tour(window, window.PageHost); // real presses: the focus must never leave the page
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }

        Assert.True(problems.Count == 0, "Not reachable by the pad:\n" + string.Join("\n", problems));
    });

    [Fact]
    public void OnTheOptimizePage_ThePadReachesEveryButton() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            vm.ShowOptimizeCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var missed = Unreachable(window, window.PageHost).Select(Name).ToList(); // measured first: a list that follows the pad changes what is showing
            await Tour(window, window.PageHost); // real presses: the focus must never leave the page
            Assert.True(missed.Count == 0, "Not reachable by the pad: " + string.Join(", ", missed));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void OnEveryOtherPage_ThePadReachesEveryControl_AndPageBackClosesIt() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        var problems = new List<string>();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            var game = vm.Games.First();

            foreach (var (name, open) in new (string, Action)[]
            {
                ("Stats", () => vm.ShowStatsCommand.Execute(null)),
                ("Game details", () => vm.ShowGameDetailsCommand.Execute(game)),
                ("Play time", () => vm.ShowPlayTimeCommand.Execute(game)),
                ("Pick a game", () => vm.PickGameCommand.Execute(null)),
            })
            {
                open();
                await ShellTestSupport.SettleAsync();
                Assert.True(vm.IsPageOpen, name);

                await Tour(window, window.PageHost);
                foreach (var missed in Unreachable(window, window.PageHost))
                    problems.Add($"{name}: {Name(missed)}");

                window.ControllerRouterForTest.Handle(PadButton.Back); // B closes the page
                await ShellTestSupport.SettleAsync();
                Assert.False(vm.IsPageOpen, name);
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }

        Assert.True(problems.Count == 0, "Not reachable by the pad:\n" + string.Join("\n", problems));
    });

    [Fact]
    public void InSettings_AChoosesACategory_AndMovingOntoOneShowsItsOptions() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();
            var router = window.ControllerRouterForTest;

            // Down onto the Library category shows the Library options at once (the list follows the pad).
            Keyboard.Focus(page.GeneralCategory);
            await ShellTestSupport.SettleAsync();
            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.Same(page.LibraryCategory, Keyboard.FocusedElement);
            Assert.Same(page.LibraryCategory, page.CategoryList.SelectedItem);
            Assert.True(page.LibrarySection.IsVisible);

            // A chooses a category too, even where moving did not (here: put the selection somewhere else first).
            page.CategoryList.SelectedItem = page.GeneralCategory;
            await ShellTestSupport.SettleAsync();
            Keyboard.Focus(page.AboutCategory);
            router.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.Same(page.AboutCategory, page.CategoryList.SelectedItem);
            Assert.True(page.AboutSection.IsVisible);

            // Right goes into the options.
            router.Handle(PadButton.Right);
            await ShellTestSupport.SettleAsync();
            Assert.IsNotType<ListBoxItem>(Keyboard.FocusedElement);
            Assert.True(Within((DependencyObject)Keyboard.FocusedElement, page.AboutSection));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void DownFromAnyGameCard_LandsOnTheRealPlayButton() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            var router = window.ControllerRouterForTest;

            foreach (var game in vm.Games.ToList())
            {
                window.Shell.FocusRibbonItem(game);
                await ShellTestSupport.SettleAsync();
                Assert.True(window.RibbonView.TryFocusTile());
                Assert.True(window.RibbonView.GameTileHasFocus);

                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();

                Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
            }

            // The Library tile too.
            window.Shell.FocusRibbonItem(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.TryFocusTile());
            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheButtonMapping_IsInSettings_ChangesWithAPress_AndResets() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();
            page.CategoryList.SelectedItem = page.AppearanceCategory;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var rows = Walk(page.ControllerButtonList).OfType<Wpf.Ui.Controls.Button>().ToList();
            Assert.Equal(7, rows.Count);                                    // X, Y, Start, LB, RB, LT, RT: not A, not B
            Assert.Equal("Default · Settings", (string)rows[1].Content);    // Y

            // Focus Y's row and press A on the pad: it moves on to the next job.
            Keyboard.Focus(rows[1]);
            window.ControllerRouterForTest.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.Equal("Library", (string)rows[1].Content);
            Assert.Equal(PadFunction.Library, vm.ControllerMap.Resolve(PadButton.Y));
            Assert.True(page.ControllerResetButton.IsEnabled);

            // Reset puts everything back.
            Keyboard.Focus(page.ControllerResetButton);
            window.ControllerRouterForTest.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.Equal("Default · Settings", (string)rows[1].Content);
            Assert.True(vm.ControllerMap.IsAllDefault);
            Assert.False(page.ControllerResetButton.IsEnabled);

            // Every row and the reset button can be reached with the pad.
            Assert.Empty(Unreachable(window, window.PageHost));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void InEveryDialog_ThePadStartsInsideIt_ReachesEveryControl_NeverLeavesIt_AndBClosesIt() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var (vm, window) = Open();
        var problems = new List<string>();
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            var dialogs = new (string Title, FrameworkElement Content)[]
            {
                ("Collections", new CollectionsDialog(new CollectionsViewModel("Game 0", ["Backlog", "Co-op"], ["Backlog"]))),
                ("Send Feedback", new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d"))),
                ("Confirm", new ConfirmDialog("Sure?", "Yes", "No", warning: true)),
            };

            foreach (var (title, content) in dialogs)
            {
                var shown = await ShellTestSupport.ShowDialogAsync(window, title, content);
                window.UpdateLayout();
                await ShellTestSupport.SettleAsync();

                // The dialog has the pad as soon as it opens: nothing outside it is focused.
                Assert.True(Keyboard.FocusedElement is DependencyObject start && Within(start, window.ModalLayer), $"{title}: the pad did not start inside the dialog ({Name(Keyboard.FocusedElement)})");

                foreach (var missed in Unreachable(window, window.ModalLayer))
                    problems.Add($"{title}: {Name(missed)}");

                await Tour(window, window.ModalLayer);

                window.ControllerRouterForTest.Handle(PadButton.Back); // B closes it
                await ShellTestSupport.SettleAsync();
                Assert.False(window.IsModalOpen, $"{title}: B did not close it");
                await shown.Operation;
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }

        Assert.True(problems.Count == 0, "Not reachable by the pad:\n" + string.Join("\n", problems));
    });
}
