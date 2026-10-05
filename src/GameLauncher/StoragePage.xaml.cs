using System.Windows;
using System.Windows.Controls;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>"What's using this drive?": the games on one drive ranked by size. All figures come from StorageViewModel; the only action
/// is opening a game's folder, which the library carries out.</summary>
public partial class StoragePage : UserControl
{
    public StoragePage() => InitializeComponent();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StorageRow row } && DataContext is StorageViewModel viewModel)
            viewModel.RequestOpenInstallLocation(row.Game);
    }
}
