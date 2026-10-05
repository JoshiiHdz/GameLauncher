using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>"Pick a game for me": shows one suggestion. Play marks the view model and closes the page; the library does the launching.</summary>
public partial class PickGamePage : UserControl
{
    public PickGamePage() => InitializeComponent();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PickGameViewModel viewModel)
            viewModel.PlayRequested = true;

        AppShell.RequestClose(this);
    }
}
