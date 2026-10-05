using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

public partial class ChangeCoverDialog : UserControl
{
    public ChangeCoverDialog(ChangeCoverDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Cancel and Apply both raise this one event, and Esc or the card's close button simply leave Applied false, so every
        // way this dialog can close is equivalent to Cancel unless Apply ran first; see ChangeCoverDialogViewModel's own remarks.
        viewModel.RequestClose += () => AppShell.RequestClose(this);
    }
}
