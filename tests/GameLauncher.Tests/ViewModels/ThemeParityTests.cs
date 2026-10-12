using System.IO;
using System.Windows;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The themes differ in look and in how they are driven, not in what they can do: what Axis offers, the PlayStation theme offers too.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ThemeParityTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary(ThemeId theme)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Parity-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x";
        vm.SearchText = "";
        vm.AppearanceTheme = theme;
        return vm;
    }

    [Fact]
    public void HiddenGames_CanBeShownAndUnhidden_InThePlayStationLibrary_AsInAxis() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ThemeId.ConsoleRibbon);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            var borderlands = vm.Games.Single(g => g.Name == "Borderlands");
            vm.ToggleHiddenCommand.Execute(borderlands);
            await ShellTestSupport.SettleAsync();

            Assert.DoesNotContain(borderlands, window.Shell.LibraryGroups.SelectMany(g => g.Games));    // hidden: not in the grid
            window.Shell.ShowLibraryCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RibbonView.HiddenToggle.IsVisible);                                     // the way to reveal them is offered (it is not before there are any)

            vm.ToggleShowHiddenCommand.Execute(null);
            await ShellTestSupport.SettleAsync();

            var hidden = window.Shell.LibraryGroups.Single(g => g.Letter == "Hidden");
            Assert.Contains(borderlands, hidden.Games);                                                // shown, in a group of their own
            var menu = new ShellState(vm);
            menu.OpenCardMenu(borderlands, new Rect(0, 0, 10, 10));
            Assert.Contains(menu.MenuItems, i => i.Header == "Unhide");                                // and can be unhidden from the card's menu
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void ACollection_CanBeDeletedFromThePlayStationFilters_AsFromAxisRightClick() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var vm = NewLibrary(ThemeId.ConsoleRibbon);
            var apex = vm.Games.Single(g => g.Name == "Apex");
            vm.SetGameCollections(apex, ["Co-op"]);
            Assert.Contains(vm.Collections, c => c.Name == "Co-op");

            var shell = new ShellState(vm);
            shell.OpenCollectionMenu("Co-op", new Rect(0, 0, 10, 10));

            Assert.Equal(ShellMenuKind.Collection, shell.MenuKind);
            Assert.Equal("Co-op", shell.MenuHeader);
            var delete = Assert.Single(shell.MenuItems);
            Assert.Equal("Delete collection", delete.Header);
            Assert.True(delete.Destructive);

            delete.Run!();

            Assert.DoesNotContain(vm.Collections, c => c.Name == "Co-op");
            Assert.Empty(apex.Collections);                                                            // the game itself is untouched
            shell.CloseMenu();
            Assert.Equal(string.Empty, shell.MenuCollection);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void TheOptimizeShortcut_OpensTheOptimizePage_InBothThemes() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ThemeId.ConsoleRibbon);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            Assert.Same(vm.ShowOptimizeCommand, window.RibbonView.QuickOptimize.Command);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }

        await Task.CompletedTask;
    });

    [Fact]
    public void F11AndThePalettesControllerCommand_ToggleTheMode_InEveryTheme() => sta.RunAsync(async () =>
    {
        foreach (var theme in new[] { ThemeId.Axis, ThemeId.ConsoleRibbon })
        {
            var vm = NewLibrary(theme);
            vm.SetControllerStatus(true, null);
            Assert.Contains(vm.BuildPaletteItems(), i => i.Title == "Controller mode (F11)");

            vm.ControllerModeKeyCommand.Execute(null);
            Assert.True(vm.IsControllerMode, theme.ToString());
            Assert.Contains(vm.BuildPaletteItems(), i => i.Title == "Leave controller mode (F11)");

            vm.ControllerModeKeyCommand.Execute(null);
            Assert.False(vm.IsControllerMode, theme.ToString());
            Assert.Equal(theme, vm.AppearanceTheme);                                                   // the look it had comes back
        }

        await Task.CompletedTask;
    });

    [Fact]
    public void TheInstalledTwiceFilter_IsInThePlayStationFilters_WhenThereAreDuplicates() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary(ThemeId.ConsoleRibbon);
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            window.Shell.ShowLibraryCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.Equal(vm.HasDuplicates, window.RibbonView.FilterDuplicates.IsVisible);              // there when there are duplicates, like Axis's sidebar row
            Assert.Equal("duplicates", window.RibbonView.FilterDuplicates.CommandParameter);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }

        await Task.CompletedTask;
    });
}
