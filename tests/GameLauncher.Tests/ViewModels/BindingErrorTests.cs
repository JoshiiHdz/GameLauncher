using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>A XAML binding to a property that does not exist fails silently - the control just shows nothing. This opens every page, every
/// Settings category and the shell's dialogs against a real library and listens for WPF's own binding-error reports.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class BindingErrorTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Bindings-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class Collector : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    private static GameEntry Game(string name, GameSource source, int i) => new()
    {
        Id = name, Name = name, Source = source, ExecutablePath = @"C:\G\" + name + ".exe", InstallDir = @"C:\G\" + name,
        TotalPlaySeconds = (i + 1) * 3600, LastPlayedUtc = DateTime.UtcNow.AddDays(-i), InstallSizeBytes = (i + 1) * (1L << 30),
    };

    [Fact]
    public void TheShell_HasNoBrokenBindings_OnAnyPageDialogOrSettingsCategory() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var collector = new Collector();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(collector);
        try
        {
            var vm = new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory)) { InstallSizeEstimatorForTest = (_, _) => null };
            vm.UninstallWizardFinder = _ => null;
            var sources = new[] { GameSource.Steam, GameSource.Epic, GameSource.Manual, GameSource.Ea, GameSource.Manual };
            vm.SimulateRefreshResult(sources.Select((s, i) => Game("Game " + i, s, i)).ToList());
            vm.SearchText = "x"; vm.SearchText = "";
            vm.Drives.Add(new DriveSpaceInfo { Letter = "C:", Label = "Local Disk", TotalBytes = 500L << 30, FreeBytes = 8L << 30 });
            vm.Drives.Add(new DriveSpaceInfo { Letter = "D:", Label = "Games", TotalBytes = 500L << 30, FreeBytes = 300L << 30 });
            vm.Drives.Add(new DriveSpaceInfo { Letter = "E:", Label = "Archive", TotalBytes = 2000L << 30, FreeBytes = 900L << 30, IsIgnored = true });
            vm.SearchableDrives.Add(new DriveToggleItem("E:", "Archive", "1 TB used", () => false, _ => { }));
            vm.HasDrives = true;
            vm.HasMultipleDrives = true;
            vm.SelectViewCommand.Execute("all");

            var window = ShellTestSupport.OpenMain(vm, 1280, 900);
            await ShellTestSupport.SettleAsync();

            foreach (var view in new[] { "favorites", "recent", "unplayed", "all" })
            {
                vm.SelectViewCommand.Execute(view);
                await ShellTestSupport.SettleAsync();
            }

            vm.SelectDriveCommand.Execute("C:");
            await ShellTestSupport.SettleAsync();
            vm.SelectDriveCommand.Execute("C:");

            vm.ShowStatsCommand.Execute(null); await ShellTestSupport.SettleAsync();
            vm.PickGameCommand.Execute(null); await ShellTestSupport.SettleAsync();
            vm.ShowGameDetailsCommand.Execute(vm.Games[0]); await ShellTestSupport.SettleAsync();
            vm.ShowStorageCommand.Execute("C:"); await ShellTestSupport.SettleAsync();
            vm.ShowOptimizeCommand.Execute(null); await Task.Delay(800); await ShellTestSupport.SettleAsync();

            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            var page = Descendants<SettingsPage>(window.PageContent).Single();
            for (var i = 0; i < page.CategoryList.Items.Count; i++)
            {
                page.CategoryList.SelectedIndex = i;
                await ShellTestSupport.SettleAsync();
                window.UpdateLayout();
            }

            vm.ClosePage();

            var dialogs = new (string Title, FrameworkElement Content)[]
            {
                ("Collections", new CollectionsDialog(new CollectionsViewModel("Game 0", ["Backlog"], []))),
                ("Send Feedback", new FeedbackDialog(new FeedbackViewModel(new FeedbackService(null, null, () => DateTime.UtcNow), () => "d"))),
                ("Command palette", new CommandPaletteDialog(new CommandPaletteViewModel(vm.BuildPaletteItems()))),
                ("Confirm", new ConfirmDialog("Sure?", "Yes", "No", warning: true)),
            };
            foreach (var (title, content) in dialogs)
            {
                var shown = await ShellTestSupport.ShowDialogAsync(window, title, content);
                await shown.CloseAsync();
            }

            window.Close();
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(collector);
        }

        Assert.True(collector.Messages.Count == 0, "Binding problems:\n" + string.Join("\n---\n", collector.Messages.Take(12)));
    });

    [Fact]
    public void TheListener_ReallyHearsABrokenBinding_SoTheTestAboveMeansSomething() => sta.RunAsync(async () =>
    {
        var collector = new Collector();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(collector);
        try
        {
            var text = new TextBlock { DataContext = new object() };
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty"));
            text.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(collector);
        }

        Assert.Contains(collector.Messages, m => m.Contains("NoSuchProperty"));
        await Task.CompletedTask;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
