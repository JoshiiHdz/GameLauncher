using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
