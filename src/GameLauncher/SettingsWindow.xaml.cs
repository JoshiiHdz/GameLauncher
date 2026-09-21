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

    // The secret is saved when the box loses focus (not on every keystroke) and the box is then cleared: it is never read back
    // into the UI, and "a client secret is saved" is the only thing shown afterwards.
    private void IgdbSecretBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel && !string.IsNullOrEmpty(IgdbSecretBox.Password))
        {
            viewModel.SaveIgdbClientSecret(IgdbSecretBox.Password);
            IgdbSecretBox.Password = string.Empty;
        }
    }

    private void RemoveIgdbSecret_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel)
            viewModel.SaveIgdbClientSecret(null);
    }
}
