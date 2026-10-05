using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Behaviors;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The shared motion behaviours, and the controls the new actions added to the main window. The timing is Windows' real animation
/// clock, so these check where an animation ENDS (and that it started), never frame-by-frame values.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class MotionTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Motion-" + Guid.NewGuid());

    public void Dispose()
    {
        Motion.AnimationsEnabled = () => System.Windows.SystemParameters.ClientAreaAnimation;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            Content = content, Width = 300, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static async Task Until(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    [Fact]
    public void WithAnimationsOff_NothingAnimatesAndEverythingLandsAtOnce() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => false;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 5 };
        var card = new Border { Child = bar };
        Motion.SetFadeIn(card, true);
        Motion.SetAnimatedValue(bar, 0.75);
        var window = Host(card);
        try
        {
            Assert.Equal(0.75, bar.Value);
            Assert.Equal(1.0, card.Opacity);
            Assert.False(card.RenderTransform is TranslateTransform { HasAnimatedProperties: true });
            Assert.Same(Transform.Identity, card.RenderTransform);
        }
        finally { window.Close(); }
        await Task.CompletedTask;
    });

    [Fact]
    public void AProgressBar_EasesToItsValue_AndFollowsLaterChanges() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => true;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 5, Width = 100 };
        Motion.SetAnimatedValue(bar, 0.6);
        var window = Host(bar);
        try
        {
            Assert.True(bar.HasAnimatedProperties, "The bar should be animating towards its value, not jumping.");
            await Until(() => Math.Abs(bar.Value - 0.6) < 0.0001);
            Assert.InRange(bar.Value, 0.5999, 0.6001);

            Motion.SetAnimatedValue(bar, 0.2);
            await Until(() => Math.Abs(bar.Value - 0.2) < 0.0001);
            Assert.InRange(bar.Value, 0.1999, 0.2001);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TurningAnimationsOffMidway_SnapsABarToItsValue() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => true;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 5, Width = 100 };
        Motion.SetAnimatedValue(bar, 0.9);
        var window = Host(bar);
        try
        {
            Motion.AnimationsEnabled = () => false;
            Motion.SetAnimatedValue(bar, 0.4);

            Assert.False(bar.HasAnimatedProperties);
            Assert.Equal(0.4, bar.Value);
        }
        finally { window.Close(); }
        await Task.CompletedTask;
    });

    [Fact]
    public void FadeIn_PlaysWhenTheElementLoads_AndEndsExactlyOnItsRealValues() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => true;
        var card = new Border { Opacity = 0.65, Width = 50, Height = 50, Background = Brushes.Red };
        var started = new List<FrameworkElement>();
        void OnStarted(FrameworkElement e) => started.Add(e);
        Motion.FadeStarted += OnStarted;
        Motion.SetFadeIn(card, true);
        var window = Host(card);
        try
        {
            Assert.Same(card, Assert.Single(started));
            var lift = Assert.IsType<TranslateTransform>(card.RenderTransform);

            await Task.Delay(900); // well past the 260 ms fade

            Assert.Equal(0.65, card.Opacity); // the element's own (deliberately dimmed) opacity, never forced to 1
            Assert.Equal(0, lift.Y);
        }
        finally { Motion.FadeStarted -= OnStarted; window.Close(); }
    });

    [Fact]
    public void FadeIn_NeverReplacesATransformTheElementAlreadyHas() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => true;
        var own = new ScaleTransform(2, 2);
        var card = new Border { Width = 50, Height = 50, RenderTransform = own };
        Motion.SetFadeIn(card, true);
        var window = Host(card);
        try
        {
            Assert.Same(own, card.RenderTransform);
            Assert.Equal(2, own.ScaleX);
        }
        finally { window.Close(); }
        await Task.CompletedTask;
    });

    [Fact]
    public void Replay_PlaysOnAChange_NotOnTheFirstValue_AndNotForTheSameValue() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => true;
        var panel = new Border { Width = 50, Height = 50, RenderTransform = new TranslateTransform() };
        var plays = 0;
        void OnStarted(FrameworkElement e) => plays++;
        Motion.FadeStarted += OnStarted;
        Motion.SetReplay(panel, "first");
        Assert.Equal(0, plays); // not on screen yet: the first value never replays

        var window = Host(panel);
        try
        {
            Assert.Equal(0, plays);

            Motion.SetReplay(panel, "second");
            Assert.Equal(1, plays);

            Motion.SetReplay(panel, "second");
            Assert.Equal(1, plays); // same value: nothing changed, nothing replays

            Motion.SetReplay(panel, "third");
            Assert.Equal(2, plays);
        }
        finally { Motion.FadeStarted -= OnStarted; window.Close(); }
        await Task.CompletedTask;
    });

    // ---- the main window's power / uninstall controls ----------------------------------------------

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    [Fact]
    public void TheSidebarShutDownEntry_AsksBeforeShuttingDown_AndTheFooterOffersCancelWhilePending() => sta.RunAsync(async () =>
    {
        Motion.AnimationsEnabled = () => false;
        var vm = new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory));
        var asked = new List<string>();
        var ran = new List<string>();
        vm.ConfirmShutdownForTest = q => { asked.Add(q); return true; };
        vm.StartShutdownForTest = ran.Add;
        vm.ShutdownCountdownDelay = (_, token) => Task.Delay(Timeout.Infinite, token);
        var window = new MainWindow(vm, startRuntimeServices: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        try
        {
            var power = Descendants<System.Windows.Controls.Button>(window).Single(b => Equals(b.ToolTip, "Shut down this PC (asks first)"));
            var cancel = Descendants<Wpf.Ui.Controls.Button>(window).Single(b => b.Content as string == "Cancel shutdown");
            Assert.Same(vm.ShutdownPcCommand, power.Command);
            Assert.Same(vm.CancelShutdownCommand, cancel.Command);
            Assert.False(cancel.IsVisible);
            Assert.Empty(ran); // nothing happens just by the window existing

            power.Command.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            Assert.Equal(["Shut down your PC?"], asked);
            Assert.True(cancel.IsVisible);

            cancel.Command.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            Assert.Equal("/a", ran.Last());
            Assert.False(cancel.IsVisible);
        }
        finally { window.Close(); }
    });
}
