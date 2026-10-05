using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Big-screen mode: reading a controller (with key repeat), the carousel's navigation, what the shelves hold, the real
/// full-screen window with keyboard and controller input, and how the library launches the chosen game.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class BigScreenTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => SystemParameters.ClientAreaAnimation;
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    // ---- the controller ----------------------------------------------------------------------------

    private const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Lb = 0x100, Rb = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private sealed class Pad
    {
        public GamepadState? Slot0;
        public long Now;
        public readonly List<GamepadButton> Fired = [];
        public readonly GamepadService Service;

        public Pad()
        {
            Service = new GamepadService(i => i == 0 ? Slot0 : null, () => Now);
            Service.ButtonPressed += Fired.Add;
        }

        public void Hold(ushort buttons, short lx = 0, short ly = 0) => Slot0 = new GamepadState(buttons, lx, ly);

        public void Release() => Slot0 = new GamepadState(0, 0, 0);
    }

    [Fact]
    public void AButtonFiresOncePerPress_NotOnEveryReading()
    {
        var pad = new Pad();
        pad.Hold(A);

        pad.Service.Poll();
        pad.Now += 33;
        pad.Service.Poll();
        pad.Now += 1000;
        pad.Service.Poll();

        Assert.Equal([GamepadButton.Accept], pad.Fired);

        pad.Release();
        pad.Service.Poll();
        pad.Hold(A);
        pad.Service.Poll();
        Assert.Equal([GamepadButton.Accept, GamepadButton.Accept], pad.Fired);
    }

    [Fact]
    public void EveryButtonMapsToItsAction()
    {
        var pad = new Pad();
        foreach (var (mask, expected) in new (ushort, GamepadButton)[]
                 {
                     (Up, GamepadButton.Up), (Down, GamepadButton.Down), (Left, GamepadButton.Left), (Right, GamepadButton.Right),
                     (A, GamepadButton.Accept), (B, GamepadButton.Back), (Y, GamepadButton.Favorite),
                     (Lb, GamepadButton.PreviousShelf), (Rb, GamepadButton.NextShelf),
                 })
        {
            pad.Fired.Clear();
            pad.Hold(mask);
            pad.Service.Poll();
            Assert.Equal([expected], pad.Fired);
            pad.Release();
            pad.Service.Poll();
        }
    }

    [Fact]
    public void AnUnmappedButton_DoesNothing()
    {
        var pad = new Pad();
        pad.Hold(X);

        pad.Service.Poll();

        Assert.Empty(pad.Fired);
    }

    [Fact]
    public void ADirectionHeld_RepeatsAfterADelay_ThenAtASteadyRate()
    {
        var pad = new Pad();
        pad.Hold(Right);

        pad.Service.Poll();                                                    // t=0: the press
        pad.Now = 300; pad.Service.Poll();                                     // inside the delay
        Assert.Single(pad.Fired);
        pad.Now = GamepadService.RepeatDelayMs; pad.Service.Poll();            // delay over: first repeat
        Assert.Equal(2, pad.Fired.Count);
        pad.Now += GamepadService.RepeatIntervalMs - 10; pad.Service.Poll();   // too soon for the next
        Assert.Equal(2, pad.Fired.Count);
        pad.Now += 10; pad.Service.Poll();                                     // on time
        Assert.Equal(3, pad.Fired.Count);
    }

    [Fact]
    public void OnlyDirectionsRepeat_AHeldAcceptIsNeverFiredTwice()
    {
        var pad = new Pad();
        pad.Hold(A);
        pad.Service.Poll();

        pad.Now = 5000;
        pad.Service.Poll();

        Assert.Single(pad.Fired);
    }

    [Theory]
    [InlineData(0, 20000, GamepadButton.Up)]
    [InlineData(0, -20000, GamepadButton.Down)]
    [InlineData(-20000, 0, GamepadButton.Left)]
    [InlineData(20000, 0, GamepadButton.Right)]
    public void TheLeftStick_ActsAsTheDpad_PastItsDeadZone(short x, short y, GamepadButton expected)
    {
        var pad = new Pad();
        pad.Hold(0, x, y);

        pad.Service.Poll();

        Assert.Equal([expected], pad.Fired);
    }

    [Fact]
    public void AStickThatIsOnlyResting_DoesNothing()
    {
        var pad = new Pad();
        pad.Hold(0, 9000, -7000); // a loose stick, well inside the dead zone

        pad.Service.Poll();

        Assert.Empty(pad.Fired);
    }

    [Fact]
    public void NoControllerPluggedIn_IsNotAnError()
    {
        var service = new GamepadService(_ => null);
        var fired = false;
        service.ButtonPressed += _ => fired = true;

        service.Poll();

        Assert.False(fired);
    }

    [Fact]
    public void AnySecondController_IsHeardToo()
    {
        var states = new Dictionary<int, GamepadState?> { [2] = new GamepadState(A, 0, 0) };
        var service = new GamepadService(i => states.GetValueOrDefault(i), () => 0);
        var fired = new List<GamepadButton>();
        service.ButtonPressed += fired.Add;

        service.Poll();

        Assert.Equal([GamepadButton.Accept], fired);
    }

    [Fact]
    public void TheRealXInput_CanBeAskedOnThisMachine_WithoutAnyControllerOrError()
    {
        var service = new GamepadService();

        service.Poll();
        service.Poll();
        service.Dispose();
    }

    // ---- the carousel ------------------------------------------------------------------------------

    private static GameEntry Game(string id, string name) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = GameSource.Manual,
    };

    private static BigScreenViewModel Screen() => new(
    [
        new BigShelf("Jump back in", [Game("a", "Apex"), Game("b", "Borderlands")]),
        new BigShelf("All games", [Game("a2", "Apex"), Game("b2", "Borderlands"), Game("c", "Celeste"), Game("d", "Doom")]),
    ]);

    [Fact]
    public void ItOpensOnTheFirstGameOfTheFirstShelf()
    {
        var screen = Screen();

        Assert.Equal("Jump back in", screen.ShelfName);
        Assert.Equal("Apex", screen.SelectedGame!.Name);
        Assert.Equal("1 of 2", screen.ShelfPositionText);
        Assert.True(screen.HasGames);
    }

    [Fact]
    public void MovingAlongAShelf_StopsAtBothEnds()
    {
        var screen = Screen();

        screen.Move(-1);
        Assert.Equal(0, screen.SelectedIndex);
        screen.Move(1);
        screen.Move(1);
        screen.Move(1);

        Assert.Equal(1, screen.SelectedIndex);
        Assert.Equal("Borderlands", screen.SelectedGame!.Name);
        Assert.Equal("2 of 2", screen.ShelfPositionText);
    }

    [Fact]
    public void ChangingShelf_Wraps_AndRemembersWhereYouWereOnEach()
    {
        var screen = Screen();
        screen.Move(1);                   // Borderlands on shelf 0

        screen.MoveShelf(1);
        Assert.Equal("All games", screen.ShelfName);
        Assert.Equal(0, screen.SelectedIndex);
        screen.Move(2);                   // Celeste on shelf 1

        screen.MoveShelf(1);              // wraps back to shelf 0
        Assert.Equal("Jump back in", screen.ShelfName);
        Assert.Equal("Borderlands", screen.SelectedGame!.Name);

        screen.MoveShelf(-1);             // and back to shelf 1, at Celeste
        Assert.Equal("Celeste", screen.SelectedGame!.Name);
    }

    [Fact]
    public void TwoShelves_LandingOnTheSameIndex_StillShowTheirOwnGame()
    {
        var screen = new BigScreenViewModel([new BigShelf("One", [Game("a", "Apex")]), new BigShelf("Two", [Game("z", "Zelda")])]);

        screen.MoveShelf(1);

        Assert.Equal("Zelda", screen.SelectedGame!.Name);
        Assert.Equal("1 of 1", screen.ShelfPositionText);
    }

    [Fact]
    public void ASingleShelf_DoesNotMove()
    {
        var screen = new BigScreenViewModel([new BigShelf("Only", [Game("a", "Apex"), Game("b", "Borderlands")])]);
        screen.Move(1);

        screen.MoveShelf(1);

        Assert.Equal("Only", screen.ShelfName);
        Assert.Equal("Borderlands", screen.SelectedGame!.Name);
    }

    [Fact]
    public void Accepting_RecordsTheGame_AndAsksTheWindowToClose()
    {
        var screen = Screen();
        screen.Move(1);
        var closed = 0;
        screen.CloseRequested += () => closed++;

        screen.Accept();

        Assert.Equal("Borderlands", screen.Chosen!.Name);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void GoingBack_ClosesWithoutChoosing()
    {
        var screen = Screen();
        var closed = false;
        screen.CloseRequested += () => closed = true;

        screen.Back();

        Assert.True(closed);
        Assert.Null(screen.Chosen);
    }

    [Fact]
    public void Favoriting_AsksForTheHighlightedGame()
    {
        var screen = Screen();
        screen.Move(1);
        GameEntry? asked = null;
        screen.FavoriteRequested += g => asked = g;

        screen.ToggleFavorite();

        Assert.Equal("Borderlands", asked!.Name);
    }

    [Fact]
    public void ControllerButtons_DriveTheSameActionsAsTheKeys()
    {
        var screen = Screen();

        screen.Handle(GamepadButton.Right);
        Assert.Equal("Borderlands", screen.SelectedGame!.Name);
        screen.Handle(GamepadButton.NextShelf);
        Assert.Equal("All games", screen.ShelfName);
        screen.Handle(GamepadButton.Down);
        Assert.Equal("Jump back in", screen.ShelfName);
        screen.Handle(GamepadButton.Up);
        Assert.Equal("All games", screen.ShelfName);
        screen.Handle(GamepadButton.Left);
        screen.Handle(GamepadButton.Accept);
        Assert.NotNull(screen.Chosen);
    }

    [Fact]
    public void AnEmptyScreen_DoesNothingAndDoesNotThrow()
    {
        var screen = new BigScreenViewModel([]);

        screen.Move(1);
        screen.MoveShelf(1);
        screen.Accept();
        screen.ToggleFavorite();

        Assert.False(screen.HasGames);
        Assert.Null(screen.Chosen);
        Assert.Null(screen.SelectedGame);
    }

    // ---- the shelves -------------------------------------------------------------------------------

    private LibraryViewModel Library(params GameEntry[] games)
    {
        var vm = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
            ControllerModeAvailable = true, // the skeleton is tested as if it were open; the closed state has its own test below
        };
        vm.SimulateRefreshResult([.. games]);
        vm.SelectViewCommand.Execute("all");
        return vm;
    }

    private static GameEntry Played(string id, string name, double hoursAgo) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = GameSource.Manual,
        TotalPlaySeconds = 600, LastPlayedUtc = DateTime.UtcNow.AddHours(-hoursAgo),
    };

    [Fact]
    public void TheShelves_AreRecentFavoritesThenAll_AndEmptyOnesAreLeftOut()
    {
        var old = Played("o", "Old", 50);
        var fresh = Played("f", "Fresh", 1);
        var unplayed = Game("u", "Unplayed");
        var vm = Library(old, fresh, unplayed);
        vm.ToggleFavoriteCommand.Execute(unplayed);

        var shelves = vm.BuildBigScreenShelves();

        Assert.Equal(["Jump back in", "Favorites", "All games"], shelves.Select(s => s.Name));
        Assert.Equal(["Fresh", "Old"], shelves[0].Games.Select(g => g.Name));
        Assert.Equal(["Unplayed"], shelves[1].Games.Select(g => g.Name));
        Assert.Equal(["Fresh", "Old", "Unplayed"], shelves[2].Games.Select(g => g.Name));

        var plain = Library(Game("a", "Apex"));
        Assert.Equal(["All games"], plain.BuildBigScreenShelves().Select(s => s.Name));
    }

    [Fact]
    public void HiddenGames_NeverAppearOnTheBigScreen()
    {
        var hidden = Game("h", "Hidden");
        var vm = Library(Game("a", "Apex"), hidden);
        vm.ToggleHiddenCommand.Execute(hidden);

        var all = vm.BuildBigScreenShelves().Single();

        Assert.Equal(["Apex"], all.Games.Select(g => g.Name));
    }

    [Fact]
    public void UntilItIsSwitchedOn_ControllerMode_IsComingSoon_FromEveryWayIn() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = Library(Game("a", "Apex"));
        vm.ControllerModeAvailable = false;
        var opened = false;
        vm.BigScreenDialogForTest = _ => opened = true;
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            // The sidebar entry is listed, greyed out and marked, not hidden.
            Assert.False(vm.ShowBigScreenCommand.CanExecute(null));
            Assert.False(window.ControllerModeButton.IsEnabled);
            Assert.True(window.ControllerModeButton.IsVisible);
            Assert.Contains("Soon", Descendants<TextBlock>(window.ControllerModeButton).Select(t => t.Text));
            Assert.Equal("Controller mode - coming soon", window.ControllerModeButton.ToolTip);

            // F11 and the palette run the command without asking CanExecute: they get the explanation, never the screen.
            vm.ShowBigScreenCommand.Execute(null);
            Assert.False(opened);
            Assert.Equal("Controller mode is coming soon.", vm.StatusText);
            Assert.Contains("Controller mode (coming soon)", vm.BuildPaletteItems().Select(i => i.Title));
            vm.StatusText = "";
            vm.BuildPaletteItems().Single(i => i.Title == "Controller mode (coming soon)").Execute();
            Assert.False(opened);
            Assert.Equal("Controller mode is coming soon.", vm.StatusText);

            vm.ControllerModeAvailable = true;
            vm.ShowBigScreenCommand.Execute(null);
            Assert.True(opened);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithNoGames_ItSaysSoInsteadOfOpeningAnEmptyScreen()
    {
        var vm = Library();
        var opened = false;
        vm.BigScreenDialogForTest = _ => opened = true;

        vm.ShowBigScreenCommand.Execute(null);

        Assert.False(opened);
        Assert.Equal("There are no games to show in big-screen mode yet.", vm.StatusText);
    }

    [Fact]
    public void TheChosenGame_IsLaunchedAfterTheScreenCloses_AndNothingLaunchesIfYouBackOut()
    {
        var vm = Library(Game("a", "Apex"));
        vm.BigScreenDialogForTest = screen => screen.Back();
        var status = vm.StatusText;
        vm.ShowBigScreenCommand.Execute(null);
        Assert.Equal(status, vm.StatusText);

        vm.BigScreenDialogForTest = screen => screen.Accept();
        vm.ShowBigScreenCommand.Execute(null);

        // The fake exe does not exist, so a launch attempt shows as the launch failure it produces.
        Assert.StartsWith("Failed to launch Apex", vm.StatusText);
    }

    [Fact]
    public void Favoriting_OnTheBigScreen_TogglesTheLibrarysFavorite()
    {
        var game = Game("a", "Apex");
        var vm = Library(game);
        vm.BigScreenDialogForTest = screen => screen.ToggleFavorite();

        vm.ShowBigScreenCommand.Execute(null);

        Assert.True(game.Favorite);
    }

    // ---- the real window ---------------------------------------------------------------------------

    private static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Press(Window window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
    }

    private static BigScreenWindow Show(BigScreenViewModel vm, GamepadService? pad = null)
    {
        var window = new BigScreenWindow(vm, pad ?? new GamepadService(_ => null))
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = -6000, WindowState = WindowState.Normal,
            Width = 1280, Height = 720,
        };
        window.Show();
        window.UpdateLayout();
        return window;
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
    public void TheWindow_ShowsTheShelfAndTheHighlightedGame_AndTheKeysDriveIt() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var screen = Screen();
        var window = Show(screen);
        try
        {
            await Settle(window);
            var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
            Assert.Contains("Jump back in", texts);
            Assert.Contains("Apex", texts);
            Assert.Contains("Play", texts);
            Assert.Contains("Favorite", texts);

            // A plain Window leaves text black on the dark background unless the light ink is set - it must be readable.
            var heading = Descendants<TextBlock>(window).First(t => t.Text == "Jump back in");
            Assert.NotEqual(Colors.Black, ((SolidColorBrush)heading.Foreground).Color);

            Press(window, Key.Right);
            await Settle(window);
            Assert.Equal("Borderlands", screen.SelectedGame!.Name);
            Assert.Contains("Borderlands", Descendants<TextBlock>(window).Select(t => t.Text));

            Press(window, Key.Down);
            await Settle(window);
            Assert.Equal("All games", screen.ShelfName);
            Assert.Contains("All games", Descendants<TextBlock>(window).Select(t => t.Text));

            Press(window, Key.Enter);
            await Settle(window);
            Assert.Equal("a2", screen.Chosen!.Id); // the first game of the "All games" shelf, which the Down key moved to
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    private static ScrollViewer ScrollerOf(BigScreenWindow window) =>
        Descendants<ScrollViewer>(window.Carousel).First();

    [Fact]
    public void TheHighlightedTile_IsKeptInTheMiddleOfTheScreen_ScrollingInPixelsNotInItems() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var shelf = new BigShelf("Many", Enumerable.Range(1, 12).Select(i => Game("g" + i, "Game " + i)).ToList());
        var screen = new BigScreenViewModel([shelf]);
        var window = Show(screen);
        try
        {
            window.Width = 1600;
            await Settle(window);
            var viewer = ScrollerOf(window);
            Assert.True(viewer.ExtentWidth > 2000, "The carousel scrolls in pixels: twelve tiles are wider than the screen.");
            Assert.Equal(0, viewer.HorizontalOffset); // the first game: nothing to scroll past

            for (var i = 0; i < 6; i++)
                screen.Move(1);
            await Settle(window);
            await Task.Delay(50);
            await Settle(window);

            var tile = (FrameworkElement)window.Carousel.ItemContainerGenerator.ContainerFromIndex(6);
            var tileCentre = tile.TranslatePoint(new Point(tile.ActualWidth / 2, 0), viewer).X;
            Assert.True(viewer.HorizontalOffset > 200, $"Scrolled along the shelf (offset {viewer.HorizontalOffset}).");
            Assert.InRange(tileCentre, viewer.ViewportWidth / 2 - 4, viewer.ViewportWidth / 2 + 4);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheWindow_EscapeLeavesWithoutChoosing() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var screen = Screen();
        var window = Show(screen);
        try
        {
            Press(window, Key.Escape);
            await Settle(window);

            Assert.False(window.IsVisible);
            Assert.Null(screen.Chosen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheWindow_ListensToTheController_WhileOpen() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var screen = Screen();
        var held = new GamepadState(0, 0, 0);
        var now = 0L;
        var pad = new GamepadService(i => i == 0 ? held : null, () => now);
        var window = Show(screen, pad);
        try
        {
            held = new GamepadState(Right, 0, 0);
            pad.Poll();
            Assert.Equal("Borderlands", screen.SelectedGame!.Name);

            held = new GamepadState(0, 0, 0);
            pad.Poll();
            held = new GamepadState(A, 0, 0);
            pad.Poll();
            await Settle(window);

            Assert.Equal("Borderlands", screen.Chosen!.Name);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheMainWindow_BindsF11_ToBigScreenMode() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = Library(Game("a", "Apex"));
        var window = new MainWindow(vm, startRuntimeServices: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.F11);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(vm.ShowBigScreenCommand, binding.Command);
            Assert.Contains("Controller mode", vm.BuildPaletteItems().Select(i => i.Title));
        }
        finally { window.Close(); }
    });
}
