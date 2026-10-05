using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Send feedback without leaving the launcher. All behaviour is on FeedbackViewModel; the dialog only closes itself a moment
/// after a successful send.</summary>
public partial class FeedbackDialog : UserControl
{
    public FeedbackDialog(FeedbackViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => MessageInput.Focus();

        viewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(FeedbackViewModel.IsSent) && viewModel.IsSent)
            {
                await Task.Delay(1500);
                if (IsLoaded)
                    AppShell.RequestClose(this);
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this);
}
