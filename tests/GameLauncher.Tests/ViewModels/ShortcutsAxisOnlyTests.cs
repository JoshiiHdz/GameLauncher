using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Keyboard shortcuts, command keys and the commands in the palette are an Axis feature. The PlayStation theme has none of them (and names no keys on screen);
/// Axis keeps every one.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ShortcutsAxisOnlyTests(WpfStaFixture sta)
{
    private static LibraryViewModel NewLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Shortcuts-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult([new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G" }]);
        return vm;
    }

    private static KeyEventArgs Press(UIElement element, Key key, RoutedEvent routed)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key) { RoutedEvent = routed };
        element.RaiseEvent(args);
        return args;
    }

    [Fact]
    public void OnlyAxisHasShortcuts_AndOnlyAxisNamesKeysOnScreen() => sta.RunAsync(async () =>
    {
        try
        {
            ThemeManager.Apply(ThemeId.Axis);
            Assert.True(ThemeState.Instance.ShortcutsEnabled);
            Assert.Contains("Ctrl+K", ThemeState.Instance.SearchHint);
            Assert.Contains("Esc", ThemeState.Instance.BackTooltip);

            ThemeManager.Apply(ThemeId.ConsoleRibbon);
            Assert.False(ThemeState.Instance.ShortcutsEnabled);
            Assert.DoesNotContain("Ctrl", ThemeState.Instance.SearchHint);
            Assert.DoesNotContain("Esc", ThemeState.Instance.BackTooltip);
            Assert.DoesNotContain("Esc", ThemeState.Instance.CloseTooltip);
            Assert.DoesNotContain("Esc", ThemeState.Instance.MenuCloseTooltip);
            Assert.DoesNotContain("Ctrl", ThemeState.Instance.SearchTooltip);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void ThePalette_OffersCommandsAndSettings_InAxis_ButOnlyGames_InThePlayStationTheme() => sta.RunAsync(async () =>
    {
        try
        {
            var vm = NewLibrary();

            ThemeManager.Apply(ThemeId.Axis);
            Assert.Contains(vm.BuildPaletteItems(), i => i.Kind == PaletteKind.Command);

            ThemeManager.Apply(ThemeId.ConsoleRibbon);
            var items = vm.BuildPaletteItems();
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.Equal(PaletteKind.Game, i.Kind));
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void InThePlayStationTheme_EscapeAndTheShortcutKeysDoNothing() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            vm.ShowStatsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsPageOpen);

            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await ShellTestSupport.SettleAsync();

            Assert.True(vm.IsPageOpen);                                              // Esc is not a way back here: the back button is

            vm.ClosePageCommand.Execute(null);                                       // the back button's command still works
            Assert.False(vm.IsPageOpen);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void InAxis_EscapeStillGoesBack() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.Axis);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            await ShellTestSupport.SettleAsync();
            vm.ShowStatsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            Assert.True(vm.IsPageOpen);

            Press(window, Key.Escape, Keyboard.KeyDownEvent);
            await ShellTestSupport.SettleAsync();

            Assert.False(vm.IsPageOpen);
        }
        finally { window.Close(); }
    });
}
