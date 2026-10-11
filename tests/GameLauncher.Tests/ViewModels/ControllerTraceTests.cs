using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>What the launcher learns from the pad's navigation (the trace and its report), and the one-highlight rule: while the pad is in use nothing else lights up.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class ControllerTraceTests(WpfStaFixture sta)
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0);

    private static NavStep Step(double seconds, string button, string from, string to, bool? moved = null, string screen = "Home tab", string rule = "spatial") =>
        new(T0.AddSeconds(seconds), screen, button, from, to, rule, moved ?? from != to);

    [Fact]
    public void ABackAndForth_IsCountedAsTakenBack_AndAWallIsCounted()
    {
        var trace = new NavigationTrace(null);
        trace.Record(Step(0, "Right", "A", "B"));
        trace.Record(Step(1, "Left", "B", "A"));     // straight back: not what was meant
        trace.Record(Step(10, "Right", "A", "B"));
        trace.Record(Step(20, "Left", "B", "A"));    // far too late to be "straight back"
        trace.Record(Step(21, "Left", "A", "A"));    // a wall
        trace.Record(Step(22, "Accept", "A", "A", moved: false)); // a button, not a direction: no wall

        Assert.Equal(6, trace.Steps);
        Assert.Equal(4, trace.Moves);
        Assert.Equal(1, trace.TakenBack);
        Assert.Equal(1, trace.Walls);

        var report = trace.Summary();
        Assert.Contains("1 moves taken straight back", report);
        Assert.Contains("A  --Right-->  B", report);
        Assert.Contains("Home tab: Left from A", report);
    }

    [Fact]
    public void TheLog_IsOneJsonLinePerPress_AndIsCapped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Trace-" + Guid.NewGuid());
        try
        {
            var trace = new NavigationTrace(directory);
            trace.Record(Step(0, "Down", "Tile", "Play", rule: "down-to-play"));
            trace.Record(Step(1, "Up", "Play", "Tile", rule: "up-to-tile"));
            trace.WriteSummary();

            var lines = File.ReadAllLines(Path.Combine(directory, "controller-navigation.jsonl"));
            Assert.Equal(2, lines.Length);
            using (var first = System.Text.Json.JsonDocument.Parse(lines[0]))
            {
                Assert.Equal("Down", first.RootElement.GetProperty("btn").GetString());
                Assert.Equal("down-to-play", first.RootElement.GetProperty("rule").GetString());
                Assert.True(first.RootElement.GetProperty("moved").GetBoolean());
            }

            using (var second = System.Text.Json.JsonDocument.Parse(lines[1]))
                Assert.True(second.RootElement.GetProperty("back").GetBoolean());

            Assert.Contains("1 moves taken straight back", File.ReadAllText(Path.Combine(directory, "controller-navigation-summary.txt")));

            // Past the cap the old log is kept as .1 and a fresh one starts.
            File.WriteAllText(Path.Combine(directory, "controller-navigation.jsonl"), new string('x', (int)NavigationTrace.MaxBytes + 10));
            trace.Record(Step(2, "Down", "Tile", "Play"));
            Assert.True(File.Exists(Path.Combine(directory, "controller-navigation.jsonl.1")));
            Assert.Single(File.ReadAllLines(Path.Combine(directory, "controller-navigation.jsonl")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ALogThatCannotBeWritten_NeverGetsInTheWayOfThePad()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "GameLauncherTests-TraceFile-" + Guid.NewGuid());
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            var trace = new NavigationTrace(Path.Combine(blocker, "logs"));
            trace.Record(Step(0, "Down", "A", "B")); // fails inside, swallowed
            trace.WriteSummary();
            Assert.Equal(1, trace.Steps);
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void TheStick_CountsAPushIntoACornerOnce_NotOnEveryReading()
    {
        var corner = new PadReading(0, 24000, 24000, 0, 0);
        PadReading? reading = corner;
        long now = 0;
        var input = new ControllerInput(i => i == 0 ? reading : null, () => now, () => true);
        input.Poll();
        now += 20; input.Poll();
        now += 20; input.Poll();
        Assert.Equal(1, input.IgnoredDiagonals);
        Assert.Equal((24000, 24000), input.LastStick);

        reading = new PadReading(0, 0, 0, 0, 0);   // let go
        now += 20; input.Poll();
        reading = corner;                          // and into the corner again
        now += 20; input.Poll();
        Assert.Equal(2, input.IgnoredDiagonals);
    }

    // ---- the window ------------------------------------------------------------------------------------------------------------

    private static LibraryViewModel NewLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-TraceWindow-" + Guid.NewGuid());
        var vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory)) { InstallSizeEstimatorForTest = (_, _) => null };
        vm.SimulateRefreshResult(
        [
            new GameEntry { Id = "1", Name = "Apex", Source = GameSource.Steam, ExecutablePath = @"C:\G\a.exe", InstallDir = @"C:\G\a" },
            new GameEntry { Id = "2", Name = "Borderlands", Source = GameSource.Epic, ExecutablePath = @"C:\G\b.exe", InstallDir = @"C:\G\b" },
        ]);
        vm.SearchText = "x";
        vm.SearchText = "";
        vm.AppearanceTheme = ThemeId.ConsoleRibbon;
        vm.SetControllerStatus(true, null);
        return vm;
    }

    [Fact]
    public void ThePadsPresses_AreTraced_WithTheRuleThatDecidedThem() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            var before = window.NavTrace.Steps;

            window.PadForTest.PressForTest(PadButton.Down); // from the tile to Play
            await ShellTestSupport.SettleAsync();
            await ShellTestSupport.SettleAsync();

            Assert.Equal(before + 1, window.NavTrace.Steps);
            Assert.Contains("Play", window.DescribeControl(Keyboard.FocusedElement));
            Assert.True(window.NavTrace.Moves > 0);
            Assert.Contains("down-to-play", window.NavTrace.Summary());
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void WhileThePadIsInUse_ThePointerCannotLightAnything_AndOneHighlightMarksTheControl() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            window.NotePadUse();
            Assert.False(window.RootGrid.IsHitTestVisible); // no control can be "hovered" under a pointer nobody can see

            // A PlayStation button draws its own ring on focus: the pad's overlay stays off it, so there is one outline, not two.
            var play = window.RibbonView.HeroPlay;
            Assert.True(MainWindow.HasOwnFocusRing(play));
            Keyboard.Focus(play);
            await ShellTestSupport.SettleAsync();
            window.NotePadUse();
            Assert.Equal(Visibility.Collapsed, window.FocusRing.Visibility);

            // On every Settings control the pad is marked exactly once: by the control's own ring if it has one, else by the overlay.
            vm.ShowSettingsCommand.Execute(null);
            await ShellTestSupport.SettleAsync();
            window.UpdateLayout();
            var marked = 0;
            foreach (var control in window.PadControls().Take(8))
            {
                Keyboard.Focus(control);
                await ShellTestSupport.SettleAsync();
                window.NotePadUse();
                var overlay = window.FocusRing.Visibility == Visibility.Visible;
                Assert.Equal(!MainWindow.HasOwnFocusRing(control), overlay);
                marked++;
            }

            Assert.True(marked > 0);

            window.NotePointerAt(new Point(10, 10));
            window.NotePointerAt(new Point(200, 120));      // a real move: the mouse is back, the ring steps aside
            Assert.True(window.RootGrid.IsHitTestVisible);
            Assert.Equal(Visibility.Collapsed, window.FocusRing.Visibility);

            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.True(window.RootGrid.IsHitTestVisible);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void AControlsOwnFocusLook_IsSwitchedOffWhileThePadIsInUse_AndComesBack() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();

            var plain = new System.Windows.Controls.Button(); // a stock control: it has the system's dotted focus look
            Assert.NotNull(plain.FocusVisualStyle);
            window.QuietOwnFocusVisual(vm, plain);
            Assert.Null(plain.FocusVisualStyle);

            vm.ExitControllerMode();
            await ShellTestSupport.SettleAsync();
            Assert.NotNull(plain.FocusVisualStyle);
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });

    [Fact]
    public void TheTopBarIcons_SwellWhileThePadIsOnThem_AndAreAlwaysMarked_EvenWhenAHoverLeftTheirOwnRingAtZero() => sta.RunAsync(async () =>
    {
        GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
        ThemeManager.Apply(ThemeId.ConsoleRibbon);
        var vm = NewLibrary();
        var window = ShellTestSupport.OpenMain(vm);
        try
        {
            window.Activate();
            await ShellTestSupport.SettleAsync();
            vm.EnterControllerMode();
            await ShellTestSupport.SettleAsync();
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => true;

            var search = window.RibbonView.SearchButton;
            var settings = window.RibbonView.SettingsButton;
            var before = GameLauncher.Behaviors.PadPulse.Total;
            Keyboard.Focus(search);
            await ShellTestSupport.SettleAsync();
            window.NotePadUse();
            Assert.True(GameLauncher.Behaviors.PadPulse.Total > before);                       // it swells...
            Assert.True(Assert.IsType<System.Windows.Media.ScaleTransform>(search.RenderTransform).HasAnimatedProperties);
            Assert.True(MainWindow.OwnRingIsLit(search));                                      // ...and its own ring is showing, so the overlay stays off
            Assert.Equal(Visibility.Collapsed, window.FocusRing.Visibility);

            Keyboard.Focus(settings);                                                          // the pad moves on: the first lets go
            await ShellTestSupport.SettleAsync();
            Assert.True(Assert.IsType<System.Windows.Media.ScaleTransform>(search.RenderTransform).HasAnimatedProperties);

            // A mouse hover that left the ring's opacity held at zero must not leave the pad with nothing to show.
            var ring = (FrameworkElement)settings.Template.FindName("Ring", settings);
            ring.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.Zero));
            Keyboard.Focus(search);
            await ShellTestSupport.SettleAsync();
            Keyboard.Focus(settings);
            await ShellTestSupport.SettleAsync();
            window.NotePadUse();
            Assert.False(MainWindow.OwnRingIsLit(settings));
            Assert.Equal(Visibility.Visible, window.FocusRing.Visibility);                     // the overlay marks it instead
        }
        finally
        {
            GameLauncher.Behaviors.Motion.AnimationsEnabled = () => false;
            window.Close();
            ThemeManager.Apply(ThemeId.Axis);
        }
    });
}
