using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>Keyboard shortcuts, command keys and the commands in the palette work the same in every theme: the themes differ in how they look and are driven, not in what
/// they can do.</summary>
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
    public void EveryTheme_HasTheShortcuts_AndNamesTheirKeys() => sta.RunAsync(async () =>
    {
        try
        {
            foreach (var theme in new[] { ThemeId.Axis, ThemeId.ConsoleTile, ThemeId.ConsoleRibbon })
            {
                ThemeManager.Apply(theme);
                Assert.True(ThemeState.Instance.ShortcutsEnabled, theme.ToString());
                Assert.Contains("Ctrl+K", ThemeState.Instance.SearchHint);
                Assert.Contains("Esc", ThemeState.Instance.BackTooltip);
                Assert.Contains("Ctrl+K", ThemeState.Instance.SearchTooltip);
            }
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void ThePalette_OffersTheSameCommandsAndSettings_InEveryTheme() => sta.RunAsync(async () =>
    {
        try
        {
            var vm = NewLibrary();
            var titles = new List<string[]>();
            foreach (var theme in new[] { ThemeId.Axis, ThemeId.ConsoleTile, ThemeId.ConsoleRibbon })
            {
                ThemeManager.Apply(theme);
                var commands = vm.BuildPaletteItems().Where(i => i.Kind == PaletteKind.Command).Select(i => i.Title).OrderBy(t => t, StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(commands);
                titles.Add(commands);
            }

            Assert.Equal(titles[0], titles[1]);
            Assert.Equal(titles[0], titles[2]);
        }
        finally { ThemeManager.Apply(ThemeId.Axis); }

        await Task.CompletedTask;
    });

    [Fact]
    public void InThePlayStationTheme_EscapeGoesBack_LikeInAxis() => sta.RunAsync(async () =>
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
