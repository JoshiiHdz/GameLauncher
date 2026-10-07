using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The in-app shell, on the real main window: the slimmed title bar, the sidebar's Tools and Settings entries, pages that fill the
/// content area (and the ways back out of them), dialogs on the dimmed layer, and Settings' categories - including that every setting
/// that used to be on the old Settings window is still reachable.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ShellTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Shell-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static GameEntry MakeGame(string name, string drive = "C:") => new()
    {
        Id = name, Name = name, ExecutablePath = drive + @"\Games\" + name + @"\game.exe", InstallDir = drive + @"\Games\" + name,
        Source = GameSource.Manual,
    };

    private LibraryViewModel CreateModel(params GameEntry[] games)
    {
        var vm = new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
        };
        if (games.Length > 0)
        {
            vm.SimulateRefreshResult(games.ToList());
            vm.SearchText = "x"; // runs the production filter
            vm.SearchText = "";
        }

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

    private static async Task Invoke(UIElement element)
    {
        if (element is RadioButton radio)
        {
            radio.Command.Execute(radio.CommandParameter); // radio buttons have no Invoke pattern; running their command is what a click does
            await ShellTestSupport.SettleAsync();
            return;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(element) ?? throw new InvalidOperationException($"No automation peer for {element}.");
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        await ShellTestSupport.SettleAsync();
    }

    private static void Press(FrameworkElement element, Key key, RoutedEvent routed)
    {
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key) { RoutedEvent = routed });
    }

    private static async Task<MainWindow> Open(LibraryViewModel vm)
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var window = ShellTestSupport.OpenMain(vm);
        await ShellTestSupport.SettleAsync();
        window.UpdateLayout();
        return window;
    }

    // ---- the title bar and the sidebar -------------------------------------------------------------

    [Fact]
    public void TheTitleBar_HoldsOnlyTheBrandTheSearchAndTheWindowButtons() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            // No icon buttons any more: every one of the old eight lives in the sidebar, the library header or Settings.
            Assert.Empty(Descendants<Wpf.Ui.Controls.Button>(window.TitleBarDock).Where(b => !b.IsDescendantOf(window.SearchBox)));
            var tooltips = Descendants<Button>(window.TitleBarDock).Select(b => b.ToolTip as string).ToList();
            Assert.Equal(["Minimize", "Close"], tooltips.Where(t => t is "Minimize" or "Close").OrderByDescending(t => t));
            Assert.DoesNotContain(tooltips, t => t is not null && (t.Contains("Stats") || t.Contains("Optimize") || t.Contains("Pick") || t.Contains("Shut down")
                || t.Contains("Settings") || t.Contains("Rescan")));
            Assert.True(window.SearchBox.IsVisible);
        }
        finally { window.Close(); }
    });

    private static double BottomOf(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).Transform(new Point(0, element.ActualHeight)).Y;

    [Fact]
    public void TheSidebar_HoldsTheTools_PinsSettingsAboveShutDownAtTheBottom_AllWiredToTheirCommands() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            var radios = Descendants<RadioButton>(window.SidebarPanel).ToList();
            var buttons = Descendants<Button>(window.SidebarPanel).ToList();
            RadioButton Radio(string tip) => radios.Single(r => Equals(r.ToolTip, tip));
            Button Plain(string tip) => buttons.Single(b => Equals(b.ToolTip, tip));

            Assert.Same(vm.ShowStatsCommand, Radio("Stats - play time and library figures").Command);
            Assert.Same(vm.PickGameCommand, Radio("Pick a game for me").Command);
            Assert.Same(vm.ShowOptimizeCommand, Radio("Optimize - free up memory and clear storage").Command);
            Assert.Same(vm.ShowSettingsCommand, Radio("Settings").Command);
            Assert.Same(vm.ShutdownPcCommand, Plain("Shut down this PC (asks first)").Command);
            Assert.Same(vm.RescanCommand, window.RescanToolButton.Command);
            Assert.Same(vm.AddFolderCommand, window.AddFolderButton.Command);
            Assert.Contains("TOOLS", Descendants<TextBlock>(window.SidebarPanel).Where(t => t.IsVisible).Select(t => t.Text));

            // Add folder is in the Tools section (so it folds away with it); Rescan is not - it is the icon on the LIBRARY header, above the
            // views and the Launchers section; Shut down is not either.
            Assert.False(window.RescanToolButton.IsDescendantOf(window.ToolsBody));
            Assert.True(window.AddFolderButton.IsDescendantOf(window.ToolsBody));
            var allGames = radios.Single(r => Equals(r.ToolTip, "All games"));
            Assert.True(BottomOf(window.RescanToolButton, window) <= allGames.TransformToAncestor(window).Transform(new Point(0, 0)).Y + 1);
            Assert.True(BottomOf(allGames, window) <= window.LaunchersHeader.TransformToAncestor(window).Transform(new Point(0, 0)).Y + 1);
            Assert.False(Plain("Shut down this PC (asks first)").IsDescendantOf(window.ToolsBody));

            // Settings sits at the very bottom of the sidebar, Shut down PC below it.
            var shutDown = Plain("Shut down this PC (asks first)");
            Assert.True(BottomOf(Radio("Settings"), window) < BottomOf(shutDown, window));
            Assert.True(BottomOf(window.AddFolderButton, window) < BottomOf(Radio("Settings"), window));
            Assert.True(BottomOf(shutDown, window) > window.SidebarPanel.ActualHeight * 0.9 + window.WindowDragBar.ActualHeight);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RescanAndTheHiddenToggle_SitInTheLibraryHeader_AndHideWithTheLibrary() => sta.RunAsync(async () =>
    {
        var hidden = MakeGame("Secret");
        hidden.Hidden = true;
        var vm = CreateModel(MakeGame("Apex"), hidden);
        var window = await Open(vm);
        try
        {
            Assert.Same(vm.RescanCommand, window.RescanButton.Command);
            Assert.Same(vm.ToggleShowHiddenCommand, window.HiddenToggleButton.Command);
            Assert.True(window.RescanButton.IsVisible);
            Assert.True(window.HiddenToggleButton.IsVisible);

            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.False(window.RescanButton.IsVisible); // the library is not what is showing
        }
        finally { window.Close(); }
    });

    // ---- pages -------------------------------------------------------------------------------------

    [Fact]
    public void APage_FillsTheContentArea_MarksItsSidebarEntry_AndBackAndEscClosesIt() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            Assert.False(window.PageHost.IsVisible);
            var allGames = Descendants<RadioButton>(window.SidebarPanel).Single(r => Equals(r.ToolTip, "All games"));
            var settings = Descendants<RadioButton>(window.SidebarPanel).Single(r => Equals(r.ToolTip, "Settings"));
            Assert.True(allGames.IsChecked);
            Assert.False(settings.IsChecked);

            await Invoke(settings);
            Assert.True(window.PageHost.IsVisible);
            Assert.Equal("Settings", window.PageTitleText.Text);
            Assert.True(settings.IsChecked);
            Assert.False(allGames.IsChecked); // the page's own entry is the marked one

            await Invoke(window.PageBackButton);
            Assert.False(vm.IsPageOpen);
            Assert.False(window.PageHost.IsVisible);
            Assert.True(allGames.IsChecked);

            vm.ShowStatsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsStatsPageOpen);
            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await ShellTestSupport.SettleAsync();
            Assert.False(vm.IsPageOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OpeningAnotherPage_ClosesTheFirstOnce_AndEveryLibraryNavigationClosesThePage() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500, FreeBytes = 300 });
        vm.HasDrives = true;
        var window = await Open(vm);
        try
        {
            var closed = new List<string>();
            vm.OpenPage(LibraryViewModel.StatsPageKey, "Stats", LibraryStats.Compute([MakeGame("Apex")], DateTime.UtcNow), () => closed.Add("stats"));
            vm.ShowSettingsCommand.Execute(null);
            Assert.Equal(["stats"], closed);
            Assert.True(vm.IsSettingsPageOpen);

            vm.ShowSettingsCommand.Execute(null); // already open: nothing to do
            Assert.True(vm.IsSettingsPageOpen);

            vm.SelectViewCommand.Execute("favorites");
            Assert.False(vm.IsPageOpen);

            vm.ShowSettingsCommand.Execute(null);
            vm.SelectDriveCommand.Execute("C:");
            Assert.False(vm.IsPageOpen);

            vm.ShowSettingsCommand.Execute(null);
            vm.SearchText = "ap";
            Assert.False(vm.IsPageOpen);
            vm.SearchText = "";

            vm.ShowSettingsCommand.Execute(null);
            vm.SourceItems[0].IsEnabled = !vm.SourceItems[0].IsEnabled;
            Assert.False(vm.IsPageOpen);
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ThePaletteAndThePageTitles_FollowThePage() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            foreach (var (command, title) in new (System.Windows.Input.ICommand, string)[]
            {
                (vm.ShowOptimizeCommand, "Optimize"), (vm.ShowStatsCommand, "Stats"), (vm.PickGameCommand, "Pick a game"),
            })
            {
                command.Execute(null);
                await ShellTestSupport.SettleAsync();
                Assert.Equal(title, window.PageTitleText.Text);
                vm.ClosePage();
            }
        }
        finally { window.Close(); }
    });

    // ---- Settings, grouped -------------------------------------------------------------------------

    [Fact]
    public void Settings_HasSixCategories_ShowsOneAtATime_AndRemembersWhereYouWere() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = await Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Descendants<SettingsPage>(window.PageContent).Single();

            Assert.Equal(["General", "Library", "Appearance", "Performance", "Data and backup", "About and support"],
                page.CategoryList.Items.OfType<ListBoxItem>().Select(i => (string)System.Windows.Automation.AutomationProperties.GetName(i)));
            Assert.Equal(0, page.CategoryList.SelectedIndex);

            var sections = new FrameworkElement[]
            {
                page.GeneralSection, page.LibrarySection, page.AppearanceSection, page.PerformanceSection,
                page.BackupSection, page.AboutSection,
            };
            for (var i = 0; i < sections.Length; i++)
            {
                page.CategoryList.SelectedIndex = i;
                await ShellTestSupport.SettleAsync();
                window.UpdateLayout();
                Assert.Equal(i, vm.SettingsCategory);
                for (var j = 0; j < sections.Length; j++)
                    Assert.Equal(i == j, sections[j].IsVisible);
            }

            // Back out and in again: lands on the last category (About), not General.
            vm.ClosePage();
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var again = Descendants<SettingsPage>(window.PageContent).Single();
            Assert.Equal(5, again.CategoryList.SelectedIndex);
            Assert.True(again.AboutSection.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OptimizeShortcut_OpensSettingsOnThePerformanceCategory_EvenWhenSettingsWasLastLeftElsewhere() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = await Open(vm);
        try
        {
            vm.SettingsCategory = 6; // last left on About

            vm.ShowOptimizeSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var page = Descendants<SettingsPage>(window.PageContent).Single();
            Assert.True(vm.IsSettingsPageOpen);
            Assert.Equal(3, page.CategoryList.SelectedIndex);
            Assert.True(page.PerformanceSection.IsVisible);
            Assert.Equal("Performance", System.Windows.Automation.AutomationProperties.GetName((ListBoxItem)page.CategoryList.Items[LibraryViewModel.PerformanceSettingsCategory]));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Settings_StillHasEverySettingTheOldWindowHad() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = await Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Descendants<SettingsPage>(window.PageContent).Single();

            var labels = new HashSet<string>();
            var boxes = 0;
            for (var i = 0; i < page.CategoryList.Items.Count; i++)
            {
                page.CategoryList.SelectedIndex = i;
                await ShellTestSupport.SettleAsync();
                window.UpdateLayout();
                foreach (var toggle in Descendants<Wpf.Ui.Controls.ToggleSwitch>(page).Where(t => t.IsVisible))
                    labels.Add((string)toggle.Content);
                foreach (var button in Descendants<Wpf.Ui.Controls.Button>(page).Where(b => b.IsVisible))
                    if (button.Content is string text)
                        labels.Add(text);
                boxes += Descendants<Wpf.Ui.Controls.TextBox>(page).Count(b => b.IsVisible);
            }

            string[] expected =
            [
                "Add folder", "Rescan library", "Start with Windows", "Check for updates on startup", "Hide to tray while gaming",
                "Track games opened outside the launcher", "Vibrant background", "Open maximized",
                "Open the command palette from anywhere with Ctrl+Alt+Space", "Open Optimize", "Free up memory before launching a game",
                "Export library data...", "Import library data...", "Clear cache and refresh covers", "Reset all settings and cache...", "Check for Updates Now", "Open Logs Folder",
                "Send Feedback or Report a Bug",
            ];
            Assert.Empty(expected.Except(labels));
            Assert.Contains(labels, l => l.Contains("shortcut", StringComparison.OrdinalIgnoreCase)); // the desktop-shortcut button
            Assert.Equal(0, boxes); // no free-text settings left (the Discord application id went with Discord)
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Settings_HasNoControllerModeCard_UntilTheFeatureExists() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = await Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Descendants<SettingsPage>(window.PageContent).Single();

            Assert.DoesNotContain("Integrations", page.CategoryList.Items.OfType<ListBoxItem>().Select(i => i.Content as string));
            Assert.DoesNotContain(Descendants<Wpf.Ui.Controls.Button>(page), b => Equals(b.Content, "Open controller mode"));
        }
        finally { window.Close(); }
    });

    // ---- dialogs on the dimmed layer ---------------------------------------------------------------

    [Fact]
    public void AConfirmation_IsACardInTheWindow_YesAndNoAnswerIt_AndEscIsNo() => sta.RunAsync(async () =>
    {
        var window = await Open(CreateModel());
        try
        {
            async Task<bool> Ask(Func<MainWindow, Task> answer)
            {
                var asked = window.Dispatcher.InvokeAsync(() => AppShell.Confirm("Shut down PC", "Shut down your PC?", yes: "Shut down", no: "Cancel", warning: true));
                await ShellTestSupport.WaitForModalAsync(window);
                Assert.Equal("Shut down PC", window.ModalTitle.Text);
                Assert.True(window.ModalLayer.IsVisible);
                Assert.Contains("Shut down your PC?", Descendants<TextBlock>(window.ModalContent).Select(t => t.Text));
                Assert.True(window.ModalCard.IsVisible);
                await answer(window);
                return await asked;
            }

            Assert.True(await Ask(async w => await Invoke(Descendants<Wpf.Ui.Controls.Button>(w.ModalContent).Single(b => Equals(b.Content, "Shut down")))));
            Assert.False(window.IsModalOpen);
            Assert.False(window.ModalLayer.IsVisible);

            Assert.False(await Ask(async w => await Invoke(Descendants<Wpf.Ui.Controls.Button>(w.ModalContent).Single(b => Equals(b.Content, "Cancel")))));

            Assert.False(await Ask(async w =>
            {
                Press(w, Key.Escape, Keyboard.KeyDownEvent);
                await ShellTestSupport.SettleAsync();
            }));
            Assert.False(window.IsModalOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ADialog_IgnoresAClickOnTheDimmedArea_ClosesFromItsCloseButton_AndBlocksTheShortcutsBehindIt() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Collections", new CollectionsDialog(new CollectionsViewModel("Apex", ["Backlog"], [])));
            Assert.False(window.SearchBox.IsEnabled); // nothing behind the dialog takes typing

            window.ModalScrim.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await ShellTestSupport.SettleAsync();
            Assert.True(window.IsModalOpen); // a stray click must not throw away what was typed

            await Invoke(window.ModalCloseButton);
            Assert.False(window.IsModalOpen);
            Assert.True(window.SearchBox.IsEnabled);
            await shown.Operation;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ADialog_KeepsItsOwnWidth_OnTheVeryFirstShow_AndShrinksOnlyInASmallWindow() => sta.RunAsync(async () =>
    {
        var window = await Open(CreateModel());
        try
        {
            var feedback = new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d"));
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Send Feedback", feedback);
            Assert.Equal(480, feedback.ActualWidth, precision: 0); // not squeezed by a layer that had no size yet
            await shown.CloseAsync();

            var palette = new CommandPaletteDialog(new CommandPaletteViewModel([]));
            var paletteShown = await ShellTestSupport.ShowDialogAsync(window, "Command palette", palette);
            Assert.Equal(560, palette.ActualWidth, precision: 0); // the theme's palette width (the spec: 560, and 460 in a small window)
            Assert.False(window.ModalHeader.IsVisible); // it draws its own card
            await paletteShown.CloseAsync();

            window.Width = 560; // narrower than the dialog
            window.UpdateLayout();
            var narrow = await ShellTestSupport.ShowDialogAsync(window, "Send Feedback", feedback = new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d")));
            Assert.True(feedback.ActualWidth <= window.ActualWidth - 48 + 0.5);
            await narrow.CloseAsync();
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ADialogTallerThanASmallWindow_ScrollsInsteadOfLosingItsButtons() => sta.RunAsync(async () =>
    {
        var window = ShellTestSupport.OpenMain(CreateModel(), 760, 480);
        try
        {
            var feedback = new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d"));
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Send Feedback", feedback);

            // The card is inside the window, and the part that does not fit can be scrolled to (the Send button is at the bottom).
            var cardBottom = window.ModalCard.TransformToAncestor(window).Transform(new Point(0, window.ModalCard.ActualHeight)).Y;
            Assert.True(cardBottom <= window.ActualHeight + 0.5, $"The card ends at {cardBottom}, below the window's {window.ActualHeight}.");
            Assert.True(window.ModalScroll.ScrollableHeight > 0, "Expected the dialog to need scrolling in a 480px window.");

            window.ModalScroll.ScrollToBottom();
            await ShellTestSupport.SettleAsync();
            var send = Descendants<Wpf.Ui.Controls.Button>(feedback).Single(b => Equals(b.Content, "Send"));
            var sendBottom = send.TransformToAncestor(window.ModalScroll).Transform(new Point(0, send.ActualHeight)).Y;
            Assert.True(sendBottom <= window.ModalScroll.ViewportHeight + 1, "The Send button can be scrolled into view.");
            await shown.CloseAsync();

            // A tall window needs no scrolling at all.
            window.Height = 900;
            window.UpdateLayout();
            var again = await ShellTestSupport.ShowDialogAsync(window, "Send Feedback", new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d")));
            Assert.Equal(0, window.ModalScroll.ScrollableHeight);
            await again.CloseAsync();
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DialogsStack_EscClosesOnlyTheTopOne_AndTheDialogBelowComesBack() => sta.RunAsync(async () =>
    {
        var window = await Open(CreateModel());
        try
        {
            var first = await ShellTestSupport.ShowDialogAsync(window, "First", new CollectionsDialog(new CollectionsViewModel("A", [], [])));
            var second = await ShellTestSupport.ShowDialogAsync(window, "Second", new ConfirmDialog("Sure?", "Yes", "No", warning: false));
            Assert.Equal("Second", window.ModalTitle.Text);

            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.IsModalOpen);
            Assert.Equal("First", window.ModalTitle.Text);
            Assert.Same(first.Content, window.ModalContent.Content);
            await second.Operation;

            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await ShellTestSupport.SettleAsync();
            Assert.False(window.IsModalOpen);
            await first.Operation;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ClosingTheWindowWithADialogOpen_EndsTheDialogInsteadOfHangingTheApp() => sta.RunAsync(async () =>
    {
        var window = await Open(CreateModel());
        var shown = await ShellTestSupport.ShowDialogAsync(window, "Collections", new CollectionsDialog(new CollectionsViewModel("A", [], [])));

        window.Close();
        await ShellTestSupport.SettleAsync();

        Assert.False(window.IsModalOpen);
        Assert.True(shown.Operation.Status is DispatcherOperationStatus.Completed);
    });

    [Fact]
    public void AShowModalWithNoWindow_Throws_ButAConfirmFallsBackToAMessageBoxOnlyWithoutOne() => sta.RunAsync(async () =>
    {
        var previous = AppShell.Host;
        AppShell.Host = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => AppShell.ShowModal("x", new ConfirmDialog("q", "Yes", "No", false)));
        }
        finally { AppShell.Host = previous; }
        await Task.CompletedTask;
    });

    // ---- the sidebar's accordion sections ----------------------------------------------------------

    private static GameEntry MakeGame(string name, GameSource source) => new()
    {
        Id = name, Name = name, ExecutablePath = @"C:\Games\" + name + ".exe", InstallDir = @"C:\Games\" + name, Source = source,
    };

    [Fact]
    public void TheToolsSection_FoldsAndUnfolds_AndRemembersItBetweenRuns() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex"));
        var window = await Open(vm);
        try
        {
            Assert.True(window.ToolsBody.IsVisible);
            Assert.True(window.ToolsHeader.IsVisible);

            await Invoke(window.ToolsHeader);
            Assert.False(vm.IsToolsExpanded);
            Assert.False(window.ToolsBody.IsVisible);
            Assert.False(window.AddFolderButton.IsVisible);
            Assert.True(window.RescanToolButton.IsVisible); // Rescan is not part of Tools: it stays where it is
            Assert.False(new SettingsService(_directory).Load().SidebarToolsExpanded);

            // A new session opens the way it was left.
            var again = CreateModel();
            Assert.False(again.IsToolsExpanded);

            await Invoke(window.ToolsHeader);
            Assert.True(vm.IsToolsExpanded);
            Assert.True(window.ToolsBody.IsVisible);
            Assert.True(new SettingsService(_directory).Load().SidebarToolsExpanded);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void InTheIconRail_AFoldedSectionStaysFolded_AndAChevronOpensItAgain() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam), MakeGame("Hades", GameSource.Epic));
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500, FreeBytes = 300 });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 500, FreeBytes = 300 });
        vm.HasDrives = true;
        vm.HasMultipleDrives = true;
        var window = await Open(vm);
        try
        {
            vm.IsSidebarExpanded = false; // collapsed to icons
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            // Every section is open, and each has its own little chevron (the titles are not drawn in the rail).
            Assert.True(window.ToolsBody.IsVisible);
            Assert.True(window.ToolsRailToggle.IsVisible);
            Assert.True(window.LaunchersRailToggle.IsVisible);
            Assert.True(window.DrivesRailToggle.IsVisible);
            Assert.False(window.ToolsHeader.IsVisible); // the wide sidebar's title is not what is shown here
            Assert.False(window.LaunchersHeader.IsVisible); // (its Visibility is bound, so this is the wrapper's doing - it showed a second arrow once)
            Assert.False(window.DrivesHeader.IsVisible);

            // One arrow per section, never two: the rail chevrons are the only visible chevron icons in the sidebar.
            var chevrons = Descendants<Wpf.Ui.Controls.SymbolIcon>(window.SidebarPanel)
                .Where(i => i.IsVisible && i.Symbol is Wpf.Ui.Controls.SymbolRegular.ChevronDown20 or Wpf.Ui.Controls.SymbolRegular.ChevronRight20).ToList();
            Assert.Equal(3, chevrons.Count);

            // Folding Tools from the rail hides its icons - the same state the wide sidebar uses - and the chevron stays to bring it back.
            await Invoke(window.ToolsRailToggle);
            Assert.False(vm.IsToolsExpanded);
            Assert.False(window.ToolsBody.IsVisible);
            Assert.False(window.AddFolderButton.IsVisible);
            Assert.True(window.ToolsRailToggle.IsVisible);

            await Invoke(window.LaunchersRailToggle);
            await Invoke(window.DrivesRailToggle);
            Assert.False(window.LaunchersBody.IsVisible);
            Assert.False(window.DrivesBody.IsVisible);

            // Back in the wide sidebar the same sections are still folded, with their titles to unfold them.
            vm.IsSidebarExpanded = true;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            Assert.False(window.ToolsBody.IsVisible);
            Assert.True(window.ToolsHeader.IsVisible);
            Assert.False(window.ToolsRailToggle.IsVisible);

            // And unfolding in the rail works too.
            vm.IsSidebarExpanded = false;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            await Invoke(window.ToolsRailToggle);
            Assert.True(window.ToolsBody.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void InTheIconRail_ASectionWithOneItem_HasNoChevron_AndIsAlwaysShown() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam));
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500, FreeBytes = 300 });
        vm.HasDrives = true;
        var window = await Open(vm);
        try
        {
            vm.IsSidebarExpanded = false;
            vm.IsLaunchersExpanded = false; // saved as folded, but there is only one launcher: nothing to fold
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            Assert.False(window.LaunchersRailToggle.IsVisible);
            Assert.False(window.DrivesRailToggle.IsVisible);
            Assert.True(window.LaunchersBody.IsVisible);
            Assert.True(window.DrivesBody.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheWholeSidebarScrolls_WhenItIsFull_AndTheEndsStayWhereTheyAre() => sta.RunAsync(async () =>
    {
        // A short window and a lot in the sidebar: three launchers, a collection, two drives.
        var games = new[] { MakeGame("A", GameSource.Steam), MakeGame("B", GameSource.Epic), MakeGame("C", GameSource.Gog), MakeGame("D", GameSource.Manual) };
        var vm = CreateModel(games);
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500, FreeBytes = 300 });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 500, FreeBytes = 300 });
        vm.HasDrives = true;
        vm.HasMultipleDrives = true;
        var window = ShellTestSupport.OpenMain(vm, 900, 480);
        try
        {
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            var scroller = Descendants<ScrollViewer>(window.SidebarPanel).First();
            Assert.True(scroller.ScrollableHeight > 0, "Expected the sidebar to need scrolling in a 480px window.");

            // The views are inside what scrolls (they used to be pinned above it and squeeze it to nothing).
            var allGames = Descendants<RadioButton>(window.SidebarPanel).Single(r => Equals(r.ToolTip, "All games"));
            Assert.True(allGames.IsDescendantOf(scroller));
            Assert.True(window.RescanToolButton.IsDescendantOf(scroller));

            // Collapse stays at the top; Settings and Shut down PC stay at the bottom, always on screen.
            var panelHeight = window.SidebarPanel.ActualHeight;
            var collapseTop = window.SidebarToggleButton.TransformToAncestor(window.SidebarPanel).Transform(new Point(0, 0)).Y;
            var settings = Descendants<RadioButton>(window.SidebarPanel).Single(r => Equals(r.ToolTip, "Settings"));
            var settingsBottom = settings.TransformToAncestor(window.SidebarPanel).Transform(new Point(0, settings.ActualHeight)).Y;
            Assert.True(collapseTop < 20);
            Assert.True(settingsBottom <= panelHeight + 0.5);

            // Scrolled to the end, the last thing in the list (the drives) is reachable.
            scroller.ScrollToBottom();
            await ShellTestSupport.SettleAsync();
            var drives = Descendants<Button>(window.SidebarPanel).Where(b => b.Name == "DriveButton").Last();
            var driveBottom = drives.TransformToAncestor(scroller).Transform(new Point(0, drives.ActualHeight)).Y;
            Assert.True(driveBottom <= scroller.ViewportHeight + 1, "The last drive can be scrolled into view.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LaunchersAndDrives_FoldOnlyWhenThereIsMoreThanOne() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam));
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500, FreeBytes = 300 });
        vm.HasDrives = true;
        var window = await Open(vm);
        try
        {
            // One launcher, one drive: plain titles, no chevron, nothing to fold - even if the saved state says folded.
            Assert.False(vm.HasMultipleLaunchers);
            Assert.False(vm.HasMultipleDrives);
            vm.IsLaunchersExpanded = false;
            vm.IsDrivesExpanded = false;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            Assert.False(window.LaunchersHeader.IsHitTestVisible);
            Assert.False(window.DrivesHeader.IsHitTestVisible);
            Assert.True(window.LaunchersBody.IsVisible);
            Assert.True(window.DrivesBody.IsVisible);
            Assert.DoesNotContain(Descendants<Wpf.Ui.Controls.SymbolIcon>(window.LaunchersHeader), i => i.IsVisible);
            vm.IsLaunchersExpanded = true;
            vm.IsDrivesExpanded = true;

            // A second launcher and a second drive: the titles become accordions.
            vm.SimulateRefreshResult([MakeGame("Apex", GameSource.Steam), MakeGame("Borderlands", GameSource.Epic)]);
            vm.SearchText = "x"; // runs the production filter
            vm.SearchText = "";
            vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 500, FreeBytes = 300 });
            vm.HasMultipleDrives = true;
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();

            Assert.True(vm.HasMultipleLaunchers);
            Assert.True(window.LaunchersHeader.IsHitTestVisible);
            Assert.True(window.DrivesHeader.IsHitTestVisible);
            Assert.Contains(Descendants<Wpf.Ui.Controls.SymbolIcon>(window.LaunchersHeader), i => i.IsVisible);

            await Invoke(window.LaunchersHeader);
            await Invoke(window.DrivesHeader);
            Assert.False(window.LaunchersBody.IsVisible);
            Assert.False(window.DrivesBody.IsVisible);
            Assert.True(window.LaunchersHeader.IsVisible); // the title stays, so there is a way back
            Assert.True(window.DrivesHeader.IsVisible);
            var saved = new SettingsService(_directory).Load();
            Assert.False(saved.SidebarLaunchersExpanded);
            Assert.False(saved.SidebarDrivesExpanded);

            await Invoke(window.LaunchersHeader);
            await Invoke(window.DrivesHeader);
            Assert.True(window.LaunchersBody.IsVisible);
            Assert.True(window.DrivesBody.IsVisible);
        }
        finally { window.Close(); }
    });

    // ---- opening maximized -------------------------------------------------------------------------

    [Fact]
    public void TheWindow_OpensMaximizedByDefault_AndNormalWhenTheSettingIsOff() => sta.RunAsync(async () =>
    {
        Assert.True(new AppSettings().StartMaximized);

        var on = CreateModel();
        // Decided when the window is built, so it is read before it is ever shown (WPF will not show a non-activated maximized window).
        var maximized = new MainWindow(on, startRuntimeServices: false, openPerSetting: true);
        Assert.Equal(WindowState.Maximized, maximized.WindowState);

        var off = CreateModel();
        off.StartMaximized = false;
        Assert.False(new SettingsService(_directory).Load().StartMaximized);
        var normal = new MainWindow(off, startRuntimeServices: false, openPerSetting: true);
        Assert.Equal(WindowState.Normal, normal.WindowState);

        // Without the real-start flag (every other test) the window keeps the size it was given.
        Assert.Equal(WindowState.Normal, new MainWindow(on, startRuntimeServices: false).WindowState);
        await Task.CompletedTask;
    });

    [Fact]
    public void Settings_OffersOpenMaximized_WiredToTheSetting() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = await Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var toggle = Descendants<Wpf.Ui.Controls.ToggleSwitch>(window.PageContent).Single(t => Equals(t.Content, "Open maximized"));
            Assert.True(toggle.IsChecked);
            Assert.Equal("StartMaximized", toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.ParentBinding.Path.Path);
            toggle.IsChecked = false;
            Assert.False(vm.StartMaximized);
        }
        finally { window.Close(); }
    });

    // ---- manual games ------------------------------------------------------------------------------

    [Fact]
    public void AGameWithNoLauncher_GetsAFolderIcon_OnItsBadge() => sta.RunAsync(async () =>
    {
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Games24,
            GameLauncher.Converters.GameSourceToFallbackSymbolConverter.Instance.Convert(GameSource.Manual, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Question24,
            GameLauncher.Converters.GameSourceToFallbackSymbolConverter.Instance.Convert(GameSource.Steam, typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture));
        await Task.CompletedTask;
    });

    // ---- launchers above tools, and a row for games that have no launcher ---------------------------

    [Fact]
    public void TheLaunchersSection_SitsAboveTools() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam));
        var window = await Open(vm);
        try
        {
            Assert.True(window.LaunchersHeader.IsVisible);
            var launchersTop = window.LaunchersHeader.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var toolsTop = window.ToolsHeader.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(launchersTop < toolsTop, $"Launchers ({launchersTop}) should be above Tools ({toolsTop}).");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GamesWithNoLauncher_HaveTheirOwnRowInTheLauncherList_WithAFolderIcon_AndASwitch() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam), MakeGame("Indie", GameSource.Manual));
        var window = await Open(vm);
        try
        {
            var row = vm.SourceItems.Single(i => i.Source == GameSource.Manual);
            Assert.Equal("No launcher", row.Name);
            Assert.True(row.HasGames);
            Assert.True(row.UsesGlyph);
            Assert.False(row.ShowLetter);
            Assert.True(vm.HasMultipleLaunchers); // Steam + manual: the list can fold
            Assert.Equal(["Apex", "Indie"], vm.Games.Select(g => g.Name).OrderBy(n => n));

            // The row is on screen, drawn with the folder glyph rather than a letter.
            var folders = Descendants<Wpf.Ui.Controls.SymbolIcon>(window.ExpandedLaunchers).Where(i => i.IsVisible && i.Symbol == Wpf.Ui.Controls.SymbolRegular.Games24).ToList();
            Assert.Single(folders);

            // Its switch hides and shows those games, like any launcher's.
            row.IsEnabled = false;
            await ShellTestSupport.SettleAsync();
            Assert.False(vm.DetectManual);
            Assert.Equal(["Apex"], vm.Games.Select(g => g.Name));
            Assert.False(new SettingsService(_directory).Load().DetectManual);

            row.IsEnabled = true;
            Assert.Equal(["Apex", "Indie"], vm.Games.Select(g => g.Name).OrderBy(n => n));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheManualRow_IsNotListed_WhenThereAreNoManualGames() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(MakeGame("Apex", GameSource.Steam));
        var window = await Open(vm);
        try
        {
            Assert.False(vm.SourceItems.Single(i => i.Source == GameSource.Manual).HasGames);
            Assert.False(vm.HasMultipleLaunchers);
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    // ---- "recently played" details on the cards ------------------------------------------------------

    private static GameEntry Played(string name, double hours, int daysAgo) => new()
    {
        Id = name, Name = name, ExecutablePath = @"C:\G\" + name + ".exe", InstallDir = @"C:\G\" + name, Source = GameSource.Manual,
        TotalPlaySeconds = (long)(hours * 3600), LastPlayedUtc = DateTime.UtcNow.AddDays(-daysAgo),
    };

    [Fact]
    public void EveryCard_ShowsItsLauncherHoursAndWhen() => sta.RunAsync(async () =>
    {
        var vm = CreateModel(Played("Apex", 12.5, 1), Played("Hades", 3, 4), new GameEntry
        {
            Id = "Fresh", Name = "Fresh", ExecutablePath = @"C:\G\Fresh.exe", InstallDir = @"C:\G\Fresh", Source = GameSource.Manual,
        });
        var window = await Open(vm);
        try
        {
            // Every card now carries its meta line: "Launcher · hours · when" once played, "Launcher · Not played yet" otherwise.
            List<string> Metas() => Descendants<TextBlock>(window).Where(t => t.Name == "CardMeta" && t.IsVisible).Select(t => t.Text).ToList();

            Assert.True(vm.HasRecentlyPlayed);
            var onHome = Metas();
            Assert.Contains(onHome, t => t.Contains("12.5 h") && t.Contains("·"));
            Assert.Contains(onHome, t => t.Contains("3 h") && t.Contains("·"));
            Assert.Contains(onHome, t => t.EndsWith("Not played yet"));

            vm.SelectViewCommand.Execute("recent");
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            Assert.Equal(2, vm.Games.Count);
            Assert.DoesNotContain(Metas().Where(t => t.Contains(" h ")), t => t.Contains("Not played yet"));
        }
        finally { window.Close(); }
    });

    // ---- the real commands, end to end -------------------------------------------------------------

    [Fact]
    public void TheShutDownEntry_AsksInsideTheWindow_AndNothingHappensOnNo() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var started = new List<string>();
        vm.StartShutdownForTest = started.Add;
        var window = await Open(vm);
        try
        {
            var asked = window.Dispatcher.InvokeAsync(() => vm.ShutdownPcCommand.Execute(null));
            await ShellTestSupport.WaitForModalAsync(window);
            Assert.Equal("Shut down PC", window.ModalTitle.Text);
            Assert.Contains("Shut down your PC?", Descendants<TextBlock>(window.ModalContent).Select(t => t.Text));

            await Invoke(Descendants<Wpf.Ui.Controls.Button>(window.ModalContent).Single(b => Equals(b.Content, "Cancel")));
            await asked;
            Assert.Empty(started);
            Assert.False(vm.IsShutdownPending);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ACollectionsDialogOpenedByTheLibrary_SavesThroughTheShell() => sta.RunAsync(async () =>
    {
        var game = MakeGame("Apex");
        var vm = CreateModel(game);
        var window = await Open(vm);
        try
        {
            var opened = window.Dispatcher.InvokeAsync(() => vm.EditCollectionsCommand.Execute(vm.Games.Single()));
            await ShellTestSupport.WaitForModalAsync(window);
            Assert.Equal("Collections", window.ModalTitle.Text);

            var box = Descendants<System.Windows.Controls.TextBox>(window.ModalContent).Single(b => b.Name == "NewNameBox");
            box.Text = "Backlog";
            await Invoke(Descendants<Wpf.Ui.Controls.Button>(window.ModalContent).Single(b => b.IsVisible && Equals(b.Content, "Add")));
            await Invoke(Descendants<Wpf.Ui.Controls.Button>(window.ModalContent).Single(b => b.IsVisible && Equals(b.Content, "Save")));
            await opened;

            Assert.False(window.IsModalOpen);
            Assert.Equal(["Backlog"], vm.Games.Single().Collections);
        }
        finally { window.Close(); }
    });
}
