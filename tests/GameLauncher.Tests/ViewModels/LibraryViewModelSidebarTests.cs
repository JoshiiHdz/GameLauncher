using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Shell;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

[Collection(WpfStaCollection.Name)]
public sealed class LibraryViewModelSidebarTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Sidebar-" + Guid.NewGuid());

    private LibraryViewModel CreateModel() => new(new SettingsService(_directory), new PendingUpdateNotesService(_directory));

    private static void Populate(LibraryViewModel vm)
    {
        vm.SimulateRefreshResult([new GameEntry
        {
            Id = "sidebar-xbox", Name = "Sidebar Test Game", Source = GameSource.Xbox,
            InstallDir = @"G:\SidebarTest", ExecutablePath = @"G:\SidebarTest\game.exe",
        }]);
        vm.SearchText = "Sidebar"; // Runs the production filter, including source availability.
        vm.SearchText = "";
        vm.Drives.Add(new DriveSpaceInfo { Letter = "G:", Label = "Local Disk", TotalBytes = 1000, FreeBytes = 650 });
        vm.HasDrives = true;
    }

    private static MainWindow Open(LibraryViewModel vm)
    {
        var window = new MainWindow(vm, startRuntimeServices: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task Settle(MainWindow window)
    {
        // The sidebar's 200 ms width storyboard runs on the real dispatcher: wait until the width has stopped changing (at least 240 ms),
        // and has reached one of its two resting widths (64 or 232), not for a fixed time - a busy machine can stretch the animation past it.
        var last = -1d;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(60);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var width = window.SidebarPanel.ActualWidth;
            if (i >= 3 && Math.Abs(width - last) < 0.01 && (Math.Abs(width - 64) < 0.5 || Math.Abs(width - 232) < 0.5))
                break;
            last = width;
        }
    }

    private static async Task ClickSidebarToggle(MainWindow window)
    {
        var peer = new ButtonAutomationPeer(window.SidebarToggleButton);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Settle(window);
    }

    [Fact]
    public void Settings_ShowsStartupAndExternalTrackingToggles_WithRealBindings() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Assert.Equal("Settings", window.PageTitleText.Text);
            var page = Descendants<SettingsPage>(window.PageContent).Single();
            var startup = Assert.Single(Descendants<Wpf.Ui.Controls.ToggleSwitch>(window.PageContent).Where(t => Equals(t.Content, "Start with Windows")));
            Assert.True(page.GeneralSection.IsVisible); // opens on General; the other categories are not even laid out yet

            // Play time lives in General now, beside startup and the other behaviour switches.
            var tracking = Assert.Single(Descendants<Wpf.Ui.Controls.ToggleSwitch>(page.GeneralSection).Where(t => Equals(t.Content, "Track games opened outside the launcher")));
            Assert.DoesNotContain(Descendants<Wpf.Ui.Controls.ToggleSwitch>(page.LibrarySection), t => Equals(t.Content, "Track games opened outside the launcher"));
            Assert.False(startup.IsChecked);
            Assert.True(tracking.IsChecked);
            tracking.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(vm.TrackExternalGames);
            Assert.False(new SettingsService(_directory).Load().TrackExternalGames);
            Assert.Equal("StartWithWindows", startup.GetBindingExpression(ToggleButton.IsCheckedProperty)!.ParentBinding.Path.Path);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EmptyViews_ExplainFavoritesAndRecent_WithoutDiscoveryActions() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = Open(vm);
        try
        {
            foreach (var withInstalledGame in new[] { false, true })
            {
                if (withInstalledGame) Populate(vm);
                foreach (var view in new[] { "favorites", "recent" })
                {
                    vm.SelectViewCommand.Execute(view);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Assert.True(window.LibraryEmptyState.IsVisible);
                    Assert.False(window.EmptyDiscoveryActions.IsVisible);
                    Assert.Equal(view == "favorites" ? "No favorites yet" : "Nothing played yet",
                        window.EmptyStateHeading.Text);
                    Assert.DoesNotContain("folder", vm.EmptyStateDescription, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(view == "favorites" ? Wpf.Ui.Controls.SymbolRegular.Star24 : Wpf.Ui.Controls.SymbolRegular.Clock24,
                        window.EmptyStateIcon.Symbol);
                }
            }
            // An ordinary library game remains present: these messages are about the selected view.
            vm.SelectViewCommand.Execute("all");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(window.LibraryEmptyState.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EmptyViews_DistinguishFilteredResults_FromMissingFavoritesOrHistory() => sta.RunAsync(() =>
    {
        var vm = CreateModel();
        vm.SimulateRefreshResult([new GameEntry
        {
            Id = "played-favorite", Name = "Test Game", Source = GameSource.Steam,
            InstallDir = @"G:\Test", ExecutablePath = @"G:\Test\game.exe",
            Favorite = true, TotalPlaySeconds = 60, LastPlayedUtc = DateTime.UtcNow,
        }]);
        foreach (var view in new[] { "favorites", "recent" })
        {
            vm.SelectViewCommand.Execute(view);
            Assert.False(vm.HasNoGames);
            vm.SearchText = "no match";
            Assert.True(vm.HasNoGames);
            Assert.Contains("match your filters", vm.EmptyStateTitle);
            Assert.False(vm.ShowEmptyDiscoveryActions);
            vm.SearchText = "";
            Assert.False(vm.HasNoGames);
            vm.DetectSteam = false;
            Assert.True(vm.HasNoGames);
            Assert.Contains("match your filters", vm.EmptyStateTitle);
            vm.DetectSteam = true;
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void EmptyLibrary_KeepsDiscoveryActions_OnlyInUnfilteredAllGames() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = Open(vm);
        try
        {
            vm.SelectViewCommand.Execute("all");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(window.LibraryEmptyState.IsVisible);
            Assert.True(window.EmptyDiscoveryActions.IsVisible);
            Assert.Equal("No games found yet", window.EmptyStateHeading.Text);
            vm.SearchText = "missing";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(window.EmptyDiscoveryActions.IsVisible);
            Assert.Equal("No games match this view", window.EmptyStateHeading.Text);
            vm.SearchText = "";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(window.EmptyDiscoveryActions.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CustomTitleBar_UsesNativeCaptionInNormalAndMaximizedStates_ControlsRemainClientArea() => sta.RunAsync(async () =>
    {
        var window = Open(CreateModel());
        try
        {
            foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal })
            {
                window.WindowState = state;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var chrome = WindowChrome.GetWindowChrome(window);
                Assert.Equal(44, chrome.CaptionHeight);
                Assert.True(chrome.ResizeBorderThickness.Left > 0);
                Assert.Equal(2, NativeHitTest(window, window.WindowDragBar, new Point(100, 24))); // HTCAPTION
                foreach (var control in Descendants<FrameworkElement>(window.WindowDragBar)
                             .Where(e => e is ButtonBase or TextBoxBase).Where(e => e.IsVisible))
                {
                    Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(control));
                    Assert.Equal(1, NativeHitTest(window, control,
                        new Point(control.ActualWidth / 2, control.ActualHeight / 2))); // HTCLIENT
                }
            }
            Assert.Equal(10, NativeHitTest(window, window, new Point(1, window.ActualHeight / 2))); // HTLEFT resize
        }
        finally { window.Close(); }
    });

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private static int NativeHitTest(MainWindow window, UIElement element, Point local)
    {
        var point = element.PointToScreen(local);
        var coordinates = ((int)point.X & 0xffff) | (((int)point.Y & 0xffff) << 16);
        return (int)SendMessage(new WindowInteropHelper(window).Handle, 0x0084, IntPtr.Zero, (IntPtr)coordinates);
    }

    [Fact]
    public void CaptionButton_ChangesGlyphAndTooltip_AndRestoresWhenMaximized() => sta.RunAsync(async () =>
    {
        var window = Open(CreateModel());
        try
        {
            var normalGlyph = window.CaptionMaximizeGlyph.Data.ToString();
            Assert.Equal("Maximize", window.CaptionMaximizeButton.ToolTip);
            var peer = new ButtonAutomationPeer(window.CaptionMaximizeButton);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal("Restore Down", window.CaptionMaximizeButton.ToolTip);
            Assert.NotEqual(normalGlyph, window.CaptionMaximizeGlyph.Data.ToString());
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal("Maximize", window.CaptionMaximizeButton.ToolTip);
            Assert.Equal(normalGlyph, window.CaptionMaximizeGlyph.Data.ToString());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HeroPlatformBadge_UsesLauncherIcon_AndUpdatesToXboxLogoOrManualFallback() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = Open(vm);
        try
        {
            var icon = new BitmapImage(new Uri("pack://application:,,,/GameLauncher;component/Assets/XboxLogo.png"));
            foreach (var source in new[] { GameSource.Steam, GameSource.Ea, GameSource.Xbox, GameSource.Manual })
            {
                var game = new GameEntry
                {
                    Id = "hero-" + source, Name = "Hero game", Source = source,
                    InstallDir = @"G:\HeroTest", ExecutablePath = @"G:\HeroTest\game.exe",
                    PlatformIcon = source is GameSource.Steam or GameSource.Ea ? icon : null,
                };
                vm.FeaturedGame = game;
                vm.HasFeaturedGame = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Same(game, window.HeroPlatformBadge.DataContext);
                Assert.Same(game.PlatformIcon, window.HeroPlatformIcon.Source);
                Assert.Equal(source == GameSource.Xbox ? Visibility.Visible : Visibility.Collapsed,
                    window.HeroXboxLogo.Visibility);
                Assert.Equal(source == GameSource.Manual ? Visibility.Visible : Visibility.Collapsed,
                    window.HeroPlatformFallback.Visibility);
                if (source == GameSource.Xbox)
                    Assert.NotNull(window.HeroXboxLogo.Source);
                if (source == GameSource.Manual)
                    Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Games24, window.HeroPlatformFallback.Symbol);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RealToggle_ChangesWidthAndLayout_AndPersistsAcrossFreshInstances() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        Assert.True(vm.IsSidebarExpanded);
        var window = Open(vm);
        try
        {
            await Settle(window);
            Assert.Equal(232, window.SidebarPanel.ActualWidth, 1);
            Assert.True(window.ExpandedLaunchers.IsVisible);
            Assert.False(window.CollapsedLaunchers.IsVisible);
            await ClickSidebarToggle(window);
            Assert.False(vm.IsSidebarExpanded);
            Assert.Equal(64, window.SidebarPanel.ActualWidth, 1);
            Assert.False(window.ExpandedLaunchers.IsVisible);
            Assert.True(window.CollapsedLaunchers.IsVisible);
            Assert.False(CreateModel().IsSidebarExpanded);
            await ClickSidebarToggle(window);
            Assert.True(vm.IsSidebarExpanded);
            Assert.Equal(232, window.SidebarPanel.ActualWidth, 1);
            Assert.True(CreateModel().IsSidebarExpanded);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CollapsedStartup_HidesSectionHeadings_ExpandedShowsThemOnlyWithData() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        vm.ToggleSidebarCommand.Execute(null);
        vm = CreateModel(); // Test persisted startup, not only a transition on an existing window.
        Populate(vm);
        var window = Open(vm);
        try
        {
            await Settle(window);
            TextBlock[] Headings() => Descendants<TextBlock>(window.SidebarPanel).Where(t => t.Text is "LAUNCHERS" or "DRIVES").ToArray();
            // The rail has no titles: they sit in a collapsed wrapper, so none is showing (and none may even have been built yet).
            Assert.All(Headings(), t => Assert.False(t.IsVisible));
            Assert.Equal(64, window.SidebarPanel.ActualWidth, 1);
            await ClickSidebarToggle(window);
            var headings = Headings();
            Assert.Equal(2, headings.Length);
            Assert.All(headings, t => Assert.True(t.IsVisible));
            vm.HasDrives = false;
            vm.HasAnySourceGames = false;
            await Settle(window);
            Assert.All(headings, t => Assert.False(t.IsVisible));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RealLauncherControls_StayAvailableWhenDisabled_AndSynchronizeAcrossLayouts() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var window = Open(vm);
        try
        {
            await Settle(window);
            var rows = Descendants<Border>(window.ExpandedLaunchers).Where(b => b.Name == "Row" && b.IsVisible).ToArray();
            Assert.Single(rows);
            Assert.Equal(GameSource.Xbox, Assert.IsType<SourceToggleItem>(rows[0].DataContext).Source);
            var toggle = Assert.Single(Descendants<Wpf.Ui.Controls.ToggleSwitch>(rows[0]));
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Settle(window);
            Assert.False(vm.DetectXbox);
            Assert.Empty(vm.Games);
            Assert.True(rows[0].IsVisible); // Must remain reachable to switch it back on.
            await ClickSidebarToggle(window);
            var rail = Assert.Single(Descendants<ToggleButton>(window.CollapsedLaunchers).Where(b => b.IsVisible));
            Assert.False(rail.IsChecked);
            Assert.Contains("hidden", Assert.IsType<string>(rail.ToolTip));
            var peer = new ToggleButtonAutomationPeer(rail);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
            await Settle(window);
            Assert.True(vm.DetectXbox);
            Assert.Single(vm.Games);
            Assert.Contains("shown", Assert.IsType<string>(rail.ToolTip));
            Assert.True(CreateModel().DetectXbox);
            await ClickSidebarToggle(window);
            Assert.True(toggle.IsChecked);
            SavePreviewIfRequested(window, "sidebar-expanded.png");
            await ClickSidebarToggle(window);
            SavePreviewIfRequested(window, "sidebar-collapsed.png");
            rail.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Settle(window);
            SavePreviewIfRequested(window, "sidebar-collapsed-disabled.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EveryDrive_ShowsUsageAndSpaceWhenExpanded_HidesDetailsWhenCollapsed_AndStillFilters() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false; // this test reads the bar's value, not its easing; restored in Dispose
        var vm = CreateModel();
        Populate(vm);
        vm.InstallSizeEstimatorForTest = (_, _) => 512;
        const long gb = 1024L * 1024 * 1024;
        vm.Drives.Clear();
        vm.Drives.Add(new DriveSpaceInfo { Letter = "G:", Label = "Games", TotalBytes = 1000 * gb, FreeBytes = 650 * gb });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "S:", Label = "Steam Library", TotalBytes = 500 * gb, FreeBytes = 50 * gb });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "X:", Label = "Xbox Games", TotalBytes = 200 * gb, FreeBytes = 200 * gb });
        var window = Open(vm);
        try
        {
            await Settle(window);
            var buttons = Descendants<Button>(window.SidebarPanel).Where(b => b.Name == "DriveButton").ToArray();
            Assert.Equal(3, buttons.Length);
            foreach (var button in buttons)
            {
                var drive = Assert.IsType<DriveSpaceInfo>(button.DataContext);
                var bar = Assert.Single(Descendants<ProgressBar>(button));
                var summary = Assert.Single(Descendants<TextBlock>(button).Where(t => t.Name == "DriveSpaceSummary"));
                Assert.True(bar.IsVisible);
                Assert.False(bar.IsIndeterminate);
                Assert.Equal(0, bar.Minimum);
                Assert.Equal(1, bar.Maximum);
                Assert.Equal(drive.UsedFraction, bar.Value, 5);
                Assert.True(bar.ActualWidth > 150, $"width={bar.ActualWidth} button={button.ActualWidth} sidebar={window.SidebarPanel.ActualWidth}");
                Assert.True(summary.IsVisible);
                Assert.Equal(drive.SummaryText, summary.Text);
                Assert.Equal(TextWrapping.Wrap, summary.TextWrapping);
                var bounds = summary.TransformToAncestor(button).TransformBounds(new Rect(summary.RenderSize));
                Assert.True(bounds.Bottom <= button.ActualHeight + 1);
                Assert.True(bounds.Right <= button.ActualWidth + 1);
            }
            Assert.Contains("350 GB used of 1000 GB (650 GB free)", Descendants<TextBlock>(buttons[0]).Select(t => t.Text));
            SavePreviewIfRequested(window, "drive-space-expanded.png");

            var peer = new ButtonAutomationPeer(buttons[0]);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(window);
            Assert.Equal("G:", vm.SelectedDriveLetter);
            Assert.Single(vm.Games);

            await ClickSidebarToggle(window);
            foreach (var button in buttons)
            {
                Assert.True(button.ActualHeight >= 36, $"A rail row is at least 36 high, was {button.ActualHeight}.");
                Assert.Equal(48, button.ActualWidth, 1);
                Assert.False(Assert.Single(Descendants<ProgressBar>(button)).IsVisible);
                Assert.False(Assert.Single(Descendants<TextBlock>(button).Where(t => t.Name == "DriveSpaceSummary")).IsVisible);
            }
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(window);
            Assert.Null(vm.SelectedDriveLetter);
            SavePreviewIfRequested(window, "drive-space-collapsed.png");
            await ClickSidebarToggle(window);
            Assert.All(buttons, b => Assert.True(Assert.Single(Descendants<ProgressBar>(b)).IsVisible));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void XboxLogo_IsBundled_AndDisabledCopyIsGreyWithOriginalTransparency() => sta.RunAsync(() =>
    {
        bool enabled = true;
        var item = new SourceToggleItem(GameSource.Xbox, "Xbox", "X", () => enabled, value => enabled = value);
        var color = Assert.IsAssignableFrom<BitmapSource>(item.DisplayIcon);
        Assert.True(item.HasIcon);
        Assert.True(color.IsFrozen);
        item.IsEnabled = false;
        var grey = Assert.IsAssignableFrom<BitmapSource>(item.DisplayIcon);
        Assert.NotSame(color, grey);
        Assert.True(grey.IsFrozen);
        Assert.Equal(color.PixelWidth, grey.PixelWidth);
        var original = Pixels(color);
        var result = Pixels(grey);
        Assert.Contains(Enumerable.Range(0, original.Length / 4), i => original[i * 4] != original[i * 4 + 1]);
        for (int i = 0; i < result.Length; i += 4)
        {
            Assert.Equal(result[i], result[i + 1]);
            Assert.Equal(result[i], result[i + 2]);
            Assert.Equal(original[i + 3], result[i + 3]);
        }
        // The logo's green X and neutral circle have almost equal perceptual luminance.
        // A technically grey image must not flatten those into an indistinguishable disk.
        var opaqueShades = Enumerable.Range(0, result.Length / 4)
            .Where(i => result[i * 4 + 3] == 255).Select(i => (int)result[i * 4]).ToArray();
        Assert.True(opaqueShades.Max() - opaqueShades.Min() >= 25,
            $"The disabled Xbox logo lost its X/background contrast ({opaqueShades.Min()}–{opaqueShades.Max()}).");
        item.IsEnabled = true;
        Assert.Same(color, item.DisplayIcon);
        return Task.CompletedTask;
    });

    [Fact]
    public void GreyscaleIcon_WhiteEaLetteringDoesNotDisappearIntoSaturatedBlueBackground() => sta.RunAsync(() =>
    {
        byte[] pixels = [255, 90, 40, 255, 255, 255, 255, 255]; // EA-style blue + white, BGRA.
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
        var grey = Pixels(Assert.IsAssignableFrom<BitmapSource>(GreyscaleIcon.From(source)));
        Assert.True(grey[4] - grey[0] >= 40, "The disabled logo must retain readable white-on-blue contrast.");
        Assert.Equal(grey[0], grey[1]);
        Assert.Equal(grey[1], grey[2]);
        Assert.Equal(pixels, Pixels(source));
        return Task.CompletedTask;
    });

    [Fact]
    public void GreyscaleIcon_PreservesAlpha_DoesNotMutateSource_AndAcceptsNull() => sta.RunAsync(() =>
    {
        byte[] sourcePixels = [0, 80, 0, 128, 0, 0, 0, 0, 0, 200, 40, 255];
        var source = BitmapSource.Create(3, 1, 96, 96, PixelFormats.Pbgra32, null, sourcePixels, 12);
        var result = Assert.IsAssignableFrom<BitmapSource>(GreyscaleIcon.From(source));
        Assert.Equal(sourcePixels, Pixels(source));
        var grey = Pixels(result);
        for (int i = 0; i < grey.Length; i += 4)
        {
            Assert.Equal(grey[i], grey[i + 1]);
            Assert.Equal(grey[i], grey[i + 2]);
            Assert.Equal(sourcePixels[i + 3], grey[i + 3]);
        }
        Assert.True(result.IsFrozen);
        Assert.Null(GreyscaleIcon.From(null));
        return Task.CompletedTask;
    });

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    [Fact]
    public void Collections_ListInTheSidebar_FilterOnClick_AndDeleteFromTheirMenu() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        Populate(vm);
        var game = vm.Games.Single();
        vm.SetGameCollections(game, ["Co-op", "Backlog"]);
        var window = Open(vm);
        try
        {
            await Settle(window);

            var buttons = Descendants<Button>(window.SidebarPanel).Where(b => b.Name == "CollectionButton").ToArray();
            Assert.Equal(["Backlog", "Co-op"], buttons.Select(b => (string)b.CommandParameter));
            Assert.All(buttons, b => Assert.Same(vm.SelectCollectionCommand, b.Command));
            Assert.All(buttons, b => Assert.True(b.IsVisible));
            Assert.Contains("COLLECTIONS", Descendants<TextBlock>(window.SidebarPanel).Where(t => t.IsVisible).Select(t => t.Text));
            Assert.Contains("1", Descendants<TextBlock>(buttons[0]).Where(t => t.IsVisible).Select(t => t.Text));
            SavePreviewIfRequested(window, "collections-expanded.png");

            ((IInvokeProvider)new ButtonAutomationPeer(buttons[1]).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Co-op", vm.SelectedCollection);
            Assert.Equal("Co-op", vm.LibraryHeaderText);

            // The row's own right-click menu carries a Delete item wired to that collection.
            var menu = buttons[1].ContextMenu ?? throw new InvalidOperationException("The collection row has no menu.");
            menu.PlacementTarget = buttons[1];
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var delete = menu.Items.OfType<MenuItem>().Single();
            Assert.Same(vm.DeleteCollectionCommand, delete.Command);
            Assert.Equal("Co-op", delete.CommandParameter);
            menu.IsOpen = false;

            await ClickSidebarToggle(window);
            Assert.False(vm.IsSidebarExpanded);
            Assert.All(buttons, b => Assert.True(b.IsVisible));
            Assert.DoesNotContain("COLLECTIONS", Descendants<TextBlock>(window.SidebarPanel).Where(t => t.IsVisible).Select(t => t.Text));
            SavePreviewIfRequested(window, "collections-collapsed.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CollectionsDialog_ShowsTickBoxesForExistingCollections_AndSavesTheirState() => sta.RunAsync(async () =>
    {
        var dialog = new CollectionsViewModel("Sidebar Test Game", ["Backlog", "Co-op"], ["Co-op"]);
        var window = Open(CreateModel());
        try
        {
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Collections", new CollectionsDialog(dialog));
            Assert.Equal("Collections", window.ModalTitle.Text);

            var boxes = Descendants<CheckBox>(window.ModalContent).ToArray();
            Assert.Equal(["Backlog", "Co-op"], boxes.Select(b => Descendants<TextBlock>(b).Select(t => t.Text).First(t => t.Length > 1))); // each row is the tick glyph, the name and its game count
            Assert.Equal([false, true], boxes.Select(b => b.IsChecked == true));

            boxes[0].IsChecked = true; // ticking a box writes through to the view model
            Assert.Equal(["Backlog", "Co-op"], dialog.ChosenNames());
            SavePreviewOf((FrameworkElement)window.Content, "collections-dialog.png");
            await shown.CloseAsync();
            Assert.False(window.IsModalOpen);
            Assert.False(dialog.Saved); // closed without Save
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FeedbackDialog_ShowsTheFormWithItsControlsWired() => sta.RunAsync(async () =>
    {
        var form = new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d");
        var window = Open(CreateModel());
        try
        {
            var shown = await ShellTestSupport.ShowDialogAsync(window, "Send Feedback", new FeedbackDialog(form));
            var dialog = window.ModalContent;

            var boxes = Descendants<Wpf.Ui.Controls.TextBox>(dialog).ToArray();
            Assert.Equal(2, boxes.Length);
            var send = Assert.Single(Descendants<Wpf.Ui.Controls.Button>(dialog).Where(b => Equals(b.Content, "Send")));
            Assert.Same(form.SendCommand, send.Command);
            Assert.False(send.IsEnabled); // nothing typed yet

            boxes[0].Text = "The cover for my game is wrong";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("The cover for my game is wrong", form.Message);
            Assert.True(send.IsEnabled);

            // No relay in this build: sending reports it and reveals the GitHub fallback.
            form.SendCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var fallback = Assert.Single(Descendants<Wpf.Ui.Controls.Button>(dialog).Where(b => Equals(b.Content, "Report on GitHub instead")));
            Assert.True(fallback.IsVisible);
            SavePreviewOf((FrameworkElement)window.Content, "feedback-window.png");
            await shown.CloseAsync();
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Settings_HasAReportBugButton_WiredToTheCommand() => sta.RunAsync(async () =>
    {
        var vm = CreateModel();
        var window = Open(vm);
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var page = Descendants<SettingsPage>(window.PageContent).Single();
            page.CategoryList.SelectedItem = page.AboutCategory;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var button = Assert.Single(Descendants<Wpf.Ui.Controls.Button>(window.PageContent).Where(b => Equals(b.Content, "Send Feedback or Report a Bug")));
            Assert.Same(vm.ReportBugCommand, button.Command);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    // Optional isolated markup renders for local visual review; never writes to real AppData.
    private static void SavePreviewIfRequested(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("GAME_LAUNCHER_SIDEBAR_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void SavePreviewOf(FrameworkElement window, string name)
    {
        if (Environment.GetEnvironmentVariable("GAME_LAUNCHER_SIDEBAR_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // The acrylic backdrop is not captured off-screen, so paint the dark app colour behind the window first.
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C)), null, new Rect(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    public void Dispose()
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => System.Windows.SystemParameters.ClientAreaAnimation;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
