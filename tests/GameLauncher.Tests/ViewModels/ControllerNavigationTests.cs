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

    private static LibraryViewModel NewLibrary(int extraGames = 0, bool letters = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Nav-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        if (extraGames > 0)
        {
            var more = Enumerable.Range(0, extraGames)
                .Select(i => new GameEntry { Id = "x" + i, Name = letters ? $"{(char)('A' + i / 9)} game {i % 9}" : $"Game {i:000}", Source = GameSource.Steam, ExecutablePath = $@"C:\G\x{i}.exe", InstallDir = $@"C:\G\x{i}" });
            vm.SimulateRefreshResult(vm.Games.Concat(more).ToList());
        }

        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        vm.SetControllerStatus(true, null); // controller mode needs a controller
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
    private static async Task<(LibraryViewModel Vm, MainWindow Window)> OpenInMode(int width = 1000, int height = 520, int extraGames = 0, bool letters = false)
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(extraGames, letters);
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

            // On a control that draws no ring of its own, the ring sits over it (the PlayStation buttons draw their own: the overlay stays off them).
            Assert.True(MainWindow.HasOwnFocusRing(window.RibbonView.HeroPlay));
            vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Windows", TotalBytes = 1_000_000_000_000, FreeBytes = 400_000_000_000 });
            vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 2_000_000_000_000, FreeBytes = 900_000_000_000 });
            vm.HasDrives = true; // the drive rows draw nothing of their own: the overlay is for them
            await ShellTestSupport.SettleAsync();
            var bare = window.PadControls().Where(c => !MainWindow.HasOwnFocusRing(c) && !FocusNavigator.IsGameTile(c)).ToList();
            Assert.True(bare.Count >= 2, "the home should have controls with no ring of their own");
            var play = bare[0];
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
            var more = bare[1];
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

            // Along the row of drives, with Right, reaching both drives and their options buttons (from the row's first control: Down may have landed further along).
            var all = new HashSet<FrameworkElement>(seenDriveButtons);
            for (var i = 0; i < 4; i++)
            {
                router.Handle(PadButton.Left);
                await ShellTestSupport.SettleAsync();
            }

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
            Assert.Equal(["HIGH PRIORITY", "INSTALLED IN", "PLAY TIME", "SIZE ON DISK", "COLLECTIONS", "ADDED"], cards.Select(c => c.Label)); // priority first; no notes; six cards, three even rows
            Assert.DoesNotContain(cards, c => c.Label == "LAUNCHER");
            Assert.DoesNotContain(cards, c => c.Label.StartsWith("SESSION")); // one play-time card: the Play time page has the sessions

            // Two columns filled row by row: the left column (even places) is every card you can press, the right column the plain facts.
            Assert.All(cards.Where((_, i) => i % 2 == 0), c => Assert.True(c.Clickable, c.Label));
            Assert.All(cards.Where((_, i) => i % 2 == 1), c => Assert.False(c.Clickable, c.Label));

            // The home screen has the game's priority, switched with a press (there is no notes card: notes were taken out of the launcher).
            var priority = cards.Single(c => c.Label == "HIGH PRIORITY");
            Assert.Equal("Normal priority", priority.Value);
            Assert.False(priority.State);                                   // a switch, drawn with its ON / OFF pill
            priority.Run();
            Assert.True(vm.GetRaiseGamePriority(vm.Games.First().Id));
            Assert.True(shell.ActivityCards.Single(c => c.Label == "HIGH PRIORITY").State);
            Assert.Contains("High priority", shell.ActivityCards.Single(c => c.Label == "HIGH PRIORITY").Value);
            priority.Run();
            Assert.False(vm.GetRaiseGamePriority(vm.Games.First().Id));
            Assert.DoesNotContain(shell.ActivityCards, c => c.Label == "NOTES");
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    [Fact]
    public void ThePlayTimeCard_IsTheOnlyOne_AndOpensThePlayTimePage() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            shell.FocusRibbonItem(vm.Games.First());

            var card = shell.ActivityCards.Single(c => c.Label.Contains("PLAY TIME"));
            Assert.Equal("PLAY TIME", card.Label);
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
    public void APickedGame_SendsTheFocusToPlay_AndSaysSo() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1280, 900);
        try
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;             // the swell only plays with Windows' animations on
            var before = GameLauncher.Behaviors.PadPulse.Total;

            window.Shell.RequestPlayFocus();                                          // what picking a game does
            await ShellTestSupport.SettleAsync();

            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
            Assert.Equal(before + 1, GameLauncher.Behaviors.PadPulse.Total);          // Play swelled so it is seen
            Assert.Equal("Press A to play", window.RibbonView.PlayHintText);          // and a tag names the button (A on a pad)

            Keyboard.Focus(window.RibbonView.HeroMore);                               // leaving Play takes the tag away
            await ShellTestSupport.SettleAsync();
            Assert.Null(window.RibbonView.PlayHintText);
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
            Assert.Equal(MainWindow.HasOwnFocusRing(low) ? Visibility.Collapsed : Visibility.Visible, window.FocusRing.Visibility); // marked once: by its own ring, or the overlay

var trail = new List<string>();
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

    // ---- crossing between the Library's panels ------------------------------------------------------------------------------

    private static async Task<(LibraryViewModel Vm, MainWindow Window)> OpenLibrary(bool letters = false)
    {
        var (vm, window) = await OpenInMode(1100, 700, extraGames: 90, letters: letters);
        window.ControllerRouterForTest.Handle(PadButton.Down); // the first press only shows where the pad is
        window.Shell.ShowLibraryCommand.Execute(null);
        await ShellTestSupport.SettleAsync();
        window.UpdateLayout();
        window.EnsurePadFocus();
        await ShellTestSupport.SettleAsync();
        return (vm, window);
    }

    [Fact]
    public void InTheLibrary_LeftFromTheFirstColumn_ReachesTheFilters_FromAnyRow_AndRightComesBackToACard() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary();
        try
        {
            var router = window.ControllerRouterForTest;
            var shell = window.Shell;
            var scroller = window.RibbonView.PosterScroll;
            Assert.Equal(ShellTab.Library, shell.Tab);
            Assert.True(window.RibbonView.GameTileHasFocus);

            for (var i = 0; i < 12; i++) // down the grid to a row well below the top
            {
                router.Handle(PadButton.Down);
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset > 0, "the grid should have scrolled with the pad");
            for (var i = 0; i < 12 && window.RibbonView.GameTileHasFocus; i++) // to the first column of the row it is on
            {
                router.Handle(PadButton.Left);
                await ShellTestSupport.SettleAsync();
            }

            // From the first column, Left goes to the filters - not to the end of the row above.
            Assert.False(window.RibbonView.GameTileHasFocus, $"the pad is on {Keyboard.FocusedElement}, still in the grid");
            Assert.Same(window.RibbonView.FilterAll, Keyboard.FocusedElement); // the chosen filter
            Assert.Equal(0, scroller.VerticalOffset, 1); // the grid is parked back at its top, no card lit (see GoingBackToTheFilters_ParksTheGrid...)

            router.Handle(PadButton.Right); // and back to a card that is on screen
            await ShellTestSupport.SettleAsync();
            var landed = Keyboard.FocusedElement as FrameworkElement;
            var detail = $"{landed?.GetType().Name} {(landed?.DataContext as GameEntry)?.Name} box {(landed is null ? default : FocusNavigator.BoundsIn(landed, window.RootGrid))} viewport {FocusNavigator.BoundsIn(scroller, window.RootGrid)} offset {scroller.VerticalOffset} focusedGame {shell.FocusedGame?.Name}";
            Assert.True(window.RibbonView.GameTileHasFocus, detail);
            Assert.True(FocusNavigator.IsInView(landed!, window.RootGrid), detail);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void EveryLandingOnAFilter_PulsesIt_WhicheverWayThePadCame() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary();
        try
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
            var router = window.ControllerRouterForTest;
            var before = GameLauncher.Behaviors.PadPulse.Total;

            router.Handle(PadButton.Left); // from the first card, back to the filters
            await ShellTestSupport.SettleAsync();
            var landed = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
            Assert.True(FocusNavigator.IsWithin(landed, window.RibbonView.FilterPanel));
            Assert.Equal(before + 1, GameLauncher.Behaviors.PadPulse.Total);
            var scale = Assert.IsType<ScaleTransform>(landed.RenderTransform);
            Assert.True(scale.HasAnimatedProperties);

            router.Handle(PadButton.Down); // along the filters: the next one says so too
            await ShellTestSupport.SettleAsync();
            Assert.Equal(before + 2, GameLauncher.Behaviors.PadPulse.Total);

            router.Handle(PadButton.Right); // into the grid, and back again: pulses again
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            var inGrid = GameLauncher.Behaviors.PadPulse.Total; // (the card the pad lands on pulses too)
            router.Handle(PadButton.Left);
            await ShellTestSupport.SettleAsync();
            Assert.Equal(inGrid + 1, GameLauncher.Behaviors.PadPulse.Total);
        }
        finally
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void RightAtTheEndOfARow_DoesNotRunOnToTheNextRow_ItGoesToTheRail() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var games = vm.Games.ToList();
            window.Shell.FocusRibbonItem(games[0]);
            window.RibbonView.FocusFocusedTile();
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);

            var seen = new List<GameEntry?> { window.Shell.FocusedGame };
            for (var i = 0; i < 14 && window.RibbonView.GameTileHasFocus; i++) // along the first row, and past its end
            {
                router.Handle(PadButton.Right);
                await ShellTestSupport.SettleAsync();
                seen.Add(window.Shell.FocusedGame);
            }

            // Never on to the next row (the games of the first row are the first few in the list), and out of the grid, onto the rail, at its end.
            var firstRow = seen.Distinct().ToList();
            Assert.InRange(firstRow.Count, 2, 8);
            Assert.True(firstRow.All(g => games.IndexOf(g!) < firstRow.Count), "the pad stayed in the first row");
            Assert.False(window.RibbonView.GameTileHasFocus);
            Assert.IsType<LetterItem>(((FrameworkElement)Keyboard.FocusedElement).DataContext);

            // Down is how the next row is reached.
            router.Handle(PadButton.Left);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            var lastOfRow = window.Shell.FocusedGame;
            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.NotSame(lastOfRow, window.Shell.FocusedGame);
            Assert.True(window.RibbonView.GameTileHasFocus);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void WithThePadOnTheRail_TheRightStickScrollsTheGrid_AndThePadsLetterFollowsIt() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var scroller = window.RibbonView.PosterScroll;
            window.EnsurePadFocus();
            for (var i = 0; i < 14 && window.RibbonView.GameTileHasFocus; i++)
            {
                router.Handle(PadButton.Right); // to the rail
                await ShellTestSupport.SettleAsync();
            }

            var rail = Assert.IsType<Button>(Keyboard.FocusedElement);
            var start = Assert.IsType<LetterItem>(rail.DataContext).Letter;

            for (var i = 0; i < 60 && scroller.VerticalOffset < 2600; i++) // the right stick, pushed down a long way (several letters)
            {
                window.ScrollWithStickForTest(-1, 0.1);
                window.UpdateLayout();
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset > 1000, $"offset {scroller.VerticalOffset}");
            var now = Assert.IsType<Button>(Keyboard.FocusedElement);
            var letter = Assert.IsType<LetterItem>(now.DataContext);
            Assert.NotEqual(start, letter.Letter);   // the pad is on the letter the grid has reached...
            Assert.True(letter.IsCurrent);           // ...which is the one the rail has lit
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheLibraryCards_RiseIn_StaggeredAtFirst_AndTheCardAndTheLetterThePadLandsOnSwell() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            // Entrance: every card is set to rise in, the first dozen one after another.
            var anyCard = Walk(window.RibbonView.PosterGrid).OfType<Grid>()
                .First(g => GameLauncher.Behaviors.MotionExtensions.GetEnter(g) == GameLauncher.Behaviors.MotionExtensions.EnterKind.Rise);
            Assert.True(GameLauncher.Behaviors.MotionExtensions.GetEnterDelay(anyCard) >= 0);
            Assert.Equal(0d, GameLauncher.Converters.CardStaggerConverter.Instance.Convert(0, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(5 * GameLauncher.Converters.CardStaggerConverter.StepMs, GameLauncher.Converters.CardStaggerConverter.Instance.Convert(5, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(GameLauncher.Converters.CardStaggerConverter.Staggered * GameLauncher.Converters.CardStaggerConverter.StepMs,
                GameLauncher.Converters.CardStaggerConverter.Instance.Convert(500, typeof(double), null, System.Globalization.CultureInfo.InvariantCulture)); // a long library does not wait

            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
            var router = window.ControllerRouterForTest;
            var before = GameLauncher.Behaviors.PadPulse.Total;

            router.Handle(PadButton.Right); // to the next card: it swells
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.True(GameLauncher.Behaviors.PadPulse.Total > before, "the card the pad lands on should swell");
            var card = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
            Assert.True(Assert.IsType<ScaleTransform>(card.RenderTransform).HasAnimatedProperties);

            // The letter rail: the letter the pad lands on has a ring of its own (so the overlay stays off it) and swells, its neighbours less and less (the wave).
            for (var i = 0; i < 14 && window.RibbonView.GameTileHasFocus; i++)
            {
                router.Handle(PadButton.Right);
                await ShellTestSupport.SettleAsync();
            }

            var letter = Assert.IsType<Button>(Keyboard.FocusedElement);
            var on = Assert.IsType<LetterItem>(letter.DataContext);
            Assert.True(MainWindow.HasOwnFocusRing(letter));
            Assert.True(Assert.IsType<TransformGroup>(letter.RenderTransform).Children[0] is ScaleTransform);
        }
        finally
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheRailFlows_AWaveFollowsThePad_AndTheGridsLetterWhenTheStickScrolls() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var scroller = window.RibbonView.PosterScroll;
            for (var i = 0; i < 14 && window.RibbonView.GameTileHasFocus; i++)
            {
                router.Handle(PadButton.Right); // to the rail
                await ShellTestSupport.SettleAsync();
            }

            var on = Assert.IsType<LetterItem>(((FrameworkElement)Keyboard.FocusedElement).DataContext);
            var scales = window.RibbonView.LetterScales();
            var order = window.Shell.LetterIndex.Select(l => l.Letter).ToList();
            var at = order.IndexOf(on.Letter);
            Assert.Equal(ConsoleRibbonView.WaveScale(0), scales[on.Letter], 3);
            Assert.Equal(1.5, scales[on.Letter], 3);
            if (at + 1 < order.Count)
                Assert.Equal(ConsoleRibbonView.WaveScale(1), scales[order[at + 1]], 3);   // the neighbours follow, less and less
            Assert.True(ConsoleRibbonView.WaveScale(1) < ConsoleRibbonView.WaveScale(0) && ConsoleRibbonView.WaveScale(2) < ConsoleRibbonView.WaveScale(1));
            var far = order.Where((_, i) => Math.Abs(i - at) >= 4).ToList();
            Assert.All(far, l => Assert.Equal(1.0, scales[l], 1));                         // and the far ones rest

            // With the pad off the rail, the wave sits on the letter the grid has reached, and follows the right stick.
            router.Handle(PadButton.Left);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            for (var i = 0; i < 60 && scroller.VerticalOffset < 2600; i++)
            {
                window.ScrollWithStickForTest(-1, 0.1);
                window.UpdateLayout();
                await ShellTestSupport.SettleAsync();
            }

            var current = window.Shell.LetterIndex.First(l => l.IsCurrent).Letter;
            Assert.NotEqual("A", current);
            Assert.Equal(1.5, window.RibbonView.LetterScales()[current], 3);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void CardsScrolledIntoView_ByTheRightStick_RiseIn() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
            await ShellTestSupport.SettleAsync();
            var scroller = window.RibbonView.PosterScroll;
            window.ScrollWithStickForTest(-1, 0.05); // a first small push: the grid notes what is showing
            window.UpdateLayout();
            await ShellTestSupport.SettleAsync();

            var before = GameLauncher.Behaviors.MotionExtensions.Replays;
            for (var i = 0; i < 6; i++)
            {
                window.ScrollWithStickForTest(-1, 0.1);
                window.UpdateLayout();
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset > 100);
            Assert.True(GameLauncher.Behaviors.MotionExtensions.Replays > before, "cards scrolled into view should rise in");
        }
        finally
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void GoingBackToTheFilters_ParksTheGridAtItsTop_WithNoCardLit_AndRightComesBackToTheFirstCard() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var shell = window.Shell;
            var scroller = window.RibbonView.PosterScroll;
            var firstGame = window.Shell.FocusedGame;

            for (var i = 0; i < 10; i++)
            {
                router.Handle(PadButton.Down); // down into the grid
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset > 0);
            Assert.NotNull(shell.RibbonFocus);                         // a card is lit
            Assert.NotSame(firstGame, shell.FocusedGame);
            for (var i = 0; i < 10 && window.RibbonView.GameTileHasFocus; i++)
            {
                router.Handle(PadButton.Left);
                await ShellTestSupport.SettleAsync();
            }

            Assert.False(window.RibbonView.GameTileHasFocus);          // on the filters
            Assert.Null(shell.RibbonFocus);                            // no card is lit any more
            Assert.True(shell.LibraryCardsIdle);
            Assert.Equal(0, scroller.VerticalOffset, 1);               // and the grid is back at its top
            Assert.Same(shell.FocusedGame, vm.Games.First(g => g == shell.FocusedGame));

            router.Handle(PadButton.Right);                            // back in: the first card, at the top
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.GameTileHasFocus);
            Assert.NotNull(shell.RibbonFocus);
            Assert.False(shell.LibraryCardsIdle);
            var tile = (FrameworkElement)Keyboard.FocusedElement;
            Assert.Equal(0, scroller.VerticalOffset, 1);
            Assert.True(FocusNavigator.BoundsIn(tile, window.RootGrid).Top < 330, "the first card is at the top of the grid");
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void SteppingAlongTheRail_FocusesTheFirstGameOfEachLetter_AndScrollsTheGridToIt() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var shell = window.Shell;
            for (var i = 0; i < 14 && window.RibbonView.GameTileHasFocus; i++)
            {
                router.Handle(PadButton.Right); // onto the rail: arriving does not move the grid
                await ShellTestSupport.SettleAsync();
            }

            Assert.Equal(0, window.RibbonView.PosterScroll.VerticalOffset, 1);
            var startLetter = ((LetterItem)((FrameworkElement)Keyboard.FocusedElement).DataContext).Letter;
            var groups = shell.LibraryGroups.ToList();
            var at = groups.FindIndex(g => g.Letter == startLetter);

            router.Handle(PadButton.Down); // the next letter
            await ShellTestSupport.SettleAsync();
            var letter = Assert.IsType<LetterItem>(((FrameworkElement)Keyboard.FocusedElement).DataContext);
            var group = groups.First(g => g.Letter == letter.Letter);
            Assert.NotEqual(startLetter, letter.Letter);
            Assert.Same(group.Games[0], shell.FocusedGame);            // its first game is the focused one
            Assert.NotNull(shell.RibbonFocus);                         // and lit
            Assert.True(window.RibbonView.PosterScroll.VerticalOffset > 0, "the grid scrolled to the letter");
            Assert.True(at >= 0);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void WhenTheRightStickStopsTheGrid_TheFirstGameOfTheTopRowIsFocused_AndThePadGoesWithIt() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary(letters: true);
        try
        {
            var router = window.ControllerRouterForTest;
            var shell = window.Shell;
            var scroller = window.RibbonView.PosterScroll;
            Assert.True(window.RibbonView.GameTileHasFocus);

            for (var i = 0; i < 40 && scroller.VerticalOffset < 1500; i++)
            {
                window.ScrollWithStickForTest(-1, 0.1);
                window.UpdateLayout();
                await ShellTestSupport.SettleAsync();
            }

            Assert.True(scroller.VerticalOffset >= 1500);
            Assert.True(window.RibbonView.GameTileHasFocus);            // the pad was on a card and still is...
            var card = (FrameworkElement)Keyboard.FocusedElement;
            Assert.Same(shell.FocusedGame, card.DataContext);           // ...on the focused game
            var view = FocusNavigator.BoundsIn(scroller, window.RootGrid);
            var box = FocusNavigator.BoundsIn(card, window.RootGrid);
            Assert.InRange(box.Top - view.Top, -4, 360);                // in the top row on screen...
            // ...and the first card of that row: no card of the same row lies to its left.
            var row = vm.Games.Select(g => window.RibbonView.FindPosterForTest(g)).Where(b => b is not null)
                .Select(b => FocusNavigator.BoundsIn(b!, window.RootGrid)).Where(b => Math.Abs(b.Top - box.Top) < 6).ToList();
            Assert.All(row, b => Assert.True(b.Left >= box.Left - 1));
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void FromTheFilters_RightLandsOnACardOnScreen_WhereverTheGridWasScrolledTo() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenLibrary();
        try
        {
            var router = window.ControllerRouterForTest;
            var scroller = window.RibbonView.PosterScroll;
            var first = window.Shell.FocusedGame;

            router.Handle(PadButton.Left); // to the filters
            await ShellTestSupport.SettleAsync();
            Assert.False(window.RibbonView.GameTileHasFocus);

            for (var i = 0; i < 6; i++) // the right stick scrolls the grid a long way down, the pad staying on the filters
            {
                window.ScrollWithStickForTest(-1, 0.1);
                window.UpdateLayout();
            }

            await ShellTestSupport.SettleAsync();
            Assert.True(scroller.VerticalOffset > 200, $"the grid should be well down ({scroller.VerticalOffset})");
            Assert.False(window.RibbonView.GameTileHasFocus);

            router.Handle(PadButton.Right);
            await ShellTestSupport.SettleAsync();

            Assert.True(window.RibbonView.GameTileHasFocus, $"the pad is on {Keyboard.FocusedElement}: no card could be selected");
            var tile = (FrameworkElement)Keyboard.FocusedElement;
            Assert.True(FocusNavigator.IsInView(tile, window.RootGrid), "the card is on screen");
            Assert.NotSame(first, window.Shell.FocusedGame); // a card that is on screen, not the one that was scrolled away
            Assert.Equal(window.Shell.FocusedGame, tile.DataContext);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void AcrossTwoSideBySideScrollAreas_TheCheckedControlIsReached_FromAnyHeight_ButNotAControlScrolledOutOfSight() => sta.RunAsync(async () =>
    {
        var left = new StackPanel();
        var chosen = new System.Windows.Controls.RadioButton { Content = "Chosen", IsChecked = true, Height = 30 };
        left.Children.Add(new System.Windows.Controls.RadioButton { Content = "Other", Height = 30 });
        left.Children.Add(chosen);
        var right = new StackPanel();
        var buttons = Enumerable.Range(0, 40).Select(i => new Button { Content = "Row " + i, Height = 40 }).ToList();
        foreach (var b in buttons)
            right.Children.Add(b);

        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Width = 160 };
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var grid = new Grid { Width = 500, Height = 300 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(rightScroll, 1);
        grid.Children.Add(leftScroll);
        grid.Children.Add(rightScroll);
        var window = new Window { Content = grid, Width = 500, Height = 300, Left = -5000, Top = -5000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        try
        {
            window.UpdateLayout();
            rightScroll.ScrollToVerticalOffset(900); // far down: the first rows are gone, the chosen filter is nowhere near level
            window.UpdateLayout();
            var all = new List<FrameworkElement>(left.Children.OfType<FrameworkElement>().Concat(buttons));
            var visible = buttons.First(b => FocusNavigator.IsInView(b, grid));

            // From a row far below the filters, Left goes to the chosen one - level or not.
            Assert.Same(chosen, FocusNavigator.NextAcrossPanels(visible, all, ShellDirection.Left, grid));

            // From a filter, Right lands on a row that is on screen, not on one scrolled away.
            var across = FocusNavigator.NextAcrossPanels(chosen, all, ShellDirection.Right, grid);
            Assert.NotNull(across);
            Assert.True(FocusNavigator.IsInView(across!, grid));
            Assert.Contains(across, buttons);

            // Up and Down never use this, and a control in the same area is not "across".
            Assert.Null(FocusNavigator.NextAcrossPanels(visible, all, ShellDirection.Up, grid));
            Assert.Null(FocusNavigator.NextAcrossPanels(visible, buttons, ShellDirection.Right, grid));
        }
        finally { window.Close(); }

        await Task.CompletedTask;
    });

    // ---- the pad after the games on screen change ------------------------------------------------------------------------------

    [Fact]
    public void AfterStatsShowThem_ThePadIsOnAGameInTheLibrary_AndCanMoveAndPress() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1100, 760);
        try
        {
            var apex = vm.Games.Single(g => g.Name == "Apex");
            apex.TotalPlaySeconds = 3600; // played: "Not played yet" will not hold it
            apex.LastPlayedUtc = DateTime.UtcNow;
            vm.SearchText = "x";
            vm.SearchText = "";
            window.Shell.FocusRibbonItem(apex);
            await ShellTestSupport.SettleAsync();

            vm.ShowStatsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsPageOpen);

            vm.SelectViewCommand.Execute("unplayed"); // what the page's "Show them" runs
            await ShellTestSupport.SettleAsync();

            Assert.False(vm.IsPageOpen);
            Assert.Equal(ShellTab.Library, window.Shell.Tab);
            Assert.DoesNotContain(apex, vm.Games);
            Assert.Contains(window.Shell.FocusedGame!, vm.Games);   // the focus moved to a game that is shown
            Assert.True(window.RibbonView.GameTileHasFocus, $"the pad is on {Keyboard.FocusedElement}, not a game's tile");
            Assert.Equal(window.Shell.FocusedGame, ((FrameworkElement)Keyboard.FocusedElement).DataContext);

            // And it can be used: the pad has controls to land on, and A starts the game.
            Assert.Contains(window.PadControls(), c => ReferenceEquals(c.DataContext, window.Shell.FocusedGame));
            var router = window.ControllerRouterForTest;
            router.Handle(PadButton.Down);
            await ShellTestSupport.SettleAsync();
            Assert.NotNull(Keyboard.FocusedElement);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void WhateverChangesTheGamesShown_ALeftOutFocusedGameGivesWayToOneThatIsShown() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary();
            var shell = new ShellState(vm);
            var apex = vm.Games.Single(g => g.Name == "Apex");
            var borderlands = vm.Games.Single(g => g.Name == "Borderlands");
            apex.TotalPlaySeconds = 60;
            apex.LastPlayedUtc = DateTime.UtcNow;
            vm.SearchText = "x";
            vm.SearchText = "";

            void Shown(string what)
            {
                Assert.True(shell.FocusedGame is { } g && vm.Games.Contains(g), $"{what}: the focused game {shell.FocusedGame?.Name} is not among the {vm.Games.Count} shown");
            }

            shell.FocusRibbonItem(apex);
            vm.SelectViewCommand.Execute("unplayed");          // a view (Stats, the palette, a card)
            Shown("a view");
            Assert.Same(borderlands, shell.FocusedGame);
            vm.SelectViewCommand.Execute("all");

            shell.FocusRibbonItem(apex);
            vm.SearchText = "Borderlands";                       // a search
            Shown("a search");
            vm.SearchText = "";

            shell.ShowLibraryCommand.Execute(null);
            shell.FocusRibbonItem(borderlands);
            vm.ToggleHiddenCommand.Execute(borderlands);         // the focused game is hidden
            Shown("a game hidden");
            Assert.NotSame(borderlands, shell.FocusedGame);
            vm.ToggleHiddenCommand.Execute(borderlands);

            shell.FocusRibbonItem(apex);
            vm.SelectDriveCommand.Execute("Z:");                 // a drive none of them is on: nothing is shown, so nothing can be fixed - and nothing throws
            Assert.Empty(vm.Games);
            vm.SelectDriveCommand.Execute("Z:");
            Shown("the drive cleared");
            await Task.CompletedTask;
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }
    });

    // ---- coming back to the selected game ------------------------------------------------------------------------------------

    [Fact]
    public void ComingBackUpToTheSelectedTile_MakesItPulse_SoThePadIsPlainlyThere() => sta.RunAsync(async () =>
    {
        var (vm, window) = await OpenInMode(1100, 760);
        try
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;
            var router = window.ControllerRouterForTest;
            var tile = Walk(window.RibbonView).OfType<GameLauncher.Controls.RibbonTile>().Single(t => ReferenceEquals(t.DataContext, window.Shell.FocusedGame));
            Assert.True(tile.IsSelectedItem);
            var before = tile.PulseCount;

            router.Handle(PadButton.Down); // onto Play: the tile stays lit, though the pad has left it
            await ShellTestSupport.SettleAsync();
            Assert.Same(window.RibbonView.HeroPlay, Keyboard.FocusedElement);
            Assert.Equal(before, tile.PulseCount);

            router.Handle(PadButton.Up);   // and back: the tile says so
            await ShellTestSupport.SettleAsync();
            Assert.Same(tile, Keyboard.FocusedElement);
            Assert.Equal(before + 1, tile.PulseCount);
            var scale = Assert.IsType<ScaleTransform>(tile.RenderTransform);
            Assert.True(scale.HasAnimatedProperties);
        }
        finally
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
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
