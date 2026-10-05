using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The game details page. Play, open-folder and collections hand off to the library through the view model; typed notes are
/// read back by the library after the page closes.</summary>
public partial class GameDetailsPage : UserControl
{
    public GameDetailsPage() => InitializeComponent();

    private GameDetailsViewModel? ViewModel => DataContext as GameDetailsViewModel;

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            viewModel.PlayRequested = true;

        AppShell.RequestClose(this);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestOpenInstallLocation();

    private void Uninstall_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestUninstall();

    private void EditCollections_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestEditCollections();
}
