using System.Windows;
using GameLauncher.ViewModels;
using Wpf.Ui.Controls;

namespace GameLauncher;

/// <summary>Identify Game / Choose Cover. Every operation is a command on IdentifyGameViewModel, which goes through
/// LibraryViewModel's revision-checked transactions - this window holds no state of its own beyond which tab is showing.</summary>
public partial class IdentifyGameWindow : FluentWindow
{
    private readonly IdentifyGameViewModel _viewModel;

    public IdentifyGameWindow(IdentifyGameViewModel viewModel, bool startOnCovers = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Tabs.SelectedItem = startOnCovers ? CoverTab : IdentifyTab;

        // Every way this window can close (the Close button, the title bar, Alt+F4) stops any in-flight search or cover work.
        Closed += (_, _) => _viewModel.Cancel();

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
