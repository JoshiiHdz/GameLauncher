using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The REAL Stats, Pick-a-game, drive Storage and Game details windows - and the main window's new rail buttons, drive menu and
/// low-space colours - on the shared STA dispatcher. What this proves: the XAML loads, bindings resolve to the view-model members (a typo
/// would otherwise fail silently), and the buttons act. What it cannot prove: how any of it LOOKS - that is the gaming-PC checklist.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class DiscoveryWindowTests(WpfStaFixture sta) : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private const long Gb = 1L << 30;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Discovery-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static GameEntry MakeGame(string name, long playSeconds = 0, long? sizeBytes = null, string drive = "C:") => new()
    {
        Id = name, Name = name, ExecutablePath = drive + @"\Games\" + name + @"\game.exe", InstallDir = drive + @"\Games\" + name,
        Source = GameSource.Manual, TotalPlaySeconds = playSeconds, LastPlayedUtc = playSeconds > 0 ? Now.AddHours(-2) : null,
        InstallSizeBytes = sizeBytes,
    };

    private static T Show<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -5000;
        window.Top = -5000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
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

    private static List<string> Texts(DependencyObject root) =>
        Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).ToList();

    private static Wpf.Ui.Controls.Button ButtonNamed(DependencyObject root, string content) =>
        Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content as string == content);

    private static async Task Invoke(Window window, UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element) ?? throw new InvalidOperationException($"No automation peer for {element}.");
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        await Settle(window);
    }

    // ---- The pages (Stats, Pick a game, drive storage, game details) open inside the main window -----------------------------------

    private LibraryViewModel CreateLoadedModel(params GameEntry[] games)
    {
        var vm = CreateModel();
        vm.SimulateRefreshResult(games.ToList());
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        return vm;
    }

    [Fact]
    public void TheStatsPage_ShowsTheFigures_AndShowThemReturnsToTheNotPlayedView() => sta.RunAsync(async () =>
    {
        var vm = CreateLoadedModel(MakeGame("Apex", 7200), MakeGame("Borderlands", 600), MakeGame("Celeste"));
        var window = OpenMain(vm);
        try
        {
            vm.ShowStatsCommand.Execute(null);
            await Settle(window);

            Assert.True(vm.IsPageOpen);
            Assert.True(vm.IsStatsPageOpen);
            Assert.Equal("Stats", window.PageTitleText.Text);
            var texts = Texts(window.PageContent);
            Assert.Contains("3", texts);
            Assert.Contains("2.2 h", texts);
            Assert.Contains("Most played", texts);
            Assert.Contains("Apex", texts);
            Assert.Contains("1 game has no tracked play time.", texts);
            Assert.Contains("Played this week (2)", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("Nothing tracked yet"));

            await Invoke(window, ButtonNamed(window.PageContent, "Show them"));

            Assert.False(vm.IsPageOpen);
            Assert.True(vm.IsNeverPlayedViewSelected);
            Assert.Equal(["Celeste"], vm.Games.Select(g => g.Name));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStatsPage_ForAFreshLibrary_ExplainsThereIsNothingTrackedYet() => sta.RunAsync(async () =>
    {
        var vm = CreateLoadedModel(MakeGame("Apex"));
        var window = OpenMain(vm);
        try
        {
            vm.ShowStatsCommand.Execute(null);
            await Settle(window);
            var texts = Texts(window.PageContent);

            Assert.Contains(texts, t => t.StartsWith("Nothing tracked yet"));
            Assert.Contains("None yet", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("Played this week"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePickPage_ShowsTheSuggestion_PickAnotherChangesIt_AndPlayLaunchesItAfterClosing() => sta.RunAsync(async () =>
    {
        var vm = CreateLoadedModel(MakeGame("Apex"), MakeGame("Borderlands", 1800));
        vm.RandomIndex = _ => 0;
        var window = OpenMain(vm);
        try
        {
            vm.PickGameCommand.Execute(null);
            await Settle(window);
            var pick = Assert.IsType<PickGameViewModel>(vm.CurrentPage);

            Assert.True(vm.IsPickPageOpen);
            Assert.Equal("Pick a game", window.PageTitleText.Text);
            Assert.Contains("Apex", Texts(window.PageContent));
            Assert.Contains("Not played yet", Texts(window.PageContent));
            Assert.Contains("One of 2 games in this view", Texts(window.PageContent));

            await Invoke(window, ButtonNamed(window.PageContent, "Pick another"));
            Assert.Contains("Borderlands", Texts(window.PageContent));
            Assert.DoesNotContain("Apex", Texts(window.PageContent));
            Assert.False(pick.PlayRequested);

            // The page closes first and the game launches after (the fake path does not exist, so the launch reports a failure by name).
            await Invoke(window, ButtonNamed(window.PageContent, "Play"));
            Assert.True(pick.PlayRequested);
            Assert.False(vm.IsPageOpen);
            Assert.StartsWith("Failed to launch Borderlands", vm.StatusText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStoragePage_RanksTheGames_ShowsTheWarning_AndOpensAGamesFolder() => sta.RunAsync(async () =>
    {
        var apex = MakeGame("Apex", sizeBytes: 10 * Gb);
        var celeste = MakeGame("Celeste", sizeBytes: 30 * Gb);
        var pending = MakeGame("Pending");
        var drive = new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500 * Gb, FreeBytes = 8 * Gb };
        var vm = CreateLoadedModel(apex, celeste, pending);
        vm.Drives.Add(drive);
        vm.HasDrives = true;
        var window = OpenMain(vm);
        try
        {
            vm.ShowStorageCommand.Execute("C:");
            await Settle(window);
            var storage = Assert.IsType<StorageViewModel>(vm.CurrentPage);

            var texts = Texts(window.PageContent);
            Assert.Equal("What's using C:", window.PageTitleText.Text);
            Assert.Contains("Almost full - only 8 GB free", texts);
            Assert.Contains("1 game is still being measured...", texts);
            Assert.Contains("Games: 40 GB", texts);
            Assert.True(texts.IndexOf("Celeste") < texts.IndexOf("Apex"), "The biggest game is listed first.");
            Assert.Contains("6% of drive", texts);

            GameEntry? opened = null;
            storage.OpenInstallLocationRequested += game => opened = game;
            var folderButtons = Descendants<Wpf.Ui.Controls.Button>(window.PageContent).Where(b => Equals(b.ToolTip, "Open install location")).ToList();
            Assert.Equal(2, folderButtons.Count);
            await Invoke(window, folderButtons[0]);
            Assert.Same(celeste, opened);

            // A size landing while the page is open re-ranks it.
            pending.InstallSizeBytes = 50 * Gb;
            await Settle(window);
            texts = Texts(window.PageContent);
            Assert.DoesNotContain(texts, t => t.EndsWith("still being measured..."));
            Assert.True(texts.IndexOf("Pending") < texts.IndexOf("Celeste"));
            Assert.Contains("Everything else on the drive: 402 GB", texts);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStoragePage_WithNoGames_SaysSo() => sta.RunAsync(async () =>
    {
        var vm = CreateLoadedModel();
        var window = OpenMain(vm);
        try
        {
            vm.OpenPage(LibraryViewModel.StoragePageKey, "What's using D:", new StorageViewModel("D:", null, []));
            await Settle(window);
            Assert.Contains("No games are installed on this drive.", Texts(window.PageContent));
            Assert.Contains("Drive details are unavailable right now.", Texts(window.PageContent));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheDetailsPage_ShowsTheGame_BindsNotes_AndItsButtonsRaiseTheirRequests() => sta.RunAsync(async () =>
    {
        var game = MakeGame("Apex", playSeconds: 5400, sizeBytes: 12 * Gb);
        game.Collections = ["Backlog", "Co-op"];
        var vm = CreateLoadedModel(game);
        var window = OpenMain(vm);
        try
        {
            vm.ShowGameDetailsCommand.Execute(vm.Games.Single());
            await Settle(window);
            var details = Assert.IsType<GameDetailsViewModel>(vm.CurrentPage);
            vm.CollectionsDialogForTest = _ => null; // the library's own handler for "Edit..." would otherwise open the real dialog
            var page = Descendants<GameDetailsPage>(window.PageContent).Single();

            Assert.Equal("Game details", window.PageTitleText.Text);
            var texts = Texts(window.PageContent);
            Assert.Contains("Apex", texts);
            Assert.Contains("Manual", texts);
            Assert.Contains("12 GB", texts);
            Assert.Contains("Backlog, Co-op", texts);
            Assert.Contains(texts, t => t.StartsWith("1.5 h tracked"));
            Assert.Equal("", page.NotesBox.Text);

            page.NotesBox.Text = "second boss next";
            Assert.Equal("second boss next", details.Notes);
            Assert.True(details.NotesChanged);

            var openedFolder = false;
            var editedCollections = false;
            details.OpenInstallLocationRequested += () => openedFolder = true;
            details.EditCollectionsRequested += () => editedCollections = true;
            await Invoke(window, ButtonNamed(window.PageContent, "Open install location"));
            await Invoke(window, ButtonNamed(window.PageContent, "Edit..."));
            Assert.True(openedFolder);
            Assert.True(editedCollections);
            Assert.True(vm.IsPageOpen);

            game.Collections = ["Finished"];
            await Settle(window);
            Assert.Contains("Finished", Texts(window.PageContent));

            // Notes are saved when the page closes, and Play launches after it has.
            await Invoke(window, ButtonNamed(window.PageContent, "Play"));
            Assert.True(details.PlayRequested);
            Assert.False(vm.IsPageOpen);
            Assert.Equal("second boss next", vm.GetGameNotes("Apex"));
            Assert.StartsWith("Failed to launch Apex", vm.StatusText);
        }
        finally { window.Close(); }
    });

    // ---- The main window ---------------------------------------------------------------------------

    private LibraryViewModel CreateModel()
    {
        var vm = new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
        };
        return vm;
    }

    private static MainWindow OpenMain(LibraryViewModel vm) => Show(new MainWindow(vm, startRuntimeServices: false));

    private static void Populate(LibraryViewModel vm)
    {
        vm.SimulateRefreshResult([MakeGame("Apex", 600), MakeGame("Borderlands", drive: "D:")]);
        vm.SearchText = "x"; // runs the production filter
        vm.SearchText = "";
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500 * Gb, FreeBytes = 300 * Gb });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 500 * Gb, FreeBytes = 20 * Gb });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "E:", Label = "Full", TotalBytes = 500 * Gb, FreeBytes = 4 * Gb });
        vm.HasDrives = true;
    }

    [Fact]
    public void TheWindow_OffersNotPlayedYetPickAGameAndStats_WiredToTheirCommands() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var window = OpenMain(vm);
        try
        {
            var pick = Descendants<RadioButton>(window).Single(b => Equals(b.ToolTip, "Pick a game for me"));
            var stats = Descendants<RadioButton>(window).Single(b => Equals(b.ToolTip, "Stats - play time and library figures"));
            Assert.Same(vm.PickGameCommand, pick.Command);
            Assert.Same(vm.ShowStatsCommand, stats.Command);

            var unplayed = Descendants<RadioButton>(window).Single(b => Equals(b.ToolTip, "Games with no tracked play time yet"));
            Assert.False(unplayed.IsChecked);
            Assert.Same(vm.SelectViewCommand, unplayed.Command);
            Assert.Equal("unplayed", unplayed.CommandParameter);
            unplayed.Command.Execute(unplayed.CommandParameter);
            await Settle(window);

            Assert.True(vm.IsNeverPlayedViewSelected);
            Assert.True(unplayed.IsChecked);
            Assert.Equal(["Borderlands"], vm.Games.Select(g => g.Name));
            Assert.Contains("Not played yet", Texts(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WhenEveryGameIsPlayed_TheNotPlayedViewShowsItsOwnEmptyStateAndIcon() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        vm.SimulateRefreshResult([MakeGame("Apex", 600)]);
        vm.SearchText = "x";
        vm.SearchText = "";
        var window = OpenMain(vm);
        try
        {
            vm.SelectViewCommand.Execute("unplayed");
            await Settle(window);

            Assert.True(window.LibraryEmptyState.IsVisible);
            Assert.Equal("Every game has been played", window.EmptyStateHeading.Text);
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Sparkle24, window.EmptyStateIcon.Symbol);
            Assert.False(window.EmptyDiscoveryActions.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ADriveLowOnSpace_TurnsAmber_AnAlmostFullOneRed_AndRoomyOnesStayQuiet() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var window = OpenMain(vm);
        try
        {
            await Settle(window);
            var bars = Descendants<ProgressBar>(window).Where(b => b.Name == "DriveUsageBar").ToList();
            var warnings = Descendants<TextBlock>(window).Where(t => t.Name == "DriveWarning").ToList();
            Assert.Equal(3, bars.Count);

            static Color ColourOf(ProgressBar bar) => ((SolidColorBrush)bar.Foreground).Color;
            var accent = (Color)window.FindResource("DesignAccentColor");
            Assert.Equal(accent, ColourOf(bars[0]));
            Assert.Equal(Color.FromRgb(0xF5, 0xA5, 0x24), ColourOf(bars[1]));
            Assert.Equal(Color.FromRgb(0xE5, 0x48, 0x4D), ColourOf(bars[2]));

            Assert.Equal(["Running low - 20 GB free", "Almost full - only 4 GB free"], warnings.Where(w => w.IsVisible).Select(w => w.Text));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RightClickingADrive_OffersTheStoragePage_ForThatDrive() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var opened = new List<string>();
        vm.StorageDialogForTest = storage => opened.Add(storage.DriveLetter);
        var window = OpenMain(vm);
        try
        {
            await Settle(window);
            var driveButtons = Descendants<Button>(window).Where(b => b.Name == "DriveButton").ToList();
            var menu = driveButtons[1].ContextMenu ?? throw new InvalidOperationException("The drive has no context menu.");
            menu.PlacementTarget = driveButtons[1]; // exactly what WPF sets on a real right-click
            menu.IsOpen = true;
            await Settle(window);

            var item = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "What's using this drive?");
            Assert.Same(vm.ShowStorageCommand, item.Command);
            Assert.Equal("D:", item.CommandParameter);

            item.Command.Execute(item.CommandParameter);
            Assert.Equal(["D:"], opened);
            menu.IsOpen = false;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheDriveFilterHeading_OffersTheStoragePage_OnlyWhileADriveIsSelected() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var window = OpenMain(vm);
        try
        {
            await Settle(window);
            var button = Descendants<Wpf.Ui.Controls.Button>(window).Single(b => b.Content as string == "What's using this drive?");
            Assert.False(button.IsVisible);

            vm.SelectDriveCommand.Execute("D:");
            await Settle(window);

            Assert.True(button.IsVisible);
            Assert.Same(vm.ShowStorageCommand, button.Command);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheHeroMoreMenu_OffersGameDetails_ForTheFeaturedGame() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var window = OpenMain(vm);
        try
        {
            await Settle(window);
            // The hero's menu is the short one: the game cards' menus carry the same row but also "Collections...".
            var more = Descendants<Button>(window).Single(b => b.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList() is { } headers
                && headers.Contains("Game details...") && !headers.Contains("Collections..."));
            var menu = more.ContextMenu!;
            menu.PlacementTarget = more;
            menu.IsOpen = true;
            await Settle(window);

            var item = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "Game details...");
            Assert.Same(vm.ShowGameDetailsCommand, item.Command);
            Assert.Same(vm.FeaturedGame, item.CommandParameter);
            menu.IsOpen = false;
        }
        finally { window.Close(); }
    });
}
