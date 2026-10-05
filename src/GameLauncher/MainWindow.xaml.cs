using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class MainWindow : FluentWindow
{
    private readonly GameSessionWatcher _sessionWatcher = new();

    // Reads DataContext fresh on every call (via WindowExitDiagnosticsEnabled below) rather than
    // capturing `vm` once, so replacing the context cannot leave diagnostics using stale settings.
    private readonly GameSessionOrchestrator _sessionOrchestrator;

    private CancellationTokenSource? _sessionCts;
    private GameEntry? _watchedGame;
    private int _watchedSessionId;
    private readonly CancellationTokenSource _externalMonitorCts = new();
    private bool _runtimeStarted;

    private bool WindowExitDiagnosticsEnabled() => (DataContext as LibraryViewModel)?.EnableWindowExitDiagnostics ?? false;

    public MainWindow() : this(new LibraryViewModel(), startRuntimeServices: true)
    {
    }

    // Hosts the actual markup with isolated settings in WPF tests, without scanning the machine,
    // starting network lookups, or configuring the real tray icon merely to test a sidebar.
    internal MainWindow(LibraryViewModel viewModel, bool startRuntimeServices, bool? openPerSetting = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        InitializeShell();
        _sessionOrchestrator = new GameSessionOrchestrator(_sessionWatcher, WindowExitDiagnosticsEnabled);
        SizeToDisplay();

        // Opens maximized unless the user turned that off. Tests build the window with runtime services off, and keep the size they set
        // (unless a test asks for the real behaviour with openPerSetting).
        if ((openPerSetting ?? startRuntimeServices) && viewModel.StartMaximized)
            WindowState = WindowState.Maximized;

        Loaded += async (_, _) =>
        {
            if (!startRuntimeServices || _runtimeStarted)
                return;

            if (DataContext is not LibraryViewModel vm)
                return;

            _runtimeStarted = true;

            TrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);

            vm.GameLaunched += OnGameLaunched;
            _ = vm.MonitorExternalGamesAsync(_externalMonitorCts.Token);
            vm.VibrantBackgroundChanged += ApplyBackdrop;
            ApplyBackdrop(vm.VibrantBackground);

            UpdateGlobalHotkey(vm);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryViewModel.GlobalHotkeyEnabled))
                    UpdateGlobalHotkey(vm);
            };

            // Covers both launching a game and minimizing by hand.
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    MemoryTrimmer.Trim("window minimized");
            };

            await vm.RefreshCommand.ExecuteAsync(null);

            // ShowWhatsNew is only ever true on the one launch right after DownloadUpdateAsync applied
            // an update and restarted the app (see PendingUpdateNotesService) - shown after the scan,
            // not before, so it appears in front of the user's actual library rather than the empty
            // state.
            if (vm.ShowWhatsNew)
            {
                AppShell.ShowModal("What's New", new WhatsNewDialog(vm));

                // Only reached once the card has actually closed, regardless of how (the "Got it"
                // button, its close button, Esc, ...) - the marker is only deleted here,
                // never at read time, so the notes survive to try again on a later launch if the app
                // never gets this far (a crash, a forced shutdown, a scan that hangs).
                vm.AcknowledgeWhatsNew();
            }
        };

        Closed += (_, _) =>
        {
            _hotkey?.Dispose();
            _externalMonitorCts.Cancel();
            viewModel.StopPassiveTracking();
            if (_watchedGame is { } game) viewModel.MarkGameNotRunning(game, _watchedSessionId);
            _sessionCts?.Cancel();
            TrayIcon.Dispose();
        };
    }

    private GlobalHotkeyService? _hotkey;

    /// <summary>Registers (or gives back) Ctrl+Alt+Space to match the setting. If another program already has the combination the
    /// launcher simply runs without a hotkey - Ctrl+K inside the window still works.</summary>
    private void UpdateGlobalHotkey(LibraryViewModel vm)
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (!vm.GlobalHotkeyEnabled)
            return;

        _hotkey = GlobalHotkeyService.TryRegister(this, Key.Space, ModifierKeys.Control | ModifierKeys.Alt);
        if (_hotkey is not null)
        {
            _hotkey.Pressed += () => Dispatcher.BeginInvoke(() =>
            {
                RestoreFromTray();
                vm.ShowCommandPaletteCommand.Execute(null);
            });
        }
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
        //
        // Called unconditionally whenever a previous session existed - deliberately NOT skipped when
        // `previouslyWatched` happens to be the exact same GameEntry instance being relaunched (a real,
        // confirmed leak in an earlier version of this method: skipping the call meant the superseded
        // session's own entry in LibraryViewModel's internal session-tracking map was never retired,
        // since neither this call nor the cancelled watch's own OperationCanceledException path below
        // ever reached MarkGameNotRunning for it). MarkGameNotRunning's own session-ownership check
        // (comparing sessionId against whichever session is CURRENTLY canonical - already the new one,
        // since MarkGameRunning above already ran) is what correctly leaves the badge alone for a same-
        // instance relaunch while still retiring the stale session entry - the same mechanism that
        // already protects the ordinary "different game" case, just no longer bypassed here.
        _sessionCts?.Cancel();
        if (_watchedGame is { } previouslyWatched)
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
            exited = await _sessionOrchestrator.WaitForExitAsync(sessionId, game, started, token);
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

    /// <summary>
    /// Opens at a size proportional to the display rather than a fixed 1180x760, which was cramped on a
    /// large monitor and close to full-screen on a small laptop. Runs in the constructor, before the
    /// window is shown, so WindowStartupLocation="CenterScreen" centres the size actually used.
    ///
    /// Measured against the WORK AREA, not the full screen, so the taskbar never ends up underneath the
    /// window. The upper caps matter on ultrawides: a straight percentage there would stretch the
    /// library into one enormous row of cards, which is worse than simply being wide enough.
    /// </summary>
    private void SizeToDisplay()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
            return; // no usable work area reported - keep the XAML defaults

        const double widthFraction = 0.80;
        const double heightFraction = 0.86;
        const double maxWidth = 1800;
        const double maxHeight = 1200;

        Width = Math.Clamp(work.Width * widthFraction, MinWidth, Math.Min(maxWidth, work.Width));
        Height = Math.Clamp(work.Height * heightFraction, MinHeight, Math.Min(maxHeight, work.Height));
    }

    /// <summary>Forwards the wheel from the horizontal "Recently played" strip up to the page beneath it.
    /// A nested ScrollViewer marks MouseWheel handled even when it has no room to scroll in that
    /// direction, so without this the page stops scrolling the moment the pointer is over that row -
    /// which, since the row is full of cards, reads as "scrolling breaks over a card".</summary>
    private void HorizontalStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not UIElement strip || e.Handled)
            return;

        e.Handled = true;
        var bubbled = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = strip,
        };

        // Raised on the PARENT, not on the strip: raising it here would come straight back to this
        // handler and the wheel would go nowhere.
        (VisualTreeHelper.GetParent(strip) as UIElement)?.RaiseEvent(bubbled);
    }

    protected override void SetWindowChrome()
    {
        base.SetWindowChrome();
        if (WindowChrome.GetWindowChrome(this) is not { } chrome) return;
        // Keep FluentWindow's resize border/backdrop settings, but restore a real non-client caption.
        // DragMove alone cannot provide Windows' maximized-to-restored drag behavior.
        var captionChrome = (WindowChrome)chrome.Clone();
        captionChrome.CaptionHeight = 46;
        WindowChrome.SetWindowChrome(this, captionChrome);
    }

    // The app still draws the caption buttons; their input is excluded from native caption hit testing.
    /// <summary>The 3-dot button on a drive row opens the row's own context menu (the one a right-click opens).</summary>
    private void DriveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FrameworkElement row, } && row.ContextMenu is { } menu)
        {
            menu.PlacementTarget = row;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        e.Handled = true;
    }

    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CaptionMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
}
