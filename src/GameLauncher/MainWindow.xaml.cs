using System.Windows;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not LibraryViewModel vm)
                return;

            vm.GameLaunched += () => WindowState = WindowState.Minimized;
            vm.VibrantBackgroundChanged += ApplyBackdrop;
            ApplyBackdrop(vm.VibrantBackground);

            await vm.RefreshCommand.ExecuteAsync(null);
        };
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
