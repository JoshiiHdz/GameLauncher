using System.Windows.Controls;
using System.Windows.Input;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The Ctrl+K palette. Keyboard first: typing searches, Up/Down move, Enter chooses, Esc (or clicking the dimmed area) closes.
/// The dialog only reports what was chosen; the library runs it once this has closed.</summary>
public partial class CommandPaletteDialog : UserControl
{
    private readonly CommandPaletteViewModel _viewModel;

    /// <summary>The palette's view model, so the controller can move its selection and choose (the keyboard does this through key events).</summary>
    internal CommandPaletteViewModel Model => _viewModel;

    public CommandPaletteDialog(CommandPaletteViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += () => AppShell.RequestClose(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => QueryBox.Focus();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.SelectedIndex) && ResultList.SelectedItem is { } selected)
                ResultList.ScrollIntoView(selected);
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.Choose();
                e.Handled = true;
                break;
        }
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteItem item })
            _viewModel.Choose(item);
    }
}
