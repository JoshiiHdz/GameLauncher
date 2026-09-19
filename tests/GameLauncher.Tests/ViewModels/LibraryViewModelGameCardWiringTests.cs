using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
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

    private static (MenuItem ChangeCover, MenuItem Reset) OpenContextMenu(Button button)
    {
        var menu = button.ContextMenu ?? throw new InvalidOperationException("Card has no ContextMenu.");
        menu.PlacementTarget = button; // exactly what WPF sets internally on a real right-click
        menu.IsOpen = true;
        PumpDispatcher();

        var items = menu.Items.OfType<MenuItem>().ToList();
        var changeCover = items.Single(i => (string)i.Header == "Change Cover...");
        var reset = items.Single(i => (string)i.Header == "Reset Cover to Automatic");
        return (changeCover, reset);
    }

    // ---- Each menu targets the clicked card -----------------------------------------------------------

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
