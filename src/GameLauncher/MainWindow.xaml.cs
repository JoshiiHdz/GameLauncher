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
                    MemoryTrimmer.Trim();
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

        if (!vm.MinimizeToTrayWhileGaming)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        HideToTray();

        // Only one game session is tracked at a time; launching again supersedes the previous watch.
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;

        bool exited;
        try
        {
            exited = await _sessionWatcher.WaitForExitAsync(game, started, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // If the game's processes were never found, leave the window hidden rather than popping it
        // over a game that is most likely still running - the tray icon is the way back.
        if (exited && !token.IsCancellationRequested)
            RestoreFromTray();
    }

    private void HideToTray()
    {
        TrayIcon.Visibility = Visibility.Visible;
        Hide();
        MemoryTrimmer.Trim();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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
