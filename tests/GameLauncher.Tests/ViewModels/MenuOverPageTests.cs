using System.IO;
using System.Windows;
using System.Windows.Controls;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>A menu opened from a page (the Game details page's three dots) must be on top of the page, not behind it.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class MenuOverPageTests(WpfStaFixture sta)
{
    [Fact]
    public void TheMoreMenuOfTheGameDetailsPage_IsRaisedAboveThePage_AndEverythingIsPutBackWhenItCloses() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-MenuPage-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" }]);
        vm.SearchText = "x";
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            var game = vm.Games.First();
            vm.ShowGameDetailsCommand.Execute(game);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsPageOpen);
            Assert.Equal(0, Panel.GetZIndex(window.ConsoleHost));

            window.Shell.OpenMoreMenu(game, new Rect(200, 200, 40, 40), onDetailsPage: true);
            await ShellTestSupport.SettleAsync();

            Assert.True(window.Shell.IsMenuOpen);
            Assert.True(Panel.GetZIndex(window.ConsoleHost) > Panel.GetZIndex(window.PageHost), "the menu's shell must be above the page");
            var contentRoot = window.RibbonView.ContentRoot;
            Assert.NotNull(contentRoot.OpacityMask);                     // the rest of the shell is transparent: the page is what shows
            Assert.True(window.RibbonView.MenuLayer.IsVisible);

            window.Shell.CloseMenu();
            await ShellTestSupport.SettleAsync();

            Assert.Equal(0, Panel.GetZIndex(window.ConsoleHost));
            Assert.Null(contentRoot.OpacityMask);
            Assert.NotNull(window.RibbonView.Shell.Background);          // the shell's own background is back
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
