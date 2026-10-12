using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The quick-view panel for a game (opened by a click on an Axis game card). Play and "Game tweaks" close it and the library carries on from the
/// view model; the other buttons hand off to the library and leave the panel open under whatever they show.</summary>
public partial class GameQuickViewDialog : UserControl
{
    public GameQuickViewDialog(GameDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Play is where the keyboard starts, so Enter on an opened panel starts the game.
        Loaded += (_, _) => PlayButton.Focus();
    }

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

    private void GameTweaks_Click(object sender, RoutedEventArgs e)
    {
        // The tweaks are global (Settings > Performance), which is a page: close the panel first, then the library opens it.
        if (ViewModel is { } viewModel)
            viewModel.TweaksRequested = true;

        AppShell.RequestClose(this);
    }

    private void PlayTime_Click(object sender, RoutedEventArgs e) => ViewModel?.RequestPlayTime();
}
