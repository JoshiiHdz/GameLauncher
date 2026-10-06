using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Behaviors;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Verifies the ACTUAL MainWindow markup - not a hand-written approximation of it. Merges the real
/// Resources/GameCardTemplate.xaml (the same file MainWindow.xaml itself merges) into a throwaway,
/// off-screen host window, binds it to a real LibraryViewModel, and drives the generated Buttons/
/// ContextMenus exactly as WPF would at runtime (PlacementTarget assignment, a real opened Popup, bound
/// Command/CommandParameter resolution).
///
/// This exists because a wrong RelativeSource/PlacementTarget path in the template does NOT reliably
/// throw XamlParseException at startup - a binding failure like that typically just resolves to null and
/// shows up only as a trace warning (or, worse, as every card's context menu silently acting on the wrong
/// game, or not at all). Building and inspecting the real template end to end is the only way to catch
/// that class of bug; reading the markup is not.
///
/// Runs on a dedicated STA dispatcher thread (WpfStaFixture) - WPF elements, and Application itself,
/// cannot be constructed or driven from a plain ThreadPool thread the way the rest of this test suite
/// runs on.
/// </summary>
[Collection(WpfStaCollection.Name)]
public class LibraryViewModelGameCardWiringTests : IDisposable
{
    private readonly WpfStaFixture _sta;
    private readonly string _dataDir;
    private readonly string _assetStoreDir;

    public LibraryViewModelGameCardWiringTests(WpfStaFixture sta)
    {
        _sta = sta;
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _assetStoreDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
        if (Directory.Exists(_assetStoreDir))
            Directory.Delete(_assetStoreDir, recursive: true);
    }

    private LibraryViewModel MakeViewModel() => new(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
    {
        AssetStoreDirOverrideForTest = _assetStoreDir,
        AutomaticCoverArtLookupForTest = (_, _) => null,
    };

    private static GameEntry MakeGame(string id, string name = "Test Game") => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = @"C:\Games\DoesNotExist\game.exe", // deliberately unlaunchable - see the Launch tests
        InstallDir = @"C:\Games\DoesNotExist",
        Source = GameSource.Manual,
    };

    /// <summary>Builds a throwaway, off-screen Window hosting three ItemsControls (mirroring MainWindow's
    /// Favorites/main/Hidden sections) against the real, compiled GameCardTemplate resource - loaded by
    /// the exact same pack URI mechanism MainWindow.xaml's own ResourceDictionary Source= reference
    /// uses, so this is never at risk of drifting from what the app actually ships.</summary>
    private static (Window Window, ItemsControl Favorites, ItemsControl Main, ItemsControl Hidden) BuildHostWindow(LibraryViewModel vm)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml"),
        };
        var template = (DataTemplate)dict["GameCardTemplate"];

        var favorites = new ItemsControl { ItemTemplate = template };
        var main = new ItemsControl { ItemTemplate = template };
        var hidden = new ItemsControl { ItemTemplate = template };

        favorites.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.FavoriteGames)));
        main.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.Games)));
        hidden.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(LibraryViewModel.HiddenGames)));

        var stack = new StackPanel();
        stack.Children.Add(favorites);
        stack.Children.Add(main);
        stack.Children.Add(hidden);

        var window = new Window
        {
            DataContext = vm,
            Content = stack,
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000,
            Top = -5000,
            ShowActivated = false,
        };
        window.Resources.MergedDictionaries.Add(dict);
        window.Show();
        window.UpdateLayout();

        return (window, favorites, main, hidden);
    }

    private static Button FindCardButton(ItemsControl itemsControl, GameEntry game)
    {
        var button = FindDescendant(itemsControl, game)
            ?? throw new InvalidOperationException($"No card Button found for game '{game.Id}' under {itemsControl}.");
        return button;
    }

    private static Button? FindDescendant(DependencyObject root, GameEntry game)
    {
        if (root is Button { DataContext: GameEntry entry } button && ReferenceEquals(entry, game))
            return button;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendant(VisualTreeHelper.GetChild(root, i), game);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Pumps the dispatcher's message queue without actually blocking on Task/thread-join
    /// primitives - lets a just-opened Popup finish creating its own HwndSource and evaluating its
    /// bindings before the very next line of the test reads them.</summary>
    private static void PumpDispatcher()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>Finds the card's hover-revealed "..." overflow icon - the one element in the card tree
    /// with ContextMenuButtonBehavior.OpensContextMenuOnClick set, rather than matching by name/tooltip
    /// text that could drift independently of the actual feature.</summary>
    private static Border FindOverflowButton(Button cardButton) =>
        FindOverflowDescendant(cardButton)
            ?? throw new InvalidOperationException("No overflow menu Border found under the card button.");

    private static Border? FindOverflowDescendant(DependencyObject root)
    {
        if (root is Border border && ContextMenuButtonBehavior.GetOpensContextMenuOnClick(border))
            return border;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindOverflowDescendant(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>The menu's rows in order, with "-" for a separator (spec 07 section 7.5: ten rows, three separators, no name header).</summary>
    private static List<string> MenuShape(ContextMenu menu) =>
        menu.Items.Cast<object>().Select(i => i is MenuItem m ? (m.Header as string ?? ((string?)m.Tag == "MenuHeader" ? "[name + close X]" : "")) : "-").ToList();

    private static (MenuItem ChangeCover, MenuItem Reset) OpenContextMenu(Button button)
    {
        var menu = button.ContextMenu ?? throw new InvalidOperationException("Card has no ContextMenu.");
        menu.PlacementTarget = button; // exactly what WPF sets internally on a real right-click
        menu.IsOpen = true;
        PumpDispatcher();

        var items = menu.Items.OfType<MenuItem>().ToList();
        var changeCover = items.Single(i => i.Header as string == "Change Cover Manually");
        var reset = items.Single(i => i.Header as string == "Reset cover to automatic");
        return (changeCover, reset);
    }

    // ---- Each menu targets the clicked card -----------------------------------------------------------

    /// <summary>The card menu is the spec's, under a name header with a close X: Play, details, favorite/hide/collections, the cover rows, identify, open location, uninstall - split by
    /// three separators.</summary>
    [Fact]
    public void ContextMenu_HasTheNameHeaderThenTheSpecRowsInOrder()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a", "Game A");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var menu = button.ContextMenu!;
                menu.PlacementTarget = button;
                menu.IsOpen = true;
                PumpDispatcher();

                Assert.Equal(
                    new[] { "[name + close X]", "-", "Play", "Game details...", "-", "Favorite", "Hide", "Collections...", "-", "Change Cover Manually", "Reset cover to automatic", "Identify game...", "-", "Open install location", "Uninstall..." },
                    MenuShape(menu));
                var uninstall = menu.Items.OfType<MenuItem>().Last();
                Assert.Equal("Destructive", uninstall.Tag);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ContextMenu_InMainGrid_TargetsTheClickedGame_NotAnyOtherCard()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var gameA = MakeGame("game-a", "Game A");
            var gameB = MakeGame("game-b", "Game B");
            vm.SimulateRefreshResult([gameA, gameB]); // populates _allGames, which CommitArtworkChange resolves against
            vm.Games.Add(gameA);
            vm.Games.Add(gameB);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var buttonA = FindCardButton(main, gameA);
                var (changeCoverA, resetA) = OpenContextMenu(buttonA);
                Assert.Same(gameA, changeCoverA.CommandParameter);
                Assert.Same(gameA, resetA.CommandParameter);
                Assert.Same(vm.ChangeCoverCommand, changeCoverA.Command);
                Assert.Same(vm.ResetCoverCommand, resetA.Command);

                var buttonB = FindCardButton(main, gameB);
                var (changeCoverB, resetB) = OpenContextMenu(buttonB);
                Assert.Same(gameB, changeCoverB.CommandParameter);
                Assert.Same(gameB, resetB.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ContextMenu_InFavoritesSection_TargetsTheClickedGame()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var favoriteGame = MakeGame("fav-1", "Favorite Game");
            vm.SimulateRefreshResult([favoriteGame]);
            vm.FavoriteGames.Add(favoriteGame);

            var (window, favorites, _, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(favorites, favoriteGame);
                var (changeCover, reset) = OpenContextMenu(button);
                Assert.Same(favoriteGame, changeCover.CommandParameter);
                Assert.Same(favoriteGame, reset.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ContextMenu_InHiddenSection_TargetsTheClickedGame()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var hiddenGame = MakeGame("hidden-1", "Hidden Game");
            vm.SimulateRefreshResult([hiddenGame]);
            vm.HiddenGames.Add(hiddenGame);

            var (window, _, _, hidden) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(hidden, hiddenGame);
                var (changeCover, reset) = OpenContextMenu(button);
                Assert.Same(hiddenGame, changeCover.CommandParameter);
                Assert.Same(hiddenGame, reset.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    // ---- Right-click/menu actions never execute Launch -------------------------------------------------

    [Fact]
    public void OpeningContextMenu_NeverRaisesTheCardsClickEvent()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var clicked = false;
                button.Click += (_, _) => clicked = true;

                OpenContextMenu(button);

                Assert.False(clicked, "Opening the context menu must never fire the card's own Click (which runs LaunchCommand).");
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void InvokingChangeCoverOrResetFromTheMenu_NeverRaisesTheCardsClickEvent()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            vm.ChangeCoverFilePickerForTest = _ => null; // cancel immediately - only routing matters here
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var clicked = false;
                button.Click += (_, _) => clicked = true;

                var (changeCover, reset) = OpenContextMenu(button);
                await ((IAsyncRelayCommand)changeCover.Command!).ExecuteAsync(changeCover.CommandParameter);
                await ((IAsyncRelayCommand)reset.Command!).ExecuteAsync(reset.CommandParameter);

                Assert.False(clicked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- Preview, Cancel, Apply, and Reset behave correctly, driven through the real bound commands ----

    [Fact]
    public void MenuDrivenChangeCover_PreviewCancelled_NothingChanges()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var imagePath = MakeTempPngFile();
            vm.ChangeCoverFilePickerForTest = _ => imagePath;
            vm.ChangeCoverPreviewDialogForTest = (_, _) => false; // Cancel
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var (changeCover, _) = OpenContextMenu(button);

                await ((IAsyncRelayCommand)changeCover.Command!).ExecuteAsync(changeCover.CommandParameter);

                Assert.Null(vm.GetOverride(game.Id));
            }
            finally
            {
                File.Delete(imagePath);
                window.Close();
            }
        });
    }

    [Fact]
    public void MenuDrivenChangeCover_PreviewApplied_CommitsAndTheCardShowsTheNewCover()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var imagePath = MakeTempPngFile();
            vm.ChangeCoverFilePickerForTest = _ => imagePath;
            vm.ChangeCoverPreviewDialogForTest = (_, _) => true; // Apply
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var (changeCover, _) = OpenContextMenu(button);

                await ((IAsyncRelayCommand)changeCover.Command!).ExecuteAsync(changeCover.CommandParameter);

                Assert.NotNull(vm.GetOverride(game.Id)?.Artwork);
                Assert.True(game.IsCoverArt);
            }
            finally
            {
                File.Delete(imagePath);
                window.Close();
            }
        });
    }

    [Fact]
    public void MenuDrivenReset_ClearsAPreviousSelection()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);
            vm.SetArtworkForTest(game.Id, new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true }, revision: 1);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var (_, reset) = OpenContextMenu(button);

                await ((IAsyncRelayCommand)reset.Command!).ExecuteAsync(reset.CommandParameter);

                Assert.Null(vm.GetOverride(game.Id)!.Artwork);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- Bindings still target the correct card after a refresh ---------------------------------------

    [Fact]
    public void AfterRefreshReplacesEveryGameEntry_ContextMenuTargetsTheNewLiveInstance_NotTheDiscardedOne()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var before = MakeGame("game-a", "Before Refresh");
            vm.SimulateRefreshResult([before]);
            vm.Games.Add(before);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var buttonBefore = FindCardButton(main, before);
                var (changeCoverBefore, _) = OpenContextMenu(buttonBefore);
                Assert.Same(before, changeCoverBefore.CommandParameter);
                buttonBefore.ContextMenu!.IsOpen = false;

                // A rescan replaces every GameEntry wholesale (see ReplaceAllGames) - simulated here by
                // repopulating the same public collection the real ApplyFilter() would repopulate, with a
                // brand-new instance sharing the same id.
                var after = MakeGame("game-a", "After Refresh");
                vm.SimulateRefreshResult([after]);
                vm.Games.Clear();
                vm.Games.Add(after);
                window.UpdateLayout();
                PumpDispatcher();

                var buttonAfter = FindCardButton(main, after);
                var (changeCoverAfter, resetAfter) = OpenContextMenu(buttonAfter);

                Assert.Same(after, changeCoverAfter.CommandParameter);
                Assert.Same(after, resetAfter.CommandParameter);
                Assert.NotSame(before, changeCoverAfter.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    // ---- Overflow "..." menu (hover icon, same actions as right-click) --------------------------------

    [Fact]
    public void HoverJump_IsSmallAndFinite_ReturnsToRest_AndDoesNotMoveTheHoverRoot()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("hover-jump");
            vm.SimulateRefreshResult([game]);
            vm.InstallSizeEstimatorForTest = (_, _) => 0;
            vm.SelectDriveCommand.Execute("C:");
            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var card = FindCardButton(main, game);
                var presenter = (ContentPresenter)VisualTreeHelper.GetChild(card, 0);
                var root = Assert.IsType<Border>(presenter.Content);
                var motion = Assert.IsType<StackPanel>(root.Child);
                Assert.Equal("CardRoot", root.Name);
                Assert.Equal("CardMotion", motion.Name);
                Assert.IsType<TranslateTransform>(motion.RenderTransform);
                var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml") };
                var template = (DataTemplate)dict["GameCardTemplate"];
                var trigger = Assert.Single(template.Triggers.OfType<MultiDataTrigger>());
                Assert.Contains(trigger.Conditions.Cast<Condition>(), c =>
                    c.Binding is Binding { ElementName: "CardRoot", Path.Path: "IsMouseOver" });
                Assert.Contains(trigger.Conditions.Cast<Condition>(), c =>
                    c.Binding is Binding b && Equals(b.Source, SystemParameters.ClientAreaAnimation));
                Storyboard Start(TriggerActionCollection actions)
                {
                    var storyboard = Assert.IsType<BeginStoryboard>(Assert.Single(actions.Cast<TriggerAction>())).Storyboard.Clone();
                    foreach (var animation in storyboard.Children)
                    {
                        Assert.Equal("CardMotion", Storyboard.GetTargetName(animation));
                        Assert.Equal(new RepeatBehavior(1), animation.RepeatBehavior);
                        animation.ClearValue(Storyboard.TargetNameProperty);
                        Storyboard.SetTarget(animation, motion);
                    }
                    storyboard.Begin(window, true);
                    return storyboard;
                }
                var rootPosition = root.TranslatePoint(new Point(), window);
                var enter = Start(trigger.EnterActions);
                var hover = Behaviors.ThemeTiming.Hover.TimeSpan + TimeSpan.FromMilliseconds(40);
                enter.SeekAlignedToLastTick(window, hover, TimeSeekOrigin.BeginTime);
                Assert.Equal(-2, ((TranslateTransform)motion.RenderTransform).Y, precision: 2);
                enter.SeekAlignedToLastTick(window, hover + TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
                Assert.Equal(-2, ((TranslateTransform)motion.RenderTransform).Y, precision: 2); // lifts 2 px and stays
                Assert.Equal(rootPosition, root.TranslatePoint(new Point(), window));
                var exit = Start(trigger.ExitActions);
                exit.SeekAlignedToLastTick(window, hover, TimeSeekOrigin.BeginTime);
                Assert.Equal(0, ((TranslateTransform)motion.RenderTransform).Y, precision: 2);
                Assert.Equal(rootPosition, root.TranslatePoint(new Point(), window));
                exit.Remove(window);
                enter.Remove(window);
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void FavoriteStar_WithHoverOverlayVisible_IsHitTestable_TogglesWithoutLaunching()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("favorite-hover");
            vm.SimulateRefreshResult([game]);
            vm.InstallSizeEstimatorForTest = (_, _) => 0;
            vm.SelectDriveCommand.Execute("C:"); // Keep the single test game in the grid, not the hero.
            var (window, _, main, _) = BuildHostWindow(vm);
            var launchCalls = 0;
            static IEnumerable<FrameworkElement> Elements(DependencyObject root)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is FrameworkElement element) yield return element;
                    foreach (var descendant in Elements(child)) yield return descendant;
                }
            }
            try
            {
                foreach (var expected in new[] { true, false })
                {
                    window.UpdateLayout();
                    var card = FindCardButton(main, game);
                    card.Command = new RelayCommand(() => launchCalls++);
                    var star = Assert.IsType<Button>(Elements(card).Single(e => e.Name == "FavoriteButton"));
                    foreach (var element in Elements(card).Where(e => e.Name is "FavoriteButton" or "HoverOverlay" or "HoverPlayButton"))
                        element.Visibility = Visibility.Visible; // Same visible layers as the hover trigger.
                    window.UpdateLayout();
                    var point = star.TranslatePoint(new Point(star.ActualWidth / 2, star.ActualHeight / 2), window);
                    var hit = Assert.IsAssignableFrom<DependencyObject>(window.InputHitTest(point));
                    Assert.True(ReferenceEquals(hit, star) || star.IsAncestorOf(hit),
                        "The hover overlay must not intercept the favorite star's click.");
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(star);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    PumpDispatcher();
                    Assert.Equal(expected, game.Favorite);
                    Assert.Equal(0, launchCalls);
                }
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        });
    }

    [Fact]
    public void OverflowMenuButton_IsCollapsedByDefault_AndWiredToTheSameHoverTriggerAsThePlayOverlay()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var overflow = FindOverflowButton(button);
                Assert.Equal(Visibility.Collapsed, overflow.Visibility);

                var dict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GameLauncher;component/Resources/GameCardTemplate.xaml"),
                };
                var template = (DataTemplate)dict["GameCardTemplate"];
                var hoverTrigger = template.Triggers.OfType<Trigger>()
                    .Single(t => t.Property == UIElement.IsMouseOverProperty && t.SourceName == "CardRoot");
                var targetNames = hoverTrigger.Setters.OfType<Setter>().Select(s => s.TargetName).ToList();

                Assert.Contains("HoverOverlay", targetNames);
                Assert.Contains("CardMenuButton", targetNames);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void OverflowMenuButton_OpensTheSameActions_TargetingTheClickedGame_AndNeverLaunchesTheGame()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var overflow = FindOverflowButton(button);
                var clicked = false;
                button.Click += (_, _) => clicked = true;

                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                overflow.RaiseEvent(args);
                PumpDispatcher();

                Assert.True(args.Handled, "The overflow click must be marked handled so it never reaches the card's own Button.");
                Assert.False(clicked, "Opening the overflow menu must never fire the card's own Click (which runs LaunchCommand).");

                var menu = overflow.ContextMenu ?? throw new InvalidOperationException("Overflow Border has no ContextMenu.");
                Assert.True(menu.IsOpen);
                Assert.Same(overflow, menu.PlacementTarget);

                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(11, items.Count); // the 10 actions, plus the name header with its close X
                var play = items.Single(i => i.Header as string == "Play");
                var openLocation = items.Single(i => i.Header as string == "Open install location");
                var favorite = items.Single(i => i.Header as string == "Favorite"); // game starts un-favorited
                var hide = items.Single(i => i.Header as string == "Hide"); // game starts un-hidden
                var changeCover = items.Single(i => i.Header as string == "Change Cover Manually");
                var reset = items.Single(i => i.Header as string == "Reset cover to automatic");
                var identify = items.Single(i => i.Header as string == "Identify game...");
                var collections = items.Single(i => i.Header as string == "Collections...");
                var details = items.Single(i => i.Header as string == "Game details...");
                var uninstall = items.Single(i => i.Header as string == "Uninstall...");

                Assert.Same(game, uninstall.CommandParameter);
                Assert.Same(vm.UninstallGameCommand, uninstall.Command);
                Assert.Same(game, details.CommandParameter);
                Assert.Same(vm.ShowGameDetailsCommand, details.Command);
                Assert.Same(game, collections.CommandParameter);
                Assert.Same(vm.EditCollectionsCommand, collections.Command);
                Assert.Same(game, play.CommandParameter);
                Assert.Same(game, openLocation.CommandParameter);
                Assert.Same(game, favorite.CommandParameter);
                Assert.Same(game, hide.CommandParameter);
                Assert.Same(game, changeCover.CommandParameter);
                Assert.Same(game, reset.CommandParameter);
                Assert.Same(game, identify.CommandParameter);
                Assert.Same(vm.LaunchCommand, play.Command);
                Assert.Same(vm.OpenInstallLocationCommand, openLocation.Command);
                Assert.Same(vm.ToggleFavoriteCommand, favorite.Command);
                Assert.Same(vm.ToggleHiddenCommand, hide.Command);
                Assert.Same(vm.ChangeCoverCommand, changeCover.Command);
                Assert.Same(vm.ResetCoverCommand, reset.Command);
                Assert.Same(vm.IdentifyGameCommand, identify.Command);
                Assert.DoesNotContain(items, i => i.Header as string == "Choose cover from catalog..."); // the cover section is just these three
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    // ---- Play / Open Install Location (both menus) -----------------------------------------------------

    [Fact]
    public void RightClickMenu_HasPlayAndOpenInstallLocation_TargetingTheClickedGame()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var menu = button.ContextMenu!;
                menu.PlacementTarget = button;
                menu.IsOpen = true;
                PumpDispatcher();

                var items = menu.Items.OfType<MenuItem>().ToList();
                var play = items.Single(i => i.Header as string == "Play");
                var openLocation = items.Single(i => i.Header as string == "Open install location");

                Assert.Same(vm.LaunchCommand, play.Command);
                Assert.Same(vm.OpenInstallLocationCommand, openLocation.Command);
                Assert.Same(game, play.CommandParameter);
                Assert.Same(game, openLocation.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void FavoriteAndHiddenMenuText_TracksTheGamesOwnState_NotJustItsInitialValue()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var game = MakeGame("game-a");
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, favorites, main, hidden) = BuildHostWindow(vm);
            try
            {
                // ToggleFavorite/ToggleHidden both end by calling ApplyFilter, which Clear()s and
                // rebuilds Games/FavoriteGames/HiddenGames - a Clear raises Reset, which WPF's
                // ItemsControl treats as "regenerate every container from scratch", not just the one
                // that moved. Re-finding the card after each toggle (exactly like
                // AfterRefreshReplacesEveryGameEntry_... already does for a rescan) is what makes this
                // safe; holding onto the ORIGINAL button/menu across a toggle is not.
                (MenuItem Favorite, MenuItem Hide) OpenMenuAndFindActions(ItemsControl host)
                {
                    var button = FindCardButton(host, game);
                    var menu = button.ContextMenu!;
                    menu.PlacementTarget = button;
                    menu.IsOpen = true;
                    PumpDispatcher();
                    var items = menu.Items.OfType<MenuItem>().ToList();
                    var favorite = items.Single(i => i.Header as string is "Favorite" or "Unfavorite");
                    var hideItem = items.Single(i => i.Header as string is "Hide" or "Unhide");
                    return (favorite, hideItem);
                }

                var (favoriteBefore, hideBefore) = OpenMenuAndFindActions(main);
                Assert.Equal("Favorite", favoriteBefore.Header);
                Assert.Equal("Hide", hideBefore.Header);

                vm.ToggleFavoriteCommand.Execute(game); // moves game: main -> favorites section
                window.UpdateLayout();
                PumpDispatcher();

                var (favoriteAfterFav, hideAfterFav) = OpenMenuAndFindActions(favorites);
                Assert.Equal("Unfavorite", favoriteAfterFav.Header);
                Assert.Equal("Hide", hideAfterFav.Header);

                vm.ToggleHiddenCommand.Execute(game); // moves game: favorites -> hidden section
                window.UpdateLayout();
                PumpDispatcher();

                var (favoriteAfterHide, hideAfterHide) = OpenMenuAndFindActions(hidden);
                Assert.Equal("Unfavorite", favoriteAfterHide.Header);
                Assert.Equal("Unhide", hideAfterHide.Header);
                Assert.Same(vm.ToggleFavoriteCommand, favoriteAfterHide.Command);
                Assert.Same(vm.ToggleHiddenCommand, hideAfterHide.Command);
                Assert.Same(game, favoriteAfterHide.CommandParameter);
                Assert.Same(game, hideAfterHide.CommandParameter);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void InvokingPlayFromTheMenu_LaunchesTheClickedGame_NotJustWhicheverGameOpenedFirst()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var gameA = MakeGame("game-a", "Game A");
            var gameB = MakeGame("game-b", "Game B");
            vm.SimulateRefreshResult([gameA, gameB]);
            vm.Games.Add(gameA);
            vm.Games.Add(gameB);

            GameEntry? launched = null;
            vm.GameLaunched += (g, _) => launched = g;

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var buttonB = FindCardButton(main, gameB);
                var menuB = buttonB.ContextMenu!;
                menuB.PlacementTarget = buttonB;
                menuB.IsOpen = true;
                PumpDispatcher();

                var play = menuB.Items.OfType<MenuItem>().Single(i => i.Header as string == "Play");
                play.Command!.Execute(play.CommandParameter);

                // Both unlaunchable (see MakeGame's remarks), so Launch logs/sets StatusText and returns
                // rather than raising GameLaunched - this proves ROUTING (the right game reached the
                // command), not that a real process started.
                Assert.Null(launched);
                Assert.Contains("Game B", vm.StatusText);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void InvokingOpenInstallLocation_OpensTheClickedGamesOwnFolder()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var realDir = Directory.CreateTempSubdirectory("GameLauncherTests-InstallDir-").FullName;
            string? openedPath = null;
            vm.OpenFolderInExplorerForTest = p => openedPath = p;

            var game = new GameEntry
            {
                Id = "game-a",
                Name = "Game A",
                ExecutablePath = Path.Combine(realDir, "game.exe"),
                InstallDir = realDir,
                Source = GameSource.Manual,
            };
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var menu = button.ContextMenu!;
                menu.PlacementTarget = button;
                menu.IsOpen = true;
                PumpDispatcher();

                var openLocation = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Open install location");
                openLocation.Command!.Execute(openLocation.CommandParameter);

                Assert.Equal(realDir, openedPath);
            }
            finally
            {
                window.Close();
                Directory.Delete(realDir, recursive: true);
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void InvokingOpenInstallLocation_WhenTheFolderIsGone_ExplainsItInsteadOfThrowing()
    {
        _sta.RunAsync(async () =>
        {
            var vm = MakeViewModel();
            var opened = false;
            vm.OpenFolderInExplorerForTest = _ => opened = true;
            var game = MakeGame("game-a", "Game A"); // InstallDir deliberately doesn't exist
            vm.SimulateRefreshResult([game]);
            vm.Games.Add(game);

            var (window, _, main, _) = BuildHostWindow(vm);
            try
            {
                var button = FindCardButton(main, game);
                var menu = button.ContextMenu!;
                menu.PlacementTarget = button;
                menu.IsOpen = true;
                PumpDispatcher();

                var openLocation = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Open install location");
                openLocation.Command!.Execute(openLocation.CommandParameter);

                Assert.False(opened, "A missing folder must never reach the explorer-opening seam.");
                Assert.Contains("Game A", vm.StatusText);
                Assert.Contains("Can't find", vm.StatusText);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    private static string MakeTempPngFile()
    {
        var pixels = new byte[8 * 8];
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Gray8, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-CardWiring-{Guid.NewGuid()}.png");
        using (var stream = new FileStream(path, FileMode.CreateNew))
            encoder.Save(stream);
        return path;
    }
}
