using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Console-ribbon's home and library (see ConsoleRibbonView.xaml). DataContext is the <see cref="ShellState"/>. In the ribbon strip a
/// click on an item focuses it and a click on the focused item opens it; hovering an icon also focuses it at once (only when the
/// pointer actually moves; the title sits under the icon, so nothing slides sideways beneath a resting pointer). In the Library
/// grid hover moves focus at once and a click opens Game details.</summary>
public partial class ConsoleRibbonView : UserControl
{
    private Point _lastMouse = new(double.NaN, double.NaN);

    public ConsoleRibbonView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => PositionMenu();
    }

    private ShellState? State => DataContext as ShellState;

    private static GameEntry? GameOf(object sender) => (sender as FrameworkElement)?.DataContext as GameEntry;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShellState old)
        {
            old.PropertyChanged -= OnStateChanged;
            old.PlayFocusRequested -= OnPlayFocusRequested;
        }

        if (e.NewValue is ShellState current)
        {
            current.PropertyChanged += OnStateChanged;
            current.PlayFocusRequested += OnPlayFocusRequested;
        }
    }

    private void OnPlayFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (HeroPlay.IsVisible)
            {
                HeroPlay.Focus();
                Keyboard.Focus(HeroPlay);
            }
        }, DispatcherPriority.Loaded);

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellState.MenuKind) && State is { IsMenuOpen: true })
            Dispatcher.BeginInvoke(PositionMenu, DispatcherPriority.Loaded);
    }

    // ---- The ribbon strip ---------------------------------------------------------------------------------------------

    private bool IsFocusedItem(object sender) =>
        State is { } state && (sender is Controls.RibbonTile { IsSelectedItem: true });

    private void RibbonLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state)
            return;

        if (state.LibraryTileFocused)
            state.ShowLibraryCommand.Execute(null);
        else
            state.FocusRibbonItem(null);
    }

    private void RibbonMore_Click(object sender, RoutedEventArgs e) => State?.ShowLibraryCommand.Execute(null);

    private void RibbonItem_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state || GameOf(sender) is not { } game)
            return;

        if (IsFocusedItem(sender))
            state.RequestPlayFocus(); // the home already shows this game's details: highlight Play
        else
            state.FocusRibbonItem(game);
    }

    private void RibbonItem_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
            state.FocusRibbonItem(game);
    }

    private void RibbonItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (State is not { } state || GameOf(sender) is not { } game)
            return;

        e.Handled = true;
        state.FocusRibbonItem(game);
        state.OpenCardMenu(game, ShellGeometry.RectOf((FrameworkElement)sender, this));
    }

    /// <summary>Hover moves focus in the strip at once, but only when the pointer really moves: icons sliding under a resting pointer (the strip
    /// scrolling, or a neighbour changing size) must not change focus on their own.</summary>
    private void RibbonItem_MouseMove(object sender, MouseEventArgs e)
    {
        // While the strip is being scrolled by the pointer at its ends, tiles slide under it: focus holds still instead of chasing them.
        if (State is not { IsMenuOpen: false, NavOpen: false } state || sender is not FrameworkElement element || IsFocusedItem(sender) || Behaviors.EdgeScroll.IsScrolling)
            return;

        var position = e.GetPosition(this);
        if (!double.IsNaN(_lastMouse.X) && (position - _lastMouse).Length < 1)
            return;

        _lastMouse = position;
        if (element.DataContext is GameEntry game)
            state.FocusRibbonItem(game);
        else if (element.DataContext is RibbonLibraryItem)
            state.FocusRibbonItem(null);
    }

    private void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer strip)
        {
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // ---- Hero and activity cards --------------------------------------------------------------------------------------

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state)
            return;

        if (state.LibraryTileFocused)
            state.ShowLibraryCommand.Execute(null);
        else if (state.HeroGame is { } game)
            state.Play(game); // a game that is already running is told so, not launched again
    }

    private void HeroUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && state.HeroGame is { } game)
            state.Library.UninstallGameCommand.Execute(game);
    }

    private void HeroPlayTime_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && state.HeroGame is { } game)
            state.Library.ShowPlayTimeCommand.Execute(game);
    }

    private void HeroFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && state.HeroGame is { } game)
            state.ToggleFavorite(game);
    }

    private void HeroMore_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && state.HeroGame is { } game && sender is FrameworkElement button)
            state.OpenMoreMenu(game, ShellGeometry.RectOf(button, this));
    }

    private void ActivityCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityCard card)
            card.Run();
    }

    private void Poster_MouseEnter(object sender, MouseEventArgs e)
    {
        if (State is { IsMenuOpen: false, NavOpen: false } state && GameOf(sender) is { } game)
        {
            state.FocusedRow = 0;
            state.FocusRibbonItem(game);
        }
    }

    private void Poster_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
            state.FocusRibbonItem(game);
    }

    private void Poster_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
        {
            state.FocusRibbonItem(game);
            state.Library.ShowGameDetailsCommand.Execute(game); // the Library tab has no details panel, so a poster opens Game details
        }
    }

    private void Poster_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (State is not { } state || GameOf(sender) is not { } game)
            return;

        e.Handled = true;
        state.FocusRibbonItem(game);
        state.OpenCardMenu(game, ShellGeometry.RectOf((FrameworkElement)sender, this));
    }

    // ---- Drive options and the menu --------------------------------------------------------------------------------------

    private void NavDriveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && (sender as FrameworkElement)?.DataContext is DriveSpaceInfo drive)
            state.OpenDriveMenu(drive, ShellGeometry.RectOf((FrameworkElement)sender, this));
    }

    private void MenuScrim_MouseDown(object sender, MouseButtonEventArgs e)
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

    // ---- The Library grid by letter -----------------------------------------------------------------------------------

    /// <summary>The poster of a game in the grouped grid: the group's container holds an inner list of its games.</summary>
    private FrameworkElement? FindPoster(ShellState state, GameEntry game)
    {
        var group = state.LibraryGroups.FirstOrDefault(g => g.Games.Contains(game));
        if (group is null || PosterGrid.ItemContainerGenerator.ContainerFromItem(group) is not DependencyObject container)
            return null;

        return FindDescendant<ItemsControl>(container)?.ItemContainerGenerator.ContainerFromItem(game) as FrameworkElement;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>Scrolls the Library grid so a letter's heading is at the top, and puts the focus on that letter's first game.</summary>
    public void JumpToLetter(string letter)
    {
        if (State is not { } state || state.LibraryGroups.FirstOrDefault(g => g.Letter == letter) is not { } group || group.Games.Count == 0)
            return;

        PosterGrid.UpdateLayout();
        if (PosterGrid.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement header)
        {
            var top = header.TransformToAncestor(PosterScroll).Transform(new Point(0, 0)).Y;
            PosterScroll.ScrollToVerticalOffset(Math.Max(0, PosterScroll.VerticalOffset + top - 8));
        }

        state.FocusedRow = 0;
        state.FocusedGame = group.Games[0];
    }

    private void Letter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LetterItem item)
            JumpToLetter(item.Letter);
    }

    /// <summary>Lights the rail's letter for the heading nearest the top of the grid as it scrolls.</summary>
    private void PosterScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (State is not { ShowLetterIndex: true } state)
            return;

        string? current = state.LibraryGroups.FirstOrDefault()?.Letter;
        foreach (var group in state.LibraryGroups)
        {
            if (PosterGrid.ItemContainerGenerator.ContainerFromItem(group) is not FrameworkElement container)
                continue;

            if (container.TransformToAncestor(PosterScroll).Transform(new Point(0, 0)).Y > 24)
                break;

            current = group.Letter;
        }

        state.SetCurrentLetter(current);
    }

    // ---- Keyboard support ---------------------------------------------------------------------------------------------

    /// <summary>Scrolls the focused item into view: along the strip (home) or down the grid (Library).</summary>
    public void RevealFocused()
    {
        if (State is not { } state)
            return;

        if (state.Tab == ShellTab.Library)
        {
            if (state.FocusedGame is { } game && FindPoster(state, game) is { } poster)
                poster.BringIntoView();

            return;
        }

        object item = state.LibraryTileFocused || state.FocusedGame is null ? RibbonLibraryItem.Instance : state.FocusedGame;
        var index = state.RibbonItems.IndexOf(item);
        if (index < 0 || FindScrollViewer(Ribbon) is not { } scroller)
            return;

        // The Library tile is pinned outside the strip, so the strip's first game is item 1.
        var idle = FindResource("ActiveRibbonItemSize") is double s ? s : 64;
        var estimate = Math.Max(0, index - 1) * (idle + 14);
        if (estimate < scroller.HorizontalOffset + 24 || estimate > scroller.HorizontalOffset + scroller.ViewportWidth - 160)
            scroller.ScrollToHorizontalOffset(Math.Max(0, estimate - 24));

        Dispatcher.BeginInvoke(() =>
        {
            if (Ribbon.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
                container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;

            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }

    public void OpenCardMenuForFocused(GameEntry game)
    {
        // Beside the focused icon in the strip (or, with no icon on screen, the More button under it).
        var anchor = FindSelectedTile(Ribbon) is { } tile ? ShellGeometry.RectOf(tile, this) : ShellGeometry.RectOf(HeroMore, this);
        State?.OpenCardMenu(game, anchor);
    }

    private static Controls.RibbonTile? FindSelectedTile(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Controls.RibbonTile { IsSelectedItem: true } tile)
                return tile;

            if (FindSelectedTile(child) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>Places the open menu next to what opened it: to the right of an icon or card (the left if that would run off the window), or under a
    /// button (above it if there is no room); always inside the window by 12 px and clear of the title bar. It used to dock to the far right edge.</summary>
    private void PositionMenu()
    {
        if (State is not { IsMenuOpen: true } state)
            return;

        var width = ActualWidth;
        var height = ActualHeight;
        MenuPanel.Measure(new Size(MenuPanel.Width, Math.Max(120, height - 60)));
        var size = MenuPanel.DesiredSize;
        var anchor = state.MenuAnchor;
        double x;
        double y;
        if (state.MenuKind is ShellMenuKind.Card or ShellMenuKind.More)
        {
            // Always beside what opened it - never above or below - so it cannot flip up over the title and the launcher chip.
            x = anchor.Right + 12;
            y = anchor.Top;
            if (x + size.Width > width - 12)
                x = anchor.Left - 12 - size.Width;
        }
        else
        {
            x = anchor.Left;
            y = anchor.Bottom + 8;
            if (y + size.Height > height - 12)
                y = anchor.Top - 8 - size.Height;
        }

        x = Math.Max(12, Math.Min(x, width - size.Width - 12));
        y = Math.Max(40, Math.Min(y, height - size.Height - 12));
        MenuPanel.Margin = new Thickness(x, y, 0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        if (State is { } state)
        {
            var poster = (FindResource("ActiveCardWidth") is double w ? w : 136) + 18;
            var filter = FindResource("ActiveFilterColumnWidth") is double f ? f : 240;
            state.GridColumns = Math.Max(1, (int)((arrangeBounds.Width - filter - 76) / poster));
        }

        return base.ArrangeOverride(arrangeBounds);
    }
}
