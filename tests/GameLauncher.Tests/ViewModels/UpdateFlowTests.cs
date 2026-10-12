using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The update flow: the answer is shown where the person is (Settings says it in place), and a ready update has a button in both themes' top bars.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class UpdateFlowTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary(ThemeId theme)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Update-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" }]);
        vm.SearchText = "x";
        vm.SearchText = "";
        vm.AppearanceTheme = theme;
        return vm;
    }

    [Fact]
    public void CheckingNow_ShowsItsProgressAndItsAnswer_InPlace() => sta.RunAsync(async () =>
    {
        var vm = NewLibrary(ThemeId.Axis);
        Assert.Contains("You're on version v", vm.UpdateStatusText);   // never blank, even before any check

        var gate = new TaskCompletionSource<UpdateCheckResult>();
        vm.CheckForUpdateForTest = () => gate.Task;
        var running = vm.CheckForUpdateNowCommand.ExecuteAsync(null);

        Assert.Equal("Checking for updates...", vm.UpdateStatusText);
        Assert.Equal("Checking...", vm.CheckUpdateButtonText);
        Assert.False(vm.CheckForUpdateNowCommand.CanExecute(null));    // a second press while it runs does nothing

        gate.SetResult(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
        await running;

        Assert.Contains("latest version", vm.UpdateStatusText);
        Assert.Contains("v" + AppInfo.Version, vm.UpdateStatusText);
        Assert.Equal("Check for Updates Now", vm.CheckUpdateButtonText);
        Assert.True(vm.CheckForUpdateNowCommand.CanExecute(null));
        Assert.False(vm.UpdateAvailable);

        vm.CheckForUpdateForTest = () => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.CheckFailed));
        await vm.CheckForUpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("Couldn't check", vm.UpdateStatusText);

        vm.CheckForUpdateForTest = () => Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NotInstalled));
        await vm.CheckForUpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("not an installed build", vm.UpdateStatusText);
    });

    [Fact]
    public void AFoundUpdate_IsNamedEverywhere_AndToasts() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ThemeId.ConsoleRibbon);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            Assert.False(window.RibbonView.UpdateButton.IsVisible);

            vm.ShowUpdateFound("9.9.9");
            await ShellTestSupport.SettleAsync();

            Assert.True(window.RibbonView.UpdateButton.IsVisible);                           // the PlayStation theme has somewhere to apply it
            Assert.Equal("Update v9.9.9", window.RibbonView.UpdateButton.Content);
            Assert.Equal("Install v9.9.9 and restart", vm.InstallUpdateText);
            Assert.Equal("Update available", window.Shell.FeedbackText);                     // and a toast, wherever the person is
            Assert.Contains("v9.9.9", window.Shell.FeedbackDetail);
            Assert.Contains(window.PadControls(), c => ReferenceEquals(c, window.RibbonView.UpdateButton)); // the pad can reach it

            vm.OpenSettingsAt(LibraryViewModel.AboutSettingsCategory);                    // and Settings says it in place, with the Install button
            await ShellTestSupport.SettleAsync();
            var page = System.Windows.Media.VisualTreeHelper.GetChildrenCount(window.PageHost) > 0 ? FindSettings(window.PageHost) : null;
            Assert.NotNull(page);
            Assert.True(page!.InstallUpdateButton.IsVisible);
            Assert.Equal("Install v9.9.9 and restart", page.InstallUpdateButton.Content);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    private static SettingsPage? FindSettings(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is SettingsPage page)
                return page;

            if (FindSettings(child) is { } nested)
                return nested;
        }

        return null;
    }

    [Fact]
    public void InAxis_AUpdateButtonIsInTheTitleBar_OnEveryPage() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = NewLibrary(ThemeId.Axis);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            Assert.False(window.AxisUpdateButton.IsVisible);

            vm.ShowUpdateFound("9.9.9");
            vm.ShowSettingsCommand.Execute(null);               // a page covers the library, and its banner with it
            await ShellTestSupport.SettleAsync();

            Assert.True(window.AxisUpdateButton.IsVisible);
            Assert.Equal("Update v9.9.9", window.AxisUpdateButton.Content);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheAxisUpdateButton_FitsTheSmallestWindow_WithTheOtherTitleBarControls() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        var vm = NewLibrary(ThemeId.Axis);
        vm.SetControllerStatus(true, null); // the controller button and pill are there too
        vm.ShowUpdateFound("12.34.56");
        var window = ShellTestSupport.OpenMain(vm, 760, 480);
        try
        {
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var root = (System.Windows.FrameworkElement)window.Content;
            var update = FindBox(window.AxisUpdateButton, root);
            var pill = FindBox(window.AxisControllerPill, root);
            var mode = FindBox(window.AxisControllerModeButton, root);
            var search = FindBox(window.SearchBox, root);
            Assert.True(update.Left >= 0 && update.Right <= root.ActualWidth + 0.5, $"the update button is cut off ({update})");
            update.Inflate(-1, -1); // boxes that merely touch are neighbours, not an overlap
            Assert.False(update.IntersectsWith(pill) || update.IntersectsWith(mode) || update.IntersectsWith(search), $"the update button overlaps its neighbours ({update} / {pill} / {mode} / {search})");
        }
        finally { window.Close(); }
    });

    private static System.Windows.Rect FindBox(System.Windows.FrameworkElement element, System.Windows.Media.Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new System.Windows.Rect(0, 0, element.ActualWidth, element.ActualHeight));
}
