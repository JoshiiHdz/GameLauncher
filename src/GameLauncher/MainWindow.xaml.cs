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

        // Session tracking (and so the "Running" badge) runs regardless of the tray setting -
        // MinimizeToTrayWhileGaming only decides whether the window also hides/restores itself.
        game.IsRunning = true;

        // Only one game session is tracked at a time; launching again supersedes the previous watch.
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;

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
            // Superseded by a newer launch - that one owns the badge/tray state now, this game's
            // own "running" flag still needs clearing since nothing else will do it.
            game.IsRunning = false;
            return;
        }

        if (token.IsCancellationRequested)
            return;

        // If the game's processes were never found, leave the window hidden (and the badge showing)
        // rather than assuming it's closed - it's most likely still running, and the tray icon is
        // the way back regardless.
        if (!exited)
            return;

        game.IsRunning = false;

        if (vm.MinimizeToTrayWhileGaming)
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
    /// </summary>
    private void RestoreFromTray()
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
}
