using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class MainWindow : FluentWindow
{
    private readonly GameSessionWatcher _sessionWatcher = new();
    private CancellationTokenSource? _sessionCts;
    private GameEntry? _watchedGame;
    private int _watchedSessionId;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (DataContext is not LibraryViewModel vm)
                return;

            TrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);

            vm.GameLaunched += OnGameLaunched;
            vm.VibrantBackgroundChanged += ApplyBackdrop;
            ApplyBackdrop(vm.VibrantBackground);

            // The sidebar's real content (how many source rows, how many drives) isn't known until
            // the first scan finishes - growing the window here, and again after every rescan, means
            // a PC with a lot of drives/launchers opens tall enough to show them without the user
            // ever having to drag the window bigger by hand.
            vm.LibraryRefreshed += () => FitWindowToSidebar();
            vm.PropertyChanged += Vm_PropertyChanged;

            // Covers both launching a game and minimizing by hand.
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    MemoryTrimmer.Trim("window minimized");
            };

            await vm.RefreshCommand.ExecuteAsync(null);
        };

        Closed += (_, _) =>
        {
            _sessionCts?.Cancel();
            TrayIcon.Dispose();
        };
    }

    private async void OnGameLaunched(GameEntry game, Process? started)
    {
        if (DataContext is not LibraryViewModel vm)
            return;

        // Session tracking (and so the "Running" badge and LibraryViewModel's update guard) runs
        // regardless of the tray setting - MinimizeToTrayWhileGaming only decides whether the window
        // also hides/restores itself. Routed through the view model rather than setting
        // GameEntry.IsRunning directly, so its running-game tracking (which survives a rescan
        // replacing every GameEntry) can never drift out of sync with the badge. The returned session
        // id is what makes the cleanup call below safe regardless of call order: relaunching this
        // exact game after a refresh means the superseded entry and the new one share the same game
        // id, so only a session id - not the game id - can tell "the session being cleaned up" apart
        // from "the session that just replaced it."
        var previousSessionId = _watchedSessionId;
        _watchedSessionId = vm.MarkGameRunning(game);

        // Only one game session is tracked at a time; launching again supersedes the previous watch.
        // Clear the superseded game's badge right here, rather than trusting the cancelled watch to
        // do it on its way out - WaitForExitAsync swallows OperationCanceledException internally on
        // every await path and returns a plain bool instead, so the code below can't reliably tell
        // "cancelled" apart from "exited" for the game that just got superseded.
        _sessionCts?.Cancel();
        if (_watchedGame is { } previouslyWatched && previouslyWatched != game)
            vm.MarkGameNotRunning(previouslyWatched, previousSessionId);
        _watchedGame = game;

        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;

        // Captured locally rather than read from _watchedSessionId after the await below: a newer
        // launch can overwrite that field (and _watchedGame) before this session's watch resolves,
        // and the cleanup call at the end of this method must always identify *this* session, not
        // whatever the field currently holds.
        var sessionId = _watchedSessionId;

        if (vm.MinimizeToTrayWhileGaming)
        {
            Logger.Info($"Hiding to tray while '{game.Name}' runs.");
            HideToTray();
        }
        else
        {
            WindowState = WindowState.Minimized;
        }

        bool exited;
        try
        {
            exited = await _sessionWatcher.WaitForExitAsync(game, started, token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer launch - that launch already cleared this game's badge above.
            return;
        }

        if (token.IsCancellationRequested)
            return;

        // If the game's processes were never found, leave the window hidden (and the badge showing)
        // rather than assuming it's closed - it's most likely still running, and the tray icon is
        // the way back regardless.
        if (!exited)
            return;

        vm.MarkGameNotRunning(game, sessionId);

        // Restore regardless of which way the window was put away - RestoreFromTray() handles a
        // plain minimized window fine too (Show()/WindowState=Normal/Activate all still apply, and
        // collapsing an already-collapsed tray icon is a no-op). Previously this only ran when
        // MinimizeToTrayWhileGaming was on, so with that setting off the window stayed minimized on
        // the taskbar forever after the game closed - IsRunning cleared correctly, but nothing ever
        // brought the window back, which read as "it doesn't reopen when the game closes."
        RestoreFromTray();
    }

    private void HideToTray()
    {
        TrayIcon.Visibility = Visibility.Visible;
        Hide();
        MemoryTrimmer.Trim("hidden to tray");
    }

    /// <summary>
    /// Windows enforces a well-documented restriction (the foreground-lock): a background process
    /// cannot forcibly steal focus from whatever currently owns it. After sitting hidden in the tray
    /// while a game owned focus, a plain Activate() call can silently do nothing - no exception, no
    /// log signal, the window just stays exactly where it was. This is the standard failure mode for
    /// tray-icon apps specifically, and the Topmost-toggle below is the standard, widely-used
    /// workaround: forcing the window topmost and immediately releasing it makes Windows actually
    /// bring it to the front, where Activate() alone could not.
    ///
    /// Also called by App.OnStartup's single-instance listener: a second launch attempt (double-
    /// clicking the exe/shortcut again) signals this instance instead of opening a duplicate window,
    /// and lands here whether this window was hidden to tray, minimized, or just sitting behind
    /// other windows - every one of those needs the same "get to the front" handling.
    /// </summary>
    public void RestoreFromTray()
    {
        Logger.Info("Restoring window from tray.");
        Show();
        WindowState = WindowState.Normal;
        Activate();

        Topmost = true;
        Topmost = false;
        Focus();

        TrayIcon.Visibility = Visibility.Collapsed;
    }

    private void TrayIcon_Restore(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _sessionCts?.Cancel();
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Applies the backdrop via WindowBackdrop rather than the FluentWindow.WindowBackdropType
    /// property: setting that property after the window has loaded makes WPF-UI re-run
    /// SetWindowChrome(), which throws on the already-attached chrome Freezable and kills the app.
    /// Wrapped defensively - a cosmetic effect must never be able to take the launcher down.
    /// </summary>
    private void ApplyBackdrop(bool vibrant)
    {
        try
        {
            if (vibrant)
                WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic);
            else
                WindowBackdrop.RemoveBackdrop(this);
        }
        catch (Exception ex)
        {
            Logger.Warn("Couldn't apply the window backdrop; continuing without it.", ex);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm)
            return;

        var settingsWindow = new SettingsWindow(vm) { Owner = this };
        settingsWindow.ShowDialog();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-expanding the sidebar can reveal content (source rows, drives) that was hidden while
        // collapsed and never factored into the window's height - re-check then too, not just after
        // a scan.
        if (e.PropertyName == nameof(LibraryViewModel.IsSidebarExpanded)
            && DataContext is LibraryViewModel { IsSidebarExpanded: true })
        {
            FitWindowToSidebar();
        }
    }

    /// <summary>
    /// Grows the window (never shrinks it - never fights a size the user chose on purpose) so the
    /// sidebar's actual content fits without needing its own scrollbar. The sidebar's inner content
    /// is measured directly with Measure(..., PositiveInfinity) rather than read off ActualHeight,
    /// since ActualHeight only reflects whatever space the Border/ScrollViewer were already given -
    /// exactly the constrained number that hides the "your window is too small" problem this exists
    /// to fix. MainWindow.xaml's sidebar ScrollViewer is the fallback for whenever this still isn't
    /// enough (a monitor too short to fit everything even at the work-area cap below).
    /// </summary>
    private void FitWindowToSidebar()
    {
        if (DataContext is not LibraryViewModel { IsSidebarExpanded: true } || WindowState != WindowState.Normal)
            return;

        UpdateLayout();

        var probeWidth = SidebarBorder.ActualWidth > 0 ? SidebarBorder.ActualWidth : 200;
        SidebarChromeTop.Measure(new Size(probeWidth, double.PositiveInfinity));
        SidebarScrollableContent.Measure(new Size(probeWidth, double.PositiveInfinity));

        const double sidebarTopMargin = 12; // the DockPanel's own Margin="0,12,0,0"
        const double bottomBreathingRoom = 16;
        var neededSidebarHeight = sidebarTopMargin + SidebarChromeTop.DesiredSize.Height
            + SidebarScrollableContent.DesiredSize.Height + bottomBreathingRoom;

        var neededGridHeight = AppTitleBar.ActualHeight + neededSidebarHeight + MainStatusBar.ActualHeight;
        var windowChrome = ActualHeight - RootGrid.ActualHeight; // non-client chrome, if any (normally ~0)
        var neededWindowHeight = neededGridHeight + windowChrome;

        var maxHeight = SystemParameters.WorkArea.Height - 40;
        neededWindowHeight = Math.Min(neededWindowHeight, maxHeight);

        if (neededWindowHeight > Height)
            Height = neededWindowHeight;
    }
}
