using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

public partial class ChangeCoverWindow : FluentWindow
{
    public ChangeCoverWindow(ChangeCoverDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Cancel, Apply, the title bar's own close button, Alt+F4 - every way this window can close
        // routes through here exactly once, since both commands raise the same event; see
        // ChangeCoverDialogViewModel's own remarks on why that makes every closing path equivalent.
        viewModel.RequestClose += Close;
    }
}
