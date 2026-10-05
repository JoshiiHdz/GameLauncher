using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Tick the collections one game belongs to, or type a new one. Nothing is saved here: Save marks the dialog saved and
/// the library applies the result.</summary>
public partial class CollectionsDialog : UserControl
{
    private readonly CollectionsViewModel _viewModel;

    public CollectionsDialog(CollectionsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => NewNameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Saved = true;
        AppShell.RequestClose(this);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this);
}
