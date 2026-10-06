using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Nothing may be cut off on a small screen. A 1366 x 768 laptop at 150 percent scaling gives the app a window of about 911 x 484, and 760 x 480 is the smallest the window
/// can be: on every screen, in both themes, every button, box and drop-down must be inside the window, or inside a scroll area that can scroll to it (and not one that only
/// scrolls the other way). Set AXIS_FIT_SHOTS to a folder to also save a picture of each screen.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class SmallScreenFitTests(WpfStaFixture sta)
{
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    private static string Describe(FrameworkElement e)
    {
        var text = e switch { ContentControl c when c.Content is string s => s, TextBox => "(text box)", _ => "" };
        return $"{e.GetType().Name}{(string.IsNullOrEmpty(e.Name) ? "" : "#" + e.Name)} '{text}'";
    }

    /// <summary>Everything on screen that is cut off, as readable lines.</summary>
    private static void Audit(MainWindow window, string screen, List<string> report)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        foreach (var d in Walk(window))
        {
            if (d is not FrameworkElement e || e is not (ButtonBase or TextBox or ComboBox) || !e.IsVisible || e.ActualWidth < 2 || e.ActualHeight < 2)
                continue;

            if (e is ButtonBase && e.TemplatedParent is not null && e.TemplatedParent is not ContentPresenter)
                continue; // a part inside a control (a scroll bar's arrows)

            if (e is not ButtonBase && e.TemplatedParent is not null)
                continue;

            Rect inWindow;
            try { inWindow = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }
            catch (InvalidOperationException) { continue; }

            var problems = new List<string>();
            var reachable = false;
            for (DependencyObject? p = VisualTreeHelper.GetParent(e); p is not null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is not ScrollViewer sv)
                    continue;

                Rect inSv;
                try { inSv = e.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }
                catch (InvalidOperationException) { continue; }

                // The innermost scroll area that clips the control decides: if it can scroll that way the control is reachable, and outer areas no longer matter.
                var overSideways = inSv.Right > sv.ViewportWidth + 1.5 || inSv.Left < -1.5;
                var overDown = inSv.Bottom > sv.ViewportHeight + 1.5 || inSv.Top < -1.5;
                if (overSideways && sv.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
                    problems.Add($"cut off sideways by a scroll area ({inSv.Right - sv.ViewportWidth:0} px over)");

                if (overDown && sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                    problems.Add($"cut off at the bottom by a scroll area ({inSv.Bottom - sv.ViewportHeight:0} px over)");

                if ((overSideways && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) || (overDown && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled))
                {
                    reachable = true;
                    break;
                }
            }

            if (!reachable)
            {
                if (inWindow.Right > root.ActualWidth + 1.5) problems.Add($"outside the window on the right ({inWindow.Right - root.ActualWidth:0} px)");
                if (inWindow.Bottom > root.ActualHeight + 1.5) problems.Add($"outside the window at the bottom ({inWindow.Bottom - root.ActualHeight:0} px)");
                if (inWindow.Left < -1.5) problems.Add($"outside the window on the left ({-inWindow.Left:0} px)");
            }

            foreach (var problem in problems.Distinct())
                report.Add($"[{screen}] {Describe(e)} - {problem}");
        }
    }

    private static async Task Pause(int ms = 400)
    {
        await Task.Delay(ms);
        await ShellTestSupport.SettleAsync();
    }

    private static void Save(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("AXIS_FIT_SHOTS") is not { Length: > 0 } folder)
            return;

        Directory.CreateDirectory(folder);
        var visual = (FrameworkElement)window.Content;
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var back = new DrawingVisual();
        using (var dc = back.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x13)), null, new Rect(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Render(back);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    private static LibraryViewModel Library(int games, ThemeId theme)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Fit-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        var sources = new[] { GameSource.Steam, GameSource.Epic, GameSource.Ea, GameSource.Manual, GameSource.Xbox };
        var list = Enumerable.Range(0, games).Select(i => new GameEntry
        {
            Id = "fit-" + i, Name = (char)('A' + (i * 7) % 26) + "ame number " + i, Source = sources[i % sources.Length],
            ExecutablePath = @"C:\Games\G" + i + @"\g.exe", InstallDir = @"C:\Games\G" + i, DateAdded = DateTime.UtcNow.AddDays(-i),
            TotalPlaySeconds = i < 7 ? (i + 1) * 5400L : 0, LastPlayedUtc = i < 7 ? DateTime.UtcNow.AddDays(-i) : null,
            InstallSizeBytes = (14L + i) * 1024 * 1024 * 1024, Favorite = i % 9 == 0,
        }).ToList();
        vm.SimulateRefreshResult(list);
        vm.SearchText = "x";
        vm.SearchText = "";
        vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 1000L << 30, FreeBytes = 380L << 30 });
        vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 2000L << 30, FreeBytes = 1250L << 30 });
        vm.HasDrives = true;
        vm.AppearanceTheme = theme;
        vm.CleanCategoriesFactory = () => []; // nothing real is scanned
        vm.SystemInfoForTest = () => new SystemInfoSnapshot("A long summary line of the processor, the graphics card and the memory, as My PC shows it at the top",
        [
            new("System", "Desktop24", [new("Computer", "Micro Computer (HK) Tech Limited EliteMiner Series"), new("Windows", "Windows 11 Pro 25H2 (build 26200)", "A hint"), new("Up for", "7 d 19 h")]),
            new("Processor", "Flash24", [new("Processor", "AMD Ryzen 5 7640HS w/ Radeon 760M Graphics"), new("Cores / threads", "6 cores, 12 threads")]),
            new("Memory", "Games24", [new("Installed", "29.8 GB"), new("Module 1", "16 GB DDR5-5600 · Micron Technology"), new("Module 2", "16 GB DDR5-5600 · Micron Technology")]),
        ]);
        return vm;
    }

    private async Task AuditEverything(ThemeId theme, int width, int height, int games, List<string> report)
    {
        var vm = Library(games, theme);
        ThemeManager.Apply(theme);
        var window = ShellTestSupport.OpenMain(vm, width, height);
        var t = $"{theme} {width}x{height} ({games} games)";
        try
        {
            await Pause(700);
            var game = vm.Games.First();
            window.Shell.FocusedGame = game;
            await Pause(400);
            Audit(window, t + " home", report);
            Save(window, $"{theme}-{width}x{height}-home");

            if (theme == ThemeId.ConsoleRibbon)
            {
                window.Shell.ShowLibraryCommand.Execute(null);
                await Pause(600);
                Audit(window, t + " library", report);
                Save(window, $"{theme}-{width}x{height}-library");
                window.Shell.Tab = ShellTab.Home;
                await Pause(300);
            }

            async Task Page(string name, Action open, int wait = 700)
            {
                open();
                await Pause(wait);
                Audit(window, $"{t} {name}", report);
                Save(window, $"{theme}-{width}x{height}-{name}");
                vm.ClosePageCommand.Execute(null);
                await Pause(200);
            }

            await Page("details", () => vm.ShowGameDetailsCommand.Execute(game));
            await Page("play-time", () => vm.ShowPlayTimeCommand.Execute(game));
            await Page("stats", () => vm.ShowStatsCommand.Execute(null));
            await Page("optimize", () => vm.ShowOptimizeCommand.Execute(null), 900);
            await Page("pick", () => vm.PickGameCommand.Execute(null));
            await Page("storage", () => vm.ShowStorageCommand.Execute("C:"));
            await Page("my-pc", () => vm.ShowMyPcCommand.Execute(null), 900);
            for (var category = 0; category < 6; category++)
            {
                var index = category;
                await Page($"settings-{index}", () => vm.OpenSettingsAt(index), 500);
            }

            window.Shell.OpenMoreMenu(game, new Rect(300, 200, 48, 48));
            await Pause(400);
            Audit(window, t + " more menu", report);
            window.Shell.CloseMenu();

            var dialogs = new (string, FrameworkElement)[]
            {
                ("collections", new CollectionsDialog(new CollectionsViewModel("A game", ["Backlog", "Co-op", "Finished"], ["Co-op"]))),
                ("feedback", new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d"))),
                ("palette", new CommandPaletteDialog(new CommandPaletteViewModel(vm.BuildPaletteItems()))),
                ("confirm", new ConfirmDialog("This clears 1.2 GB of temporary files. They cannot be restored.", "Clear files", "Cancel", warning: true)),
            };
            foreach (var (name, content) in dialogs)
            {
                var shown = await ShellTestSupport.ShowDialogAsync(window, name, content);
                await Pause(300);
                Audit(window, $"{t} dialog {name}", report);
                await shown.CloseAsync();
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    }

    [Theory]
    [InlineData(911, 484)]   // 1366 x 768 at 150 percent scaling, less the taskbar
    [InlineData(760, 480)]   // the smallest window
    public void NothingIsCutOff_OnASmallScreen_InEitherTheme(int width, int height) => sta.RunAsync(async () =>
    {
        var report = new List<string>();
        foreach (var theme in new[] { ThemeId.Axis, ThemeId.ConsoleRibbon })
            await AuditEverything(theme, width, height, games: 12, report);

        Assert.True(report.Count == 0, "Cut off on a small screen:\n" + string.Join("\n", report.Take(30)));
    });

    [Fact]
    public void NothingIsCutOff_OnASmallScreen_WithABigLibrary_InThePlayStationTheme() => sta.RunAsync(async () =>
    {
        var report = new List<string>();
        await AuditEverything(ThemeId.ConsoleRibbon, 911, 484, games: 120, report); // the shortened ribbon, the A to Z library and its letter rail

        Assert.True(report.Count == 0, "Cut off on a small screen:\n" + string.Join("\n", report.Take(30)));
    });
}
