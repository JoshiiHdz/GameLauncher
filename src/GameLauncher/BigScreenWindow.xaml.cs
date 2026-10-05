using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Big-screen mode: full screen, driven by a controller (polled only while this window is open) or the keyboard. It only records
/// what was chosen; the library launches it after this has closed.</summary>
public partial class BigScreenWindow : Window
{
    private readonly BigScreenViewModel _viewModel;
    private readonly GamepadService _gamepad;
    private bool _closing;

    public BigScreenWindow(BigScreenViewModel viewModel, GamepadService? gamepad = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _gamepad = gamepad ?? new GamepadService();
        DataContext = viewModel;

        Closing += (_, _) => _closing = true;
        viewModel.CloseRequested += CloseOnce;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BigScreenViewModel.SelectedIndex) or nameof(BigScreenViewModel.ShelfIndex))
                Dispatcher.BeginInvoke(CenterSelected, DispatcherPriority.Loaded);
        };

        PreviewKeyDown += OnPreviewKeyDown;
        _gamepad.ButtonPressed += _viewModel.Handle;
        Loaded += (_, _) =>
        {
            _gamepad.Start(Dispatcher);
            CenterSelected();
            Focus();
        };
        Closed += (_, _) => _gamepad.Dispose();
    }

    private void CloseOnce()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _viewModel.Move(-1); break;
            case Key.Right: _viewModel.Move(1); break;
            case Key.Up:
            case Key.PageUp: _viewModel.MoveShelf(-1); break;
            case Key.Down:
            case Key.PageDown: _viewModel.MoveShelf(1); break;
            case Key.Enter:
            case Key.Space: _viewModel.Accept(); break;
            case Key.F: _viewModel.ToggleFavorite(); break;
            case Key.Escape:
            case Key.Back: _viewModel.Back(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void Carousel_DoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.Accept();

    /// <summary>Keeps the highlighted tile in the middle of the screen as it moves along the shelf.</summary>
    private void CenterSelected()
    {
        if (Carousel.SelectedItem is null || Carousel.ItemContainerGenerator.ContainerFromItem(Carousel.SelectedItem) is not FrameworkElement tile)
            return;

        if (FindScrollViewer(Carousel) is not { } viewer)
            return;

        // The tile's MIDDLE, not its left edge: the highlighted tile is drawn larger (a scale around its centre), which moves its left
        // edge but not its middle - centring by the left edge left the tile off-centre by half the enlargement, more or less depending on
        // how far the enlarging had got.
        var middle = tile.TranslatePoint(new Point(tile.ActualWidth / 2, 0), viewer).X + viewer.HorizontalOffset;
        viewer.ScrollToHorizontalOffset(Math.Max(0, middle - viewer.ViewportWidth / 2));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }
}
