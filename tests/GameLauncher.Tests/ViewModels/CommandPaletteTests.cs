using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The Ctrl+K command palette: how matches are ranked, what it offers, that the chosen entry runs only after it has closed,
/// the real window's keyboard handling, and the global hotkey.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class CommandPaletteTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());

    public void Dispose()
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => SystemParameters.ClientAreaAnimation;
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static PaletteItem Item(string title, string keywords = "", int bonus = 0, bool empty = false, PaletteKind kind = PaletteKind.Command, Action? run = null) =>
        new(title, "sub", keywords, kind, run ?? (() => { }), bonus, empty);

    // ---- ranking -----------------------------------------------------------------------------------

    [Fact]
    public void EveryWordMustMatch_InAnyOrder()
    {
        var elden = Item("Elden Ring");

        Assert.True(PaletteSearch.Score("ring elden", elden) > 0);
        Assert.True(PaletteSearch.Score("eld rin", elden) > 0);
        Assert.Equal(0, PaletteSearch.Score("elden sword", elden));
        Assert.Equal(0, PaletteSearch.Score("   ", elden));
    }

    [Fact]
    public void BetterMatchesRankHigher_ExactThenPrefixThenWordStartThenContains()
    {
        var exact = PaletteSearch.Score("ring", Item("Ring"));
        var prefix = PaletteSearch.Score("ring", Item("Ring of Fire"));
        var word = PaletteSearch.Score("ring", Item("Elden Ring"));
        var inside = PaletteSearch.Score("ring", Item("Spring Break"));
        var keyword = PaletteSearch.Score("ring", Item("Pick a game", keywords: "ring"));

        Assert.True(exact > prefix && prefix > word && word > inside && inside > keyword && keyword > 0,
            $"{exact} > {prefix} > {word} > {inside} > {keyword}");
    }

    [Fact]
    public void KeywordsMatchButAreNeverRequired_AndNeverOutrankATitleMatch()
    {
        var settings = Item("Open settings", keywords: "preferences options");

        Assert.True(PaletteSearch.Score("preferences", settings) > 0);
        Assert.True(PaletteSearch.Score("open prefer", settings) > 0);
        Assert.Equal(0, PaletteSearch.Score("graphics", settings));
    }

    [Fact]
    public void Search_PutsTheBestFirst_BreaksTiesByShorterTitle_AndCapsTheList()
    {
        var items = new[]
        {
            Item("Portal 2"), Item("Portal"), Item("Portal Knights"), Item("Deportal"),
        };

        var results = PaletteSearch.Search(items, "portal", max: 3);

        Assert.Equal(["Portal", "Portal 2", "Portal Knights"], results.Select(r => r.Title));
    }

    [Fact]
    public void ABonus_LiftsALikelyChoiceAboveAnEquallyGoodMatch()
    {
        var results = PaletteSearch.Search([Item("Doom Eternal"), Item("Doom 3", bonus: 40)], "doom");

        Assert.Equal("Doom 3", results[0].Title);
    }

    [Fact]
    public void WithNothingTyped_OnlyTheOfferedEntriesShow_BestBonusFirst()
    {
        var results = PaletteSearch.Search([Item("A", empty: true, bonus: 5), Item("B"), Item("C", empty: true, bonus: 40)], "");

        Assert.Equal(["C", "A"], results.Select(r => r.Title));
        Assert.Equal(["C", "A"], PaletteSearch.Search([Item("A", empty: true, bonus: 5), Item("B"), Item("C", empty: true, bonus: 40)], null).Select(r => r.Title));
    }

    // ---- the palette's own view model --------------------------------------------------------------

    [Fact]
    public void Typing_RefreshesTheResults_AndHighlightsTheFirst()
    {
        var palette = new CommandPaletteViewModel([Item("Elden Ring"), Item("Hades"), Item("Celeste")]);
        Assert.True(palette.HasNoResults); // nothing typed, nothing offered
        Assert.Equal("Nothing to suggest yet - start typing.", palette.EmptyText);

        palette.Query = "e";
        Assert.Equal(3, palette.Results.Count);
        Assert.Equal(0, palette.SelectedIndex);

        palette.Query = "zzz";
        Assert.True(palette.HasNoResults);
        Assert.Equal(-1, palette.SelectedIndex);
        Assert.Equal("Nothing matches that.", palette.EmptyText);
    }

    [Fact]
    public void TheHighlight_WrapsAtBothEnds_AndDoesNothingWithNoResults()
    {
        var palette = new CommandPaletteViewModel([Item("Aa"), Item("Ab"), Item("Ac")]);
        palette.Query = "a";

        palette.MoveSelection(-1);
        Assert.Equal(2, palette.SelectedIndex);
        palette.MoveSelection(1);
        Assert.Equal(0, palette.SelectedIndex);

        palette.Query = "nothing";
        palette.MoveSelection(1);
        Assert.Equal(-1, palette.SelectedIndex);
    }

    [Fact]
    public void Choosing_RecordsTheEntry_AndAsksTheWindowToClose_ButRunsNothing()
    {
        var ran = false;
        var palette = new CommandPaletteViewModel([Item("Hades", run: () => ran = true)]);
        palette.Query = "had";
        var closed = 0;
        palette.CloseRequested += () => closed++;

        palette.Choose();

        Assert.Equal("Hades", palette.Chosen!.Title);
        Assert.Equal(1, closed);
        Assert.False(ran);
    }

    [Fact]
    public void ChoosingWithNothingHighlighted_DoesNothing()
    {
        var palette = new CommandPaletteViewModel([Item("Hades")]);
        var closed = false;
        palette.CloseRequested += () => closed = true;

        palette.Choose();

        Assert.Null(palette.Chosen);
        Assert.False(closed);
    }

    [Fact]
    public void ResultsAreCapped()
    {
        var palette = new CommandPaletteViewModel(Enumerable.Range(1, 30).Select(i => Item("Game " + i)).ToList());

        palette.Query = "game";

        Assert.Equal(CommandPaletteViewModel.MaxResults, palette.Results.Count);
    }

    // ---- what the library offers -------------------------------------------------------------------

    private LibraryViewModel Library(params GameEntry[] games)
    {
        var vm = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
        };
        vm.SimulateRefreshResult([.. games]);
        vm.SelectViewCommand.Execute("all");
        return vm;
    }

    private static GameEntry Game(string id, string name, GameSource source = GameSource.Manual, long played = 0) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = source,
        TotalPlaySeconds = played, LastPlayedUtc = played > 0 ? DateTime.UtcNow.AddHours(-1) : null,
    };

    [Fact]
    public void ThePaletteOffers_EveryVisibleGame_AndTheAppsCommands()
    {
        var hidden = Game("h", "Hidden Game");
        var vm = Library(Game("a", "Apex"), Game("b", "Borderlands"), hidden);
        vm.ToggleHiddenCommand.Execute(hidden);

        var items = vm.BuildPaletteItems();

        Assert.Equal(["Apex", "Borderlands"], items.Where(i => i.Kind == PaletteKind.Game).Select(i => i.Title).Order());
        foreach (var command in new[] { "Pick a game for me", "Show stats", "Optimize: free up memory and storage", "Rescan library",
                     "Go to favorites", "Export library data...", "Import library data...", "Shut down this PC" })
            Assert.Contains(command, items.Select(i => i.Title));
    }

    [Fact]
    public void TheSettingsEntryIsAlwaysOffered_AndTheDuplicatesEntryOnlyWithDuplicates()
    {
        var vm = Library(Game("steam-1", "Doom", GameSource.Steam));
        Assert.Contains("Open settings", vm.BuildPaletteItems().Select(i => i.Title));
        Assert.DoesNotContain("Go to games installed twice", vm.BuildPaletteItems().Select(i => i.Title));

        var twin = Library(Game("steam-1", "Doom", GameSource.Steam), Game("gog-1", "Doom", GameSource.Gog));
        Assert.Contains("Go to games installed twice", twin.BuildPaletteItems().Select(i => i.Title));
    }

    [Fact]
    public void WithNothingTyped_TheRecentlyPlayedGamesAndTheMainCommandsAreOffered()
    {
        var vm = Library(Game("a", "Apex", played: 600), Game("b", "Borderlands"));

        var offered = PaletteSearch.Search(vm.BuildPaletteItems(), "").Select(i => i.Title).ToList();

        Assert.Contains("Apex", offered);
        Assert.DoesNotContain("Borderlands", offered);
        Assert.Contains("Pick a game for me", offered);
    }

    [Fact]
    public void ASearch_FindsAGameByItsCollection_AndRanksARecentFavouriteFirst()
    {
        var plain = Game("a", "Doom Eternal");
        var loved = Game("b", "Doom 3", played: 600);
        var vm = Library(plain, loved);
        vm.ToggleFavoriteCommand.Execute(loved);
        vm.SetGameCollections(plain, ["Co-op"]);

        var byName = PaletteSearch.Search(vm.BuildPaletteItems(), "doom");
        var byCollection = PaletteSearch.Search(vm.BuildPaletteItems(), "co-op");

        Assert.Equal("Doom 3", byName[0].Title);
        Assert.Equal("Doom Eternal", byCollection[0].Title);
    }

    [Fact]
    public void TheChosenGame_IsLaunchedOnlyAfterThePaletteHasClosed()
    {
        var vm = Library(Game("a", "Apex"));
        var order = new List<string>();
        vm.CommandPaletteDialogForTest = palette =>
        {
            palette.Query = "apex";
            order.Add("palette open");
            return palette.Results[0];
        };

        vm.ShowCommandPaletteCommand.Execute(null);

        // The fake exe does not exist, so a launch attempt shows as the launch failure it produces.
        Assert.StartsWith("Failed to launch Apex", vm.StatusText);
    }

    [Fact]
    public void ADismissedPalette_RunsNothing()
    {
        var vm = Library(Game("a", "Apex"));
        vm.CommandPaletteDialogForTest = _ => null;
        var status = vm.StatusText;

        vm.ShowCommandPaletteCommand.Execute(null);

        Assert.Equal(status, vm.StatusText);
    }

    [Fact]
    public void AnOpenPalette_IsNotStackedBySecondRequest()
    {
        var vm = Library(Game("a", "Apex"));
        var opened = 0;
        vm.CommandPaletteDialogForTest = _ =>
        {
            opened++;
            vm.ShowCommandPaletteCommand.Execute(null); // a second hotkey press while it is open
            return null;
        };

        vm.ShowCommandPaletteCommand.Execute(null);
        Assert.Equal(1, opened);

        vm.ShowCommandPaletteCommand.Execute(null); // once closed, it opens again
        Assert.Equal(2, opened);
    }

    [Fact]
    public void ACommandThatFails_IsReported_NotThrown()
    {
        var vm = Library();
        vm.CommandPaletteDialogForTest = _ => Item("Broken", run: () => throw new InvalidOperationException("boom"));

        vm.ShowCommandPaletteCommand.Execute(null);

        Assert.Equal("Couldn't run \"Broken\": boom", vm.StatusText);
    }

    [Fact]
    public void ChoosingACommand_RunsIt()
    {
        var vm = Library(Game("a", "Apex"));
        var shown = false;
        vm.StatsDialogForTest = _ => { shown = true; return false; };
        vm.CommandPaletteDialogForTest = palette =>
        {
            palette.Query = "show stats";
            return palette.Results[0];
        };

        vm.ShowCommandPaletteCommand.Execute(null);

        Assert.True(shown);
    }

    [Fact]
    public void TheHotkeySetting_IsOnByDefault_AndIsRemembered()
    {
        var vm = Library();
        Assert.True(vm.GlobalHotkeyEnabled);

        vm.GlobalHotkeyEnabled = false;

        Assert.False(new SettingsService(_dataDir).Load().GlobalHotkeyEnabled);
        Assert.False(Library().GlobalHotkeyEnabled);
    }

    // ---- the real window ---------------------------------------------------------------------------

    private static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Press(FrameworkElement element, Key key, RoutedEvent? routed = null)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key) { RoutedEvent = routed ?? Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args);
    }

    private readonly string _paletteDirectory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Palette-" + Guid.NewGuid());

    private MainWindow OpenShell()
    {
        Directory.CreateDirectory(_paletteDirectory);
        return ShellTestSupport.OpenMain(new LibraryViewModel(new SettingsService(_paletteDirectory), new PendingUpdateNotesService(_paletteDirectory)));
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
    public void TheDialog_ListsMatches_ArrowsMove_AndEnterChoosesAndCloses() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = new CommandPaletteViewModel([Item("Hades", kind: PaletteKind.Game), Item("Hades II", kind: PaletteKind.Game), Item("Celeste", kind: PaletteKind.Game)]);
        var window = OpenShell();
        try
        {
            var dialog = new CommandPaletteDialog(vm);
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Command palette", dialog);

            vm.Query = "hades";
            await Settle(window);
            var texts = Descendants<TextBlock>(dialog).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("Hades", texts);
            Assert.Contains("Hades II", texts);
            Assert.DoesNotContain("Celeste", texts);

            Press(dialog, Key.Down);
            Assert.Equal(1, vm.SelectedIndex);
            Press(dialog, Key.Up);
            Press(dialog, Key.Up);
            Assert.Equal(1, vm.SelectedIndex); // wrapped to the last

            Press(dialog, Key.Enter);
            await Settle(window);

            Assert.Equal("Hades II", vm.Chosen!.Title);
            Assert.False(window.IsModalOpen);
            await shown.Operation;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheDialog_EscapeClosesItWithoutChoosing_AClickOnTheDimmedAreaToo_AndClickingARowChoosesIt() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var window = OpenShell();
        try
        {
            var vm = new CommandPaletteViewModel([Item("Hades"), Item("Celeste")]);
            var escaped = await ShellTestSupport.ShowDialogAsync(window, "Command palette", new CommandPaletteDialog(vm));
            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await Settle(window);
            Assert.False(window.IsModalOpen);
            Assert.Null(vm.Chosen);
            await escaped.Operation;

            var dimmed = new CommandPaletteViewModel([Item("Hades"), Item("Celeste")]);
            var clickedAway = await ShellTestSupport.ShowDialogAsync(window, "Command palette", new CommandPaletteDialog(dimmed));
            window.ModalScrim.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Settle(window);
            Assert.False(window.IsModalOpen);
            Assert.Null(dimmed.Chosen);
            await clickedAway.Operation;

            var second = new CommandPaletteViewModel([Item("Hades"), Item("Celeste")]);
            var dialog = new CommandPaletteDialog(second);
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Command palette", dialog);
            second.Query = "cel";
            await Settle(window);
            var row = Descendants<ListBoxItem>(dialog).Single();
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            await Settle(window);

            Assert.Equal("Celeste", second.Chosen!.Title);
            Assert.False(window.IsModalOpen);
            await shown.Operation;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheMainWindow_BindsCtrlK_ToThePalette_AndOffersSettingsAsAPage() => sta.RunAsync(async () =>
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
            var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.K);
            Assert.Equal(ModifierKeys.Control, binding.Modifiers);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(vm.ShowCommandPaletteCommand, binding.Command);
            vm.BuildPaletteItems().Single(i => i.Title == "Open settings").Execute();
            Assert.True(vm.IsSettingsPageOpen);
        }
        finally { window.Close(); }
    });

    // ---- the global hotkey -------------------------------------------------------------------------

    [Fact]
    public void ModifierBits_MapToWindows_AndAlwaysSuppressKeyRepeat()
    {
        Assert.Equal(0x4000u | 0x2 | 0x1, GlobalHotkeyService.ModifierBits(ModifierKeys.Control | ModifierKeys.Alt));
        Assert.Equal(0x4000u | 0x4 | 0x8, GlobalHotkeyService.ModifierBits(ModifierKeys.Shift | ModifierKeys.Windows));
        Assert.Equal(0x4000u, GlobalHotkeyService.ModifierBits(ModifierKeys.None));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void ARegisteredHotkey_RaisesPressed_RefusesAClash_AndIsGivenBackOnDispose() => sta.RunAsync(async () =>
    {
        // An obscure combination, so a real program on this machine can't already own it.
        const Key key = Key.F23;
        const ModifierKeys mods = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
        var first = new Window { Width = 10, Height = 10, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var second = new Window { Width = 10, Height = 10, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var hotkey = GlobalHotkeyService.TryRegister(first, key, mods);
        try
        {
            Assert.NotNull(hotkey);
            var presses = 0;
            hotkey!.Pressed += () => presses++;

            SendMessage(new WindowInteropHelper(first).Handle, 0x0312, hotkey.Id, IntPtr.Zero);
            Assert.Equal(1, presses);
            SendMessage(new WindowInteropHelper(first).Handle, 0x0312, hotkey.Id + 1, IntPtr.Zero); // someone else's hotkey
            Assert.Equal(1, presses);

            Assert.Null(GlobalHotkeyService.TryRegister(second, key, mods));

            hotkey.Dispose();
            hotkey.Dispose(); // harmless twice
            var again = GlobalHotkeyService.TryRegister(second, key, mods);
            Assert.NotNull(again);
            again!.Dispose();
        }
        finally
        {
            hotkey?.Dispose();
            first.Close();
            second.Close();
        }
        await Task.CompletedTask;
    });
}
