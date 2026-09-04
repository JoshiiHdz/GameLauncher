using System.Windows;
using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class WhatsNewWindow : FluentWindow
{
    public WhatsNewWindow(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void GotItButton_Click(object sender, RoutedEventArgs e) => Close();
}
