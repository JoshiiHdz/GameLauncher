using System.IO;
using System.Windows;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The console themes' game menus offer each thing once: a menu leaves out the rows the screen already has as buttons or cards.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ShellMenuTests(WpfStaFixture sta)
{
    private static (ShellState Shell, GameEntry Game) Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Menus-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        var game = new GameEntry { Id = "steam-1", Name = "Elden Ring", Source = GameSource.Steam, ExecutablePath = @"C:\G\er.exe", InstallDir = @"C:\G" };
        vm.SimulateRefreshResult([game]);
        return (new ShellState(vm), game);
    }

    private static List<string> Rows(ShellState shell) => shell.MenuItems.Select(i => i.IsSeparator ? "-" : i.Header).ToList();

    [Fact]
    public void XboxStyle_CardMenu_HasEveryRow_InGroups() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleTile);
        try
        {
            var (shell, game) = Create();
            shell.OpenCardMenu(game, new Rect(0, 0, 100, 100));

            Assert.Equal(
                new[] { "Play", "Game details...", "-", "Favorite", "Hide", "Collections...", "-", "Change Cover Manually", "Reset cover to automatic", "Identify game...", "-", "Open install location", "Uninstall..." },
                Rows(shell));
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void XboxStyle_MoreMenu_LeavesOutPlayAndDetails_WhichTheHeroHasAsButtons() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleTile);
        try
        {
            var (shell, game) = Create();
            shell.OpenMoreMenu(game, new Rect(0, 0, 48, 48));

            var rows = Rows(shell);
            Assert.DoesNotContain("Play", rows);
            Assert.DoesNotContain("Game details...", rows);
            Assert.Contains("Open install location", rows); // the Xbox hero has no button for it
            Assert.Contains("Favorite", rows);
            Assert.Contains("Collections...", rows);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void PlayStationStyle_HomeMenus_LeaveOutWhatTheHomeAlreadyShows() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (shell, game) = Create();

            shell.OpenCardMenu(game, new Rect(0, 0, 100, 100));
            var card = Rows(shell);
            Assert.Equal(new[] { "Play", "-", "Hide", "-", "Change Cover Manually", "Reset cover to automatic", "Identify game...", "-", "Open install location" }, card);
            shell.CloseMenu();

            shell.OpenMoreMenu(game, new Rect(0, 0, 48, 48));
            var more = Rows(shell);
            Assert.Equal(new[] { "Hide", "-", "Change Cover Manually", "Reset cover to automatic", "Identify game...", "-", "Open install location" }, more);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void PlayStationStyle_GameDetailsMoreMenu_KeepsFavoriteAndUninstall_BecauseThatPageHasNoButtonsForThem() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (shell, game) = Create();
            shell.OpenMoreMenu(game, new Rect(0, 0, 48, 48), onDetailsPage: true);

            var rows = Rows(shell);
            Assert.Contains("Favorite", rows);
            Assert.Contains("Uninstall...", rows);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void PlayStationStyle_LibraryTabCardMenu_StillOffersGameDetails() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (shell, game) = Create();
            shell.ShowLibraryCommand.Execute(null);
            shell.OpenCardMenu(game, new Rect(0, 0, 100, 100));

            var rows = Rows(shell);
            Assert.Contains("Game details...", rows);
            Assert.Contains("Open install location", rows);
            Assert.Contains("Collections...", rows);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void PlayStationStyle_PickingAGameOnTheHome_SendsTheFocusToPlay_ButInTheLibraryTabOpensDetails() => sta.RunAsync(async () =>
    {
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        try
        {
            var (shell, game) = Create();
            var playRequests = 0;
            shell.PlayFocusRequested += (_, _) => playRequests++;
            shell.FocusRibbonItem(game);

            shell.OpenFocused();
            Assert.Equal(1, playRequests);

            shell.ShowLibraryCommand.Execute(null);
            shell.OpenFocused();
            Assert.Equal(1, playRequests); // the Library tab has no details panel: it opened Game details instead
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });
}

/// <summary>The Xbox style is kept in the code but not offered: it is not in the picker, cannot be chosen, and a saved choice of it opens as Axis.</summary>
public sealed class ThemeAvailabilityTests
{
    [Fact]
    public void TheXboxStyle_IsNotAvailable_SoASavedChoiceOpensAsAxis()
    {
        Assert.False(ThemeManager.IsAvailable(ThemeId.ConsoleTile));
        Assert.True(ThemeManager.IsAvailable(ThemeId.Axis));
        Assert.True(ThemeManager.IsAvailable(ThemeId.ConsoleRibbon));
        Assert.Equal(ThemeId.Axis, ThemeManager.Parse("ConsoleTile"));
        Assert.Equal(ThemeId.ConsoleRibbon, ThemeManager.Parse("ConsoleRibbon"));
    }

    [Fact]
    public void ChoosingTheXboxStyle_ByCommand_ChangesNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Avail-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory));

        vm.ChangeThemeCommand.Execute("ConsoleTile");
        Assert.Equal(ThemeId.Axis, vm.AppearanceTheme);

        vm.ChangeThemeCommand.Execute("ConsoleRibbon");
        Assert.Equal(ThemeId.ConsoleRibbon, vm.AppearanceTheme);
    }
}
