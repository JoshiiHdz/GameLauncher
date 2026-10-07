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

/// <summary>How the pad moves: which control a direction leads to (pure geometry), the screen scrolling to follow the focus - and back again - the focus ring, the right stick,
/// the Drives on the home screen, and the home's About cards.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerNavigationTests(WpfStaFixture sta)
{
    // ---- the geometry --------------------------------------------------------------------------------------------

    private static int Pick(Rect from, ShellDirection direction, params Rect[] candidates) => SpatialFocus.Pick(from, candidates, direction);

    [Fact]
    public void InAGrid_EachDirectionGoesToTheNeighbour_AndStopsAtTheEdge()
    {
        // Three rows of three, 100 x 40 with 10 between.
        var cells = new Rect[3, 3];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                cells[r, c] = new Rect(c * 110, r * 50, 100, 40);

        var all = cells.Cast<Rect>().ToList();
        int Go(int r, int c, ShellDirection d) => SpatialFocus.Pick(cells[r, c], all, d);

        Assert.Equal(all.IndexOf(cells[1, 2]), Go(1, 1, ShellDirection.Right));
        Assert.Equal(all.IndexOf(cells[1, 0]), Go(1, 1, ShellDirection.Left));
        Assert.Equal(all.IndexOf(cells[2, 1]), Go(1, 1, ShellDirection.Down));
        Assert.Equal(all.IndexOf(cells[0, 1]), Go(1, 1, ShellDirection.Up));

        Assert.Equal(-1, Go(0, 0, ShellDirection.Up));
        Assert.Equal(-1, Go(0, 0, ShellDirection.Left));
        Assert.Equal(-1, Go(2, 2, ShellDirection.Down));
        Assert.Equal(-1, Go(2, 2, ShellDirection.Right));
    }

    [Fact]
    public void LeftAndRight_StayInTheirOwnRow_NeverJumpingToAnotherRow()
    {
        var focused = new Rect(0, 0, 100, 40);
        var otherRow = new Rect(150, 200, 100, 40); // to the right, but on a different row

        Assert.Equal(-1, Pick(focused, ShellDirection.Right, otherRow));
        Assert.Equal(0, Pick(focused, ShellDirection.Right, new Rect(150, 10, 100, 40))); // in the row: fine
    }

    [Fact]
    public void UpAndDown_PreferWhatIsStraightAhead_ButNotWhenSomethingIsClearlyCloser()
    {
        // The home screen: the Pick-a-game tile (x 362..489) with the About card (x 48..318) just above and to the left, and Uninstall (x 362..452) far above it.
        var tile = new Rect(362, 844, 127, 44);
        var card = new Rect(48, 726, 270, 63);
        var uninstall = new Rect(362, 503, 90, 40);

        Assert.Equal(0, Pick(tile, ShellDirection.Up, card, uninstall));  // the card: it is right there, even though Uninstall is straight above
    }

    [Fact]
    public void GoingDownARowOfLeftAlignedPages_EntersAtTheFirstControl()
    {
        var wideCard = new Rect(48, 726, 270, 63);                        // a card, centre x 183
        var first = new Rect(48, 844, 95, 44);                            // the first tile of the next row, centre x 95
        var second = new Rect(151, 844, 85, 44);                          // the second, centre x 193 - nearer the card's centre

        Assert.Equal(0, Pick(wideCard, ShellDirection.Down, first, second));
    }

    [Fact]
    public void WithNothingStraightAhead_TheNearestControlThatWayWins()
    {
        var focused = new Rect(1755, 39, 38, 38);                         // a button at the far right of the top bar
        var tile = new Rect(206, 96, 216, 216);
        var farther = new Rect(48, 499, 180, 48);

        Assert.Equal(0, Pick(focused, ShellDirection.Down, tile, farther));
    }

    // ---- scrolling follows the focus -------------------------------------------------------------------------------

    private static (ScrollViewer Scroller, List<FrameworkElement> Controls) MakePage(int buttons = 16)
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false; // instant, unless a test of the glide turns it on
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "A page title and a note under it", Height = 70 }); // not focusable
        var controls = new List<FrameworkElement>();
        for (var i = 0; i < buttons; i++)
        {
            var button = new Button { Content = "Option " + i, Height = 40, Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(button);
            controls.Add(button);
        }

        var scroller = new ScrollViewer { Width = 300, Height = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        scroller.Measure(new Size(300, 230));
        scroller.Arrange(new Rect(0, 0, 300, 230));
        scroller.UpdateLayout();
        return (scroller, controls);
    }

    private static bool InView(ScrollViewer scroller, FrameworkElement element)
    {
        var box = element.TransformToAncestor(scroller).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return box.Top >= -0.5 && box.Bottom <= scroller.ViewportHeight + 0.5;
    }

    [Fact]
    public void TheFocusedControl_IsScrolledIntoView_AndScrollingGoesBackUpToTheTitle() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();
        Assert.True(scroller.ScrollableHeight > 0);

        FocusNavigator.EnsureVisible(controls[9], controls);
        scroller.UpdateLayout();
        Assert.True(InView(scroller, controls[9]));
        Assert.True(scroller.VerticalOffset > 0);

        FocusNavigator.EnsureVisible(controls[0], controls); // back to the first option: its page title must be on screen again, not just the button
        scroller.UpdateLayout();
        Assert.Equal(0, scroller.VerticalOffset);

        FocusNavigator.EnsureVisible(controls[^1], controls);
        scroller.UpdateLayout();
        Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset, 1);   // the last option shows the very bottom
        await Task.CompletedTask;
    });

    [Fact]
    public void WalkingDownThenBackUp_EveryStepKeepsTheFocusedControlOnScreen_AndEndsAtTheTop() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();

        foreach (var control in controls)
        {
            FocusNavigator.EnsureVisible(control, controls);
            scroller.UpdateLayout();
            Assert.True(InView(scroller, control), $"{((Button)control).Content} is off screen going down");
        }

        for (var i = controls.Count - 1; i >= 0; i--)
        {
            FocusNavigator.EnsureVisible(controls[i], controls);
            scroller.UpdateLayout();
            Assert.True(InView(scroller, controls[i]), $"{((Button)controls[i]).Content} is off screen coming back up");
        }

        Assert.Equal(0, scroller.VerticalOffset); // and the title is back
        await Task.CompletedTask;
    });

    [Fact]
    public void AMiddleControl_ScrollsOnlyAsFarAsNeeded() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();
        FocusNavigator.EnsureVisible(controls[8], controls);
        scroller.UpdateLayout();
        var before = scroller.VerticalOffset;

        FocusNavigator.EnsureVisible(controls[7], controls); // one row up, already in view
        scroller.UpdateLayout();

        Assert.True(InView(scroller, controls[7]));
        Assert.True(Math.Abs(scroller.VerticalOffset - before) <= 60, "it should not jump a whole page for one step");
        await Task.CompletedTask;
    });

    // ---- the right stick ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRightStick_IsAPushOnlyOutsideItsDeadZone_UpIsPositive()
    {
        Assert.Equal(0, ControllerInput.StickPush(0));
        Assert.Equal(0, ControllerInput.StickPush(5000));      // a resting stick that drifts a little does nothing
        Assert.Equal(0, ControllerInput.StickPush(-7900));
        Assert.True(ControllerInput.StickPush(16000) > 0);
        Assert.True(ControllerInput.StickPush(-16000) < 0);
        Assert.Equal(1.0, ControllerInput.StickPush(32767), 3);
        Assert.Equal(-1.0, ControllerInput.StickPush(-32767), 3);
        Assert.True(ControllerInput.StickPush(30000) > ControllerInput.StickPush(15000)); // the harder the push, the bigger
    }

    [Fact]
    public void TheRightStick_RaisesAnEventEveryReading_WhileItIsHeld_WithTheTimeItCovers()
    {
        long now = 0;
        PadReading? reading = new PadReading(0, 0, 0, 0, 0, 0, 32767);
        var pushes = new List<(double X, double Y, double Seconds)>();
        var input = new ControllerInput(i => i == 0 ? reading : null, () => now);
        input.RightStick += (x, y, s) => pushes.Add((x, y, s));

        input.Poll();
        now += 33; input.Poll();
        now += 33; input.Poll();
        reading = new PadReading(0, 0, 0, 0, 0, 0, 0); // let go
        now += 33; input.Poll();

        Assert.Equal(3, pushes.Count);
        Assert.All(pushes, p => Assert.Equal(1.0, p.Y, 3));
        Assert.Equal(0.033, pushes[1].Seconds, 3);
        Assert.Equal(0.033, pushes[2].Seconds, 3);
    }

    [Fact]
    public void TheRightStick_IsIgnored_WhenButtonsAreNotBeingRead()
    {
        var pushes = 0;
        var input = new ControllerInput(i => i == 0 ? new PadReading(0, 0, 0, 0, 0, 0, 32767) : null, () => 0, () => false);
        input.RightStick += (_, _, _) => pushes++;

        input.Poll();

        Assert.Equal(0, pushes); // another window or a game has the focus: the stick is not ours
    }

    // ---- real windows ----------------------------------------------------------------------------------------------------

    private static LibraryViewModel NewLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Nav-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        return vm;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    /// <summary>A real window in controller mode, then made small (the mode makes it fill the screen) so that pages have to scroll.</summary>
    private static async Task<(LibraryViewModel Vm, MainWindow Window)> OpenInMode(int width = 1000, int height = 520)
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm, width, height);
        window.Activate();
        await ShellTestSupport.SettleAsync();
        vm.EnterControllerMode();
        await ShellTestSupport.SettleAsync();
        window.Width = width;
        window.Height = height;
        window.Left = -5000;
        window.Top = -5000;
        await ShellTestSupport.SettleAsync();
        window.UpdateLayout();
        return (vm, window);
    }

    [Fact]
    public void InSettings_GoingDownScrollsTheOptionsIn_AndGoingBackUpScrollsThemBackToTheTop() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(900, 480);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();
            page.CategoryList.SelectedItem = page.GeneralCategory;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var scroller = Assert.IsType<ScrollViewer>(page.GeneralSection);
            Assert.True(scroller.ScrollableHeight > 0, "the page should need scrolling in a small window");

            var router = window.ControllerRouterForTest;
            Keyboard.Focus(page.GeneralCategory);
            router.Handle(PadButton.Right); // into the options
            await ShellTestSupport.SettleAsync();

            for (var i = 0; i < 40; i++) // all the way down
            {
                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();
            }

            var last = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
            Assert.True(scroller.VerticalOffset > 0, $"after going down the offset is {scroller.VerticalOffset} (scrollable {scroller.ScrollableHeight}); focus is on {Keyboard.FocusedElement}");
            Assert.True(FocusNavigator.BoundsIn(last, scroller).Bottom <= scroller.ViewportHeight + 1, "the last option is on screen");

            for (var i = 0; i < 40; i++) // and all the way back up
            {
                router.Handle(PadButton.Up);
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset == 0, $"back up the offset is {scroller.VerticalOffset}; focus is on {Keyboard.FocusedElement}"); // the title and its note showing again
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheRightStick_ScrollsThePage_UpAndDown_WithoutMovingTheFocus() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(900, 480);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Walk(window.PageHost).OfType<SettingsPage>().Single();
            page.CategoryList.SelectedItem = page.GeneralCategory;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var scroller = page.GeneralSection;
            Assert.True(scroller.ScrollableHeight > 0);
            Keyboard.Focus(page.GeneralCategory);
            await ShellTestSupport.SettleAsync();

            var stick = window.ScrollerForStick();
            Assert.NotNull(stick);                            // the options, not the category list

            window.ScrollWithStickForTest(-1, 0.1);           // pushed down: the page moves on
            window.UpdateLayout();
            Assert.True(scroller.VerticalOffset > 0, $"after going down the offset is {scroller.VerticalOffset} (scrollable {scroller.ScrollableHeight}); focus is on {Keyboard.FocusedElement}");
            var down = scroller.VerticalOffset;

            window.ScrollWithStickForTest(1, 0.05);           // pushed up, for a shorter time: it comes back a little
            window.UpdateLayout();
            Assert.True(scroller.VerticalOffset < down);

            window.ScrollWithStickForTest(1, 5);              // and a long push up stops at the top
            window.UpdateLayout();
            Assert.Equal(0, scroller.VerticalOffset);
            window.ScrollWithStickForTest(-1, 50);            // a long push down stops at the bottom
            window.UpdateLayout();
            Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset, 1);

            Assert.Same(page.GeneralCategory, Keyboard.FocusedElement); // the focus did not move
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the focus ring --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFocusRing_IsDrawnOverTheControlThePadIsOn_NotOverGameTiles_AndStepsAsideForTheMouse() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1000, 760);
        try
        {
            // On a game tile the tile highlights itself: no ring.
            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.True(window.FocusRing.Visibility == Visibility.Collapsed, $"ring step 1: expected Visibility.Collapsed but it is {window.FocusRing.Visibility}; focus on {Keyboard.FocusedElement}");

            // On any other control the ring sits over it.
            var play = window.RibbonView.HeroPlay;
            window.NotePadUse(); // a stray pointer report from Windows between steps must not decide this test: the pad is in use
            Keyboard.Focus(play);
            await ShellTestSupport.SettleAsync();
            window.NotePadUse();
            Assert.True(window.FocusRing.Visibility == Visibility.Visible, $"ring step 2: expected Visibility.Visible but it is {window.FocusRing.Visibility}; focus on {Keyboard.FocusedElement}");
            var box = FocusNavigator.BoundsIn(play, window.RootGrid);
            Assert.Equal(box.Left - 4, Canvas.GetLeft(window.FocusRing), 1);
            Assert.Equal(box.Top - 4, Canvas.GetTop(window.FocusRing), 1);
            Assert.Equal(box.Width + 8, window.FocusRing.Width, 1);
            Assert.Equal(box.Height + 8, window.FocusRing.Height, 1);

            // It follows the focus to the next control.
            var more = window.RibbonView.HeroMore;
            Keyboard.Focus(more);
            await ShellTestSupport.SettleAsync();
            window.NotePadUse();
            Assert.Equal(FocusNavigator.BoundsIn(more, window.RootGrid).Left - 4, Canvas.GetLeft(window.FocusRing), 1);

            // And is gone while the mouse is in use. These steps are synchronous (no waiting for Windows between them), so a stray pointer report cannot land in the middle.
            window.NotePointerAt(new Point(100, 100));   // wherever the pointer already is
            window.NotePadUse();                          // and the pad is used
            window.NotePointerAt(new Point(100, 100.5)); // a flicker, not a move
            window.UpdateFocusRing();
            Assert.True(window.FocusRing.Visibility == Visibility.Visible, $"ring step 3: expected Visibility.Visible but it is {window.FocusRing.Visibility}; focus on {Keyboard.FocusedElement}");
            window.NotePointerAt(new Point(160, 140));   // a real move
            Assert.True(window.FocusRing.Visibility == Visibility.Collapsed, $"ring step 4: expected Visibility.Collapsed but it is {window.FocusRing.Visibility}; focus on {Keyboard.FocusedElement}");

            // Outside controller mode there is no ring at all.
            vm.ExitControllerMode();
            Keyboard.Focus(play);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.FocusRing.Visibility == Visibility.Collapsed, $"ring step 5: expected Visibility.Collapsed but it is {window.FocusRing.Visibility}; focus on {Keyboard.FocusedElement}");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the Drives on the home screen -------------------------------------------------------------------------------------

    [Fact]
    public void TheDrives_CanBeReachedWithThePad_AndShowWhereTheFocusIs() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Windows", TotalBytes = 1_000_000_000_000, FreeBytes = 400_000_000_000 });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 2_000_000_000_000, FreeBytes = 900_000_000_000 });
        vm.HasDrives = true;
        var window = ShellTestSupport.OpenMain(vm, 1000, 760);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            var router = window.ControllerRouterForTest;

            var seenDriveButtons = new HashSet<FrameworkElement>();
            for (var i = 0; i < 14; i++) // Down from the tile, through the hero, the cards, Quick access, to the Drives
            {
                window.NotePadUse(); // as a real press does
                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();
                if (Keyboard.FocusedElement is FrameworkElement { DataContext: DriveSpaceInfo } drive)
                {
                    seenDriveButtons.Add(drive);

                    // The pad is plainly somewhere: the ring is drawn over this drive's button.
                    Assert.Equal(Visibility.Visible, window.FocusRing.Visibility);
                    var box = FocusNavigator.BoundsIn(drive, window.RootGrid);
                    Assert.Equal(box.Left - 4, Canvas.GetLeft(window.FocusRing), 1);
                    Assert.Equal(box.Top - 4, Canvas.GetTop(window.FocusRing), 1);
                    break; // the first drive is enough here; the next lines walk along the row from it
                }
            }

            Assert.NotEmpty(seenDriveButtons);

            // Along the row of drives, with Right, reaching both drives and their options buttons.
            var all = new HashSet<FrameworkElement>(seenDriveButtons);
            for (var i = 0; i < 8; i++)
            {
                router.Handle(PadButton.Right);
                await ShellTestSupport.SettleAsync();
                if (Keyboard.FocusedElement is FrameworkElement { DataContext: DriveSpaceInfo } drive)
                    all.Add(drive);
            }

            Assert.True(all.Count >= 3, $"the drives and their option buttons should all be reachable (reached {all.Count})");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the home screen's About cards and cover ------------------------------------------------------------------------------

    [Fact]
    public void TheAboutCards_PutEveryPressableCardOnTheLeft_AndTheLauncherCardIsGone() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            shell.FocusRibbonItem(vm.Games.First());

            var cards = shell.ActivityCards.ToList();
            Assert.Equal(["SESSION PLAY TIME ›", "INSTALLED IN", "PLAY TIME ›", "SIZE ON DISK", "COLLECTIONS ›", "ADDED"], cards.Select(c => c.Label));
            Assert.DoesNotContain(cards, c => c.Label == "LAUNCHER");

            // Two columns filled row by row: the left column (even places) is every card you can press, the right column the plain facts.
            Assert.All(cards.Where((_, i) => i % 2 == 0), c => Assert.True(c.Clickable, c.Label));
            Assert.All(cards.Where((_, i) => i % 2 == 1), c => Assert.False(c.Clickable, c.Label));
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void TheSessionCard_OpensPlayTime_AndNamesTheLastSession() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            shell.FocusRibbonItem(vm.Games.First());

            var card = shell.ActivityCards.Single(c => c.Label == "SESSION PLAY TIME ›");
            Assert.Equal("No sessions yet", card.Value); // nothing played yet
            Assert.True(card.Clickable);

            card.Run();

            Assert.Equal(LibraryViewModel.PlayTimePageKey, vm.CurrentPageKey);
            Assert.Same(shell.FocusedGame, Assert.IsType<PlayTimeViewModel>(vm.CurrentPage).Game);
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void TheGameCard_IsBackOnTheRight_WhereTheSessionsPanelWas() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm, 1280, 760);
        try
        {
            await ShellTestSupport.SettleAsync();
            window.Shell.FocusRibbonItem(vm.Games.First());
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var cover = window.RibbonView.HeroCover;
            Assert.True(cover.IsVisible);
            Assert.Equal(HorizontalAlignment.Right, cover.HorizontalAlignment);
            var box = FocusNavigator.BoundsIn(cover, window.RootGrid);
            Assert.True(box.Right > window.ActualWidth * 0.75, "the card sits at the right of the home");
            Assert.True(box.Height > box.Width, "a portrait card, like a game's cover");

            // For the Library tile there is no game, so no card.
            window.Shell.FocusRibbonItem(null);
            await ShellTestSupport.SettleAsync();
            Assert.False(cover.IsVisible);
            Assert.Null(window.RibbonView.FindName("SessionsPanel")); // the old floating panel is gone
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void InARealWindow_TheTopBarAndTheStripAreOneAboveTheOther() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1280, 900);
        try
        {
            var router = window.ControllerRouterForTest;
            Assert.True(window.RibbonView.GameTileHasFocus);

            // Up from the strip is the tab of the screen that is showing.
            router.Handle(PadButton.Up);
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.HomeTab, Keyboard.FocusedElement);

            // Right along the top bar, and Down from any of its buttons is the focused game's tile again - not something far below.
            foreach (var expected in new FrameworkElement[] { window.RibbonView.LibraryTab, window.RibbonView.SearchButton, window.RibbonView.ControllerButton })
            {
                router.Handle(PadButton.Right);
                await ShellTestSupport.SettleAsync();
                Assert.Same(expected, Keyboard.FocusedElement);
            }

            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.Same(vm.Games.First(), window.Shell.FocusedGame);

            // The Library tab: the same, with its own tab.
            window.Shell.ShowLibraryCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.TryFocusTile());
            router.Handle(PadButton.Up);
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.LibraryTab, Keyboard.FocusedElement);
            router.Handle(PadButton.Down);
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
    public void DownThenUpFromThePlayButton_ComesBackToTheSameGame() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1280, 900);
        try
        {
            var router = window.ControllerRouterForTest;
            router.Handle(PadButton.Right); // the second game's tile
            await ShellTestSupport.SettleAsync();
            var game = window.Shell.FocusedGame;

            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);

            router.Handle(PadButton.Right); // More
            await ShellTestSupport.SettleAsync();
            router.Handle(PadButton.Up);
            await ShellTestSupport.SettleAsync();

            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.Same(game, window.Shell.FocusedGame); // from any hero button, Up is the tile of the game whose hero it is
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void OnTheHome_WalkingDownToTheLauncherChipsAndBackUp_ScrollsInAndBackToTheTop() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1000, 600);
        try
        {
            vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Windows", TotalBytes = 1_000_000_000_000, FreeBytes = 400_000_000_000 });
            vm.HasDrives = true;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var scroller = window.RibbonView.HomeColumn;
            Assert.True(scroller.ScrollableHeight > 0, "the home should need scrolling in a short window");

            var router = window.ControllerRouterForTest;
            router.Handle(PadButton.Down); // onto Play
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);

            for (var i = 0; i < 12; i++) // cards, Quick access, drives, launchers
            {
                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();
            }

            var low = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
            Assert.True(scroller.VerticalOffset > 0);
            Assert.True(FocusNavigator.BoundsIn(low, scroller).Bottom <= scroller.ViewportHeight + 1, "the control the pad is on is on screen");
            window.NotePadUse(); // as a real press does: a stray pointer report must not decide this
            Assert.Equal(Visibility.Visible, window.FocusRing.Visibility); // and its ring is drawn there

for (var i = 0; i < 15 && !ReferenceEquals(Keyboard.FocusedElement, window.RibbonView.HeroPlay); i++) // and back up to Play
            {
                router.Handle(PadButton.Up);
                await ShellTestSupport.SettleAsync();
            }

            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
            Assert.True(scroller.VerticalOffset == 0, $"back on Play the home should be back at its top, but it is scrolled {scroller.VerticalOffset}");

            // The right stick scrolls the same area, wherever the pad is.
            window.ScrollWithStickForTest(-1, 0.1);
            window.UpdateLayout();
            Assert.True(scroller.VerticalOffset > 0);
            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    // ---- the eased scroll ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithAnimationsOn_TheScrollGlidesToItsTarget_AndANewTargetRedirectsIt() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
        try
        {
            FocusNavigator.EnsureVisible(controls[12], controls);
            scroller.UpdateLayout();
            var target = GameLauncher.Behaviors.SmoothScroll.TargetOf(scroller);
            Assert.True(target > 0);
            Assert.True(scroller.VerticalOffset < target, "it should still be on its way, not there already");

            await Task.Delay(700);
            scroller.UpdateLayout();
            Assert.Equal(target, scroller.VerticalOffset, 1);
            Assert.True(InView(scroller, controls[12]));

            // A new target while it is gliding redirects the glide rather than waiting.
            FocusNavigator.EnsureVisible(controls[0], controls);
            await Task.Delay(100);
            FocusNavigator.EnsureVisible(controls[^1], controls);
            await Task.Delay(900);
            scroller.UpdateLayout();
            Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset, 1);
        }
        finally { GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false; }
    });

    [Fact]
    public void AGlideInProgress_IsWorkedOutFromWhereItIsHeading_SoQuickPressesLandRight() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
        try
        {
            FocusNavigator.EnsureVisible(controls[9], controls);   // set off for the ninth ...
            scroller.UpdateLayout();
            FocusNavigator.EnsureVisible(controls[10], controls);  // ... and, before it has got there, the tenth
            FocusNavigator.EnsureVisible(controls[11], controls);
            await Task.Delay(900);
            scroller.UpdateLayout();

            Assert.True(InView(scroller, controls[11])); // where it ends up has the last one pressed in view, as if each press had waited for the one before
        }
        finally { GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false; }
    });

    [Fact]
    public void ScrollingByHand_StopsAGlide() => sta.RunAsync(async () =>
    {
        var (scroller, controls) = MakePage();
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
        try
        {
            FocusNavigator.EnsureVisible(controls[14], controls);
            await Task.Delay(60);
            GameLauncher.Behaviors.SmoothScroll.Stop(scroller); // what the right stick does before it scrolls

            scroller.UpdateLayout();
            var where = scroller.VerticalOffset;
            await Task.Delay(400);
            scroller.UpdateLayout();
            Assert.Equal(where, scroller.VerticalOffset, 1); // it does not carry on to the old target behind the stick's back
        }
        finally { GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false; }
    });

    // ---- the A to Z rail -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheLetterRail_CanBeReachedWithThePad_AJumpsToTheLetter_AndLeftComesBackToTheGrid() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1280, 900);
        try
        {
            window.Shell.ShowLibraryCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            Assert.True(window.Shell.ShowLetterIndex);

            var letters = Walk(window.RibbonView).OfType<Button>().Where(b => b.DataContext is LetterItem && b.IsEnabled && b.IsVisible).ToList();
            Assert.Equal(["A", "B"], letters.Select(b => ((LetterItem)b.DataContext).Letter).OrderBy(l => l)); // only the letters that have games

            var router = window.ControllerRouterForTest;
            window.Shell.FocusRibbonItem(vm.Games.Last()); // the last game of the grid: nothing further right in it
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.TryFocusTile());

            router.Handle(PadButton.Right);
            await ShellTestSupport.SettleAsync();
            Assert.True(Keyboard.FocusedElement is Button { DataContext: LetterItem }, $"Right from the end of the grid should reach the rail, but the focus is on {Keyboard.FocusedElement}");

            // A on a letter jumps the grid to it.
            var b = letters.Single(l => ((LetterItem)l.DataContext).Letter == "B");
            Keyboard.Focus(b);
            window.Shell.FocusRibbonItem(vm.Games.First()); // somewhere else in the library first
            Keyboard.Focus(b);
            router.Handle(PadButton.Accept);
            await ShellTestSupport.SettleAsync();
            Assert.Equal("Borderlands", window.Shell.FocusedGame!.Name);

            // Left from the rail goes back to the game it landed on.
            router.Handle(PadButton.Left);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.Equal("Borderlands", window.Shell.FocusedGame!.Name);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
