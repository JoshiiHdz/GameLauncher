using System.Windows;
using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Identify Game / Choose Cover. Every operation is a command on IdentifyGameViewModel, which goes through
/// LibraryViewModel's revision-checked transactions - this dialog holds no state of its own beyond which tab is showing.</summary>
public partial class IdentifyGameDialog : UserControl
{
    private readonly IdentifyGameViewModel _viewModel;

    public IdentifyGameDialog(IdentifyGameViewModel viewModel, bool startOnCovers = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Tabs.SelectedItem = startOnCovers ? CoverTab : IdentifyTab;

        // Every way this dialog can close (the Close button, Esc, the card's own close button) stops any in-flight search or cover work.
        AddHandler(AppShell.DialogClosedEvent, new RoutedEventHandler((_, _) => _viewModel.Cancel()));

        // A search box that opens filled with the detected title, so the first click on Search is usually all that is needed.
        Loaded += (_, _) =>
        {
            if (!startOnCovers && _viewModel.HasProviders && _viewModel.Candidates.Count == 0
                && !string.IsNullOrWhiteSpace(_viewModel.SearchText) && _viewModel.SearchCommand.CanExecute(null))
            {
                _viewModel.SearchCommand.Execute(null);
            }

            if (startOnCovers && _viewModel.LoadCoversCommand.CanExecute(null))
                _viewModel.LoadCoversCommand.Execute(null);
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this);
}
