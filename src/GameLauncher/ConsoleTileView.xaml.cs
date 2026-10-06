using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameLauncher.Models;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Console-tile's home and library (see ConsoleTileView.xaml). DataContext is the <see cref="ShellState"/>; this file holds the pointer
/// behaviours the spec gives the tiles: hover moves focus (so the hero and the background follow the pointer), a click opens Game
/// details, right-click opens the card menu beside the tile.</summary>
public partial class ConsoleTileView : UserControl
{
    public ConsoleTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShellState old)
            old.PropertyChanged -= OnStateChanged;

        if (e.NewValue is ShellState current)
            current.PropertyChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellState.MenuKind) && State is { IsMenuOpen: true })
            Dispatcher.BeginInvoke(PositionMenu, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ShellState? State => DataContext as ShellState;

    private static GameEntry? GameOf(object sender) => (sender as FrameworkElement)?.DataContext as GameEntry;

    private static int RowOf(object sender) => sender is DependencyObject d ? Behaviors.ShellFocus.GetRow(d) : 0;

    private void Focus(object sender)
    {
        if (State is { } state && GameOf(sender) is { } game)
        {
            state.FocusedRow = RowOf(sender);
            state.FocusedGame = game;
        }
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        // While the row is scrolling itself under a resting pointer, tiles slide past it; focus waits for the pointer to move on a tile.
        if (State is { IsMenuOpen: false, NavOpen: false } && !Behaviors.EdgeScroll.IsScrolling)
            Focus(sender);
    }

    private void SortChip_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && sender is FrameworkElement chip)
            state.OpenSortMenu(ShellGeometry.RectOf(chip, this));
    }

    private void Tile_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Focus(sender);

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
        {
            Focus(sender);
            state.Library.ShowGameDetailsCommand.Execute(game);
        }
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (State is not { } state || GameOf(sender) is not { } game || sender is not FrameworkElement tile)
            return;

        e.Handled = true;
        Focus(sender);
        state.OpenCardMenu(game, ShellGeometry.RectOf(tile, this));
    }

    private void HeroMore_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && state.HeroGame is { } game && sender is FrameworkElement button)
            state.OpenMoreMenu(game, ShellGeometry.RectOf(button, this));
    }

    private void OpenLaunchers_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state)
            return;

        state.GuideTab = GuideTab.Launchers;
        state.NavOpen = true;
    }

    // A nested horizontal ScrollViewer marks the wheel handled even when it cannot scroll that way, which froze the page when the pointer
    // was over a shelf: hand vertical wheel turns up to the page, and let Shift scroll the shelf sideways.
    private void Shelf_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer shelf)
            return;

        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            shelf.ScrollToHorizontalOffset(shelf.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        e.Handled = true;
        var routed = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender };
        (shelf.Parent as UIElement)?.RaiseEvent(routed);
    }

    // ---- Guide ----------------------------------------------------------------------------------------------------

    private void GuideScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (State is { } state)
            state.NavOpen = false;

        e.Handled = true;
    }

    private void GuideDriveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && (sender as FrameworkElement)?.DataContext is DriveSpaceInfo drive)
            state.OpenDriveMenu(drive, ShellGeometry.RectOf((FrameworkElement)sender, this));
    }

    // ---- Menus ----------------------------------------------------------------------------------------------------

    private void MenuCatcher_MouseDown(object sender, MouseButtonEventArgs e)
    {
        State?.CloseMenu();
        e.Handled = true;
    }

    private void MenuClose_Click(object sender, MouseButtonEventArgs e)
    {
        State?.CloseMenu();
        e.Handled = true;
    }

    private void MenuRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (State is { } state && (sender as FrameworkElement)?.DataContext is ShellMenuItem item)
            state.MenuActiveIndex = state.MenuItems.IndexOf(item);
    }

    private void MenuRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (State is { } state && (sender as FrameworkElement)?.DataContext is ShellMenuItem item)
        {
            e.Handled = true;
            state.RunMenuItem(item);
        }
    }

    /// <summary>Places the open menu: to the right of its tile (or the left if that would run off), or under its button (or above it); always
    /// inside the window by 8 px and at least 40 px from the top.</summary>
    private void PositionMenu()
    {
        if (State is not { IsMenuOpen: true } state)
            return;

        var width = ActualWidth;
        var height = ActualHeight;
        MenuPanel.Measure(new Size(MenuPanel.Width, Math.Max(100, height - 48)));
        var size = MenuPanel.DesiredSize;
        var anchor = state.MenuAnchor;
        double x;
        double y;
        if (state.MenuKind == ShellMenuKind.Card)
        {
            x = anchor.Right + 10;
            y = anchor.Top;
            if (x + size.Width > width - 8)
                x = anchor.Left - 10 - size.Width;
        }
        else
        {
            x = anchor.Left;
            y = anchor.Bottom + 6;
            if (y + size.Height > height - 8)
                y = anchor.Top - 6 - size.Height;
        }

        x = Math.Max(8, Math.Min(x, width - size.Width - 8));
        y = Math.Max(40, Math.Min(y, height - size.Height - 8));
        MenuPanel.Margin = new Thickness(x, y, 0, 0);
    }

    // ---- Keyboard support -------------------------------------------------------------------------------------------

    /// <summary>Scrolls the focused tile into view: along its shelf (which has a hidden scroll bar) and then down the page.</summary>
    public void RevealFocused()
    {
        if (State is not { FocusedGame: { } game } state)
            return;

        if (state.Tab == ShellTab.Library)
        {
            if (LibraryGrid.ItemContainerGenerator.ContainerFromItem(game) is FrameworkElement container)
                container.BringIntoView();

            return;
        }

        var shelf = state.FocusedRow == 0 && state.Library.HasRecentlyPlayed ? RecentShelf : AllShelf;
        var list = state.FocusedRow == 0 && state.Library.HasRecentlyPlayed ? (System.Collections.IList)state.Library.RecentlyPlayedGames : state.Library.Games;
        var index = list.IndexOf(game);
        if (index >= 0 && FindScrollViewer(shelf) is { } scroller)
        {
            var step = (double)(FindResource("ActiveCardWidth") is double w ? w : 140) + 8;
            var left = 6 + index * step;
            var right = left + step - 8;
            if (left < scroller.HorizontalOffset + 24)
                scroller.ScrollToHorizontalOffset(Math.Max(0, left - 24));
            else if (right > scroller.HorizontalOffset + scroller.ViewportWidth - 24)
                scroller.ScrollToHorizontalOffset(right - scroller.ViewportWidth + 24);
        }

        shelf.BringIntoView();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;

            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>Opens the card menu for the focused game beside its tile; if the tile is not on screen (a virtualized shelf), under the hero.</summary>
    public void OpenCardMenuForFocused(GameEntry game)
    {
        if (State is not { } state)
            return;

        FrameworkElement? tile = null;
        foreach (var items in new ItemsControl[] { RecentShelf, AllShelf, LibraryGrid })
        {
            if (items.IsVisible && items.ItemContainerGenerator.ContainerFromItem(game) is DependencyObject container
                && FindButton(container) is { } button && state.FocusedRow == Behaviors.ShellFocus.GetRow(items))
            {
                tile = button;
                break;
            }
        }

        var anchor = tile is not null ? ShellGeometry.RectOf(tile, this) : new Rect(36, 300, 140, 40);
        state.OpenCardMenu(game, anchor);
    }

    private static Button? FindButton(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b)
                return b;

            if (FindButton(child) is { } nested)
                return nested;
        }

        return null;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        // How many tiles fit across the Library grid, for Up and Down.
        if (State is { } state)
        {
            var tile = (FindResource("ActiveCardWidth") is double w ? w : 140) + 8;
            var gutter = FindResource("ActivePageGutter") is double g ? g : 36;
            state.GridColumns = Math.Max(1, (int)((arrangeBounds.Width - 2 * gutter) / tile));
        }

        return base.ArrangeOverride(arrangeBounds);
    }
}
