using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

public partial class WhatsNewDialog : UserControl
{
    public WhatsNewDialog(LibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void GotItButton_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this);
}
