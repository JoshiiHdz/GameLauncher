using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>Console-ribbon's home and library (see ConsoleRibbonView.xaml). DataContext is the <see cref="ShellState"/>. In the ribbon strip a
/// click on an item focuses it and a click on the focused item opens it; hovering an icon also focuses it at once (only when the
/// pointer actually moves; the title sits under the icon, so nothing slides sideways beneath a resting pointer). In the Library
/// grid hover moves focus at once and a click brings the game up on the Home screen.</summary>
public partial class ConsoleRibbonView : UserControl
{
    private Point _lastMouse = new(double.NaN, double.NaN);

    public ConsoleRibbonView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => UpdateLetterWave();
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

    /// <summary>The pad landing on any of the Library's filters - from the grid, the rail, the top bar, or along the filters themselves - makes that filter swell and settle,
    /// as the selected card does when the pad returns to it. The checked filter is lit all the time, so without this there was no telling the pad had arrived.</summary>
    private void FilterPanel_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not FrameworkElement landed || State is not { Library.IsControllerMode: true })
            return;

        if (ReferenceEquals(e.NewFocus, e.OldFocus))
            return;

        // Back from a card: no card stays lit, and the grid goes back to its top, so the pad comes back to the first card and not to wherever it was left.
        if (e.OldFocus is Button { DataContext: GameEntry } && State is { Tab: ShellTab.Library } state)
        {
            state.ParkLibraryFocusAtTop();
            Behaviors.SmoothScroll.ToVertical(PosterScroll, 0);
        }

        Behaviors.PadPulse.Play(landed, scale: 1.06, originX: 0.0);
    }

    /// <summary>Set by the right stick scrolling the grid: for a moment, if the pad is on a letter of the A to Z rail, it follows the letter the grid has reached.</summary>
    internal void FollowRailAfterScroll() => _railFollowUntil = DateTime.UtcNow.AddMilliseconds(400);

    private DateTime _railFollowUntil;

    /// <summary>The pad on a top bar control (a tab, search, the controller, settings, power): it swells past its size and settles a little larger while the pad is on it, so
    /// the icons say where the pad is, and lets go when the pad leaves.</summary>
    private void TopBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is FrameworkElement control && control is System.Windows.Controls.Primitives.ButtonBase && State is { Library.IsControllerMode: true } && !ReferenceEquals(e.OldFocus, e.NewFocus))
            Behaviors.PadPulse.Play(control, scale: 1.3, settle: 1.12);
    }

    private void TopBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is FrameworkElement control && control is System.Windows.Controls.Primitives.ButtonBase)
            Behaviors.PadPulse.Rest(control);
    }

    private void RibbonItem_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
            state.FocusRibbonItem(game);
    }

    private void Collection_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (State is not { } state || sender is not FrameworkElement { DataContext: CollectionItem collection } pill)
            return;

        e.Handled = true;
        state.OpenCollectionMenu(collection.Name, ShellGeometry.RectOf(pill, this));
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
        {
            state.FocusRibbonItem(game);

            // The card the pad lands on swells past its focused size and settles (the ring and the glow come with it), so stepping through the grid is felt, not only seen.
            if (sender is FrameworkElement card && !ReferenceEquals(e.OldFocus, e.NewFocus) && state.Library.IsControllerMode)
                Behaviors.PadPulse.Play(card, scale: 1.07);
        }
    }

    // ---- The A to Z rail flows ----------------------------------------------------------------------------------------------

    /// <summary>How far a letter is raised above its size by the wave: the letter under the pad (or, with the pad elsewhere, the one the grid has reached) most, and its
    /// neighbours less and less, so the rail swells like a wave that follows the pad and the right stick up and down.</summary>
    internal static double WaveScale(int distance) => 1 + 0.5 * Math.Exp(-Math.Pow(distance / 1.4, 2));

    private string? _waveAnchor;
    private int _waveLetters;

    /// <summary>Moves the wave to where it should be: onto the letter the pad is on, else the one the grid has scrolled to. Cheap to call often.</summary>
    private void UpdateLetterWave()
    {
        if (State is not { ShowLetterIndex: true } state || !LetterRail.IsVisible)
            return;

        var anchor = Keyboard.FocusedElement is Button { DataContext: LetterItem on } && IsWithin(Keyboard.FocusedElement, LetterRail)
            ? on.Letter
            : state.LetterIndex.FirstOrDefault(l => l.IsCurrent)?.Letter;
        if (anchor == _waveAnchor && state.LetterIndex.Count == _waveLetters)
            return;

        var letters = new List<Button>();
        CollectLetters(LetterRail, letters);
        if (letters.Count == 0)
            return;

        _waveAnchor = anchor;
        _waveLetters = state.LetterIndex.Count;
        var at = anchor is null ? -1 : letters.FindIndex(b => b.DataContext is LetterItem item && item.Letter == anchor);
        var animate = Behaviors.Motion.AnimationsEnabled();
        for (var i = 0; i < letters.Count; i++)
            ApplyWave(letters[i], at < 0 ? 1.0 : WaveScale(Math.Abs(i - at)), animate);
    }

    private static bool IsWithin(IInputElement? element, DependencyObject ancestor) => element is DependencyObject d && FocusNavigator.IsWithin(d, ancestor);

    private static void CollectLetters(DependencyObject root, List<Button> found)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { DataContext: LetterItem } button)
                found.Add(button);
            else
                CollectLetters(child, found);
        }
    }

    /// <summary>The current wave scale of each letter (for the tests): by letter, as set (or being animated to).</summary>
    internal IReadOnlyDictionary<string, double> LetterScales()
    {
        var letters = new List<Button>();
        CollectLetters(LetterRail, letters);
        return letters.ToDictionary(b => ((LetterItem)b.DataContext).Letter, b => b.Tag is double target ? target : 1.0);
    }

    private static void ApplyWave(Button letter, double scale, bool animate)
    {
        if (letter.RenderTransform is not TransformGroup group || group.IsFrozen || group.Children.Count != 2)
        {
            group = new TransformGroup();
            group.Children.Add(new ScaleTransform(1, 1));
            group.Children.Add(new TranslateTransform());
            letter.RenderTransform = group;
            letter.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        letter.Tag = scale; // where the wave is sending it (the transform may still be on its way)
        var size = (ScaleTransform)group.Children[0];
        var shift = (TranslateTransform)group.Children[1];
        var left = -(scale - 1) * 14; // a letter that swells leans out over the grid side, not off the screen edge
        if (!animate)
        {
            size.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            size.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            size.ScaleX = scale;
            size.ScaleY = scale;
            shift.X = left;
            return;
        }

        DoubleAnimation To(double value) => new(value, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        size.BeginAnimation(ScaleTransform.ScaleXProperty, To(scale));
        size.BeginAnimation(ScaleTransform.ScaleYProperty, To(scale));
        shift.BeginAnimation(TranslateTransform.XProperty, To(left));
    }

    // ---- Cards come alive as they are scrolled into view ----------------------------------------------------------------------

    private HashSet<FrameworkElement> _cardsInView = new();
    private List<Button>? _posterCache;

    /// <summary>Whatever scrolls the grid - the right stick, the pad stepping down, the wheel - a card that comes into view rises in as it arrives, like the ones that were there
    /// when the Library opened. A rebuild of the grid (a filter, a sort) only notes what is showing: those cards have their own entrance.</summary>
    private void RevealCardsEnteringView(ScrollChangedEventArgs e)
    {
        var view = FocusNavigator.BoundsIn(PosterScroll, this);
        if (view.IsEmpty || view.Height < 4)
            return;

        // The cards are listed once and kept (walking the whole grid every frame of a stick scroll would be heavy); a rebuilt grid, or one whose cards have gone, is listed again.
        var rebuilt = e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0;
        if (_posterCache is null || rebuilt || (_posterCache.Count > 0 && (!_posterCache[0].IsLoaded || !_posterCache[^1].IsLoaded)))
        {
            _posterCache = new List<Button>();
            CollectPosters(PosterGrid, _posterCache);
        }

        var posters = _posterCache;
        var now = new HashSet<FrameworkElement>();
        var quiet = rebuilt || _cardsInView.Count == 0 || Math.Abs(e.VerticalChange) < 0.5;
        foreach (var poster in posters)
        {
            if (poster.Parent is not FrameworkElement card)
                continue;

            var box = FocusNavigator.BoundsIn(poster, this);
            if (box.IsEmpty || !box.IntersectsWith(view))
                continue;

            now.Add(card);
            if (!quiet && !_cardsInView.Contains(card))
                Behaviors.MotionExtensions.Replay(card);
        }

        _cardsInView = now;
    }

    /// <summary>The pad landing on a letter of the A to Z rail is handled by the wave (see <see cref="UpdateLetterWave"/>): the letter swells and its neighbours follow.</summary>
    private void LetterRail_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Stepping from one letter to another shows that letter: the grid scrolls to it and its first game is the focused one. (Not when the rail's letter is moved by the
        // grid scrolling under the right stick - that is the grid leading - and not on first arriving at the rail, which must not move the grid.)
        if (!_railFocusFollowsGrid && e.NewFocus is Button { DataContext: LetterItem item } && e.OldFocus is Button { DataContext: LetterItem }
            && State is { Library.IsControllerMode: true })
            JumpToLetter(item.Letter);

        UpdateLetterWave();
        Dispatcher.BeginInvoke(UpdateLetterWave, DispatcherPriority.Background); // once the focus has settled on where it is going
    }

    private void Poster_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && GameOf(sender) is { } game)
        {
            // The Home screen already shows a game's details (the newer, fuller layout), so a poster in the Library tab brings the game up there - there is no
            // second details page to land on.
            state.FocusRibbonItem(game);
            if (state.Library.ShowGameOnRibbonHome is { } showOnHome)
                showOnHome(game);
            else
                state.ShowGameOnHome(game);
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
        state.LibraryCardsIdle = false;
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
        RevealCardsEnteringView(e);
        if (State is { } shell)
            FollowTopRow(shell);

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

        // The pad is on the rail and the right stick scrolled the grid: the pad's letter follows the grid's, so scrolling and the rail agree about where you are.
        if (DateTime.UtcNow <= _railFollowUntil && current is not null && Keyboard.FocusedElement is Button { DataContext: LetterItem on } && on.Letter != current)
        {
            var target = FindLetterButton(this, current);
            if (target is not null)
            {
                _railFocusFollowsGrid = true;
                try { Keyboard.Focus(target); }
                finally { _railFocusFollowsGrid = false; }
            }
        }
    }

    private bool _railFocusFollowsGrid;

    /// <summary>The right stick scrolled the grid: the focused game becomes the first game of the top row on screen (the first game of a letter, when its heading is at the
    /// top), and the pad goes with it when it was on a card. With the pad on the filters nothing is lit, and scrolling leaves it so.</summary>
    private void FollowTopRow(ShellState state)
    {
        if (DateTime.UtcNow > _railFollowUntil || state.Tab != ShellTab.Library || _posterCache is not { Count: > 0 } posters)
            return;

        if (Keyboard.FocusedElement is DependencyObject focus && FocusNavigator.IsWithin(focus, FilterPanel))
            return;

        var view = FocusNavigator.BoundsIn(PosterScroll, this);
        Button? first = null;
        var firstBox = Rect.Empty;
        foreach (var poster in posters)
        {
            var box = FocusNavigator.BoundsIn(poster, this);
            if (box.IsEmpty || box.Top < view.Top - 4 || box.Top > view.Bottom)
                continue;

            if (first is null || box.Top < firstBox.Top - 6 || (Math.Abs(box.Top - firstBox.Top) <= 6 && box.Left < firstBox.Left))
            {
                first = poster;
                firstBox = box;
            }
        }

        if (first?.DataContext is not GameEntry game || ReferenceEquals(state.FocusedGame, game))
            return;

        var padOnCard = Keyboard.FocusedElement is Button { DataContext: GameEntry };
        state.FocusRibbonItem(game);
        if (padOnCard)
            Keyboard.Focus(first);
    }

    private static Button? FindLetterButton(DependencyObject root, string letter)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsVisible: true, Focusable: true, DataContext: LetterItem item } button && item.Letter == letter)
                return button;

            if (FindLetterButton(child, letter) is { } nested)
                return nested;
        }

        return null;
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

    // ---- Controller support ------------------------------------------------------------------------------------------

    private static bool IsGameTile(IInputElement? element) =>
        element is Button { DataContext: GameEntry or RibbonLibraryItem, IsVisible: true };

    /// <summary>Keyboard focus is on a game's tile: an item in the ribbon strip (or the Library tile) or a poster in the Library grid.</summary>
    internal bool GameTileHasFocus => IsGameTile(Keyboard.FocusedElement);

    /// <summary>Puts keyboard focus on the tile of the shell's focused game (or the Library tile) and scrolls it into view. Waits for layout, since the strip or grid may have just changed.</summary>
    internal void FocusFocusedTile()
    {
        if (State is not { } state)
            return;

        RevealFocused();
        Dispatcher.BeginInvoke(() => TryFocusTile(), DispatcherPriority.Loaded);
    }

    /// <summary>Puts keyboard focus on the focused game's tile right now; false when there is no such tile on screen (an empty Library tab, a game a filter hides).</summary>
    internal bool TryFocusTile()
    {
        if (State is not { } state)
            return false;

        object want = state.Tab == ShellTab.Home && state.LibraryTileFocused ? RibbonLibraryItem.Instance : state.FocusedGame ?? (object)RibbonLibraryItem.Instance;
        if (FindTile(this, want) is not { } tile)
            return false;

        tile.BringIntoView();
        return Keyboard.Focus(tile) is not null;
    }

    /// <summary>The Library grid's card that is on screen and nearest, in height, to <paramref name="from"/> (the focused game's own card when it is on screen): where the pad
    /// goes from the filters to the grid when the grid has been scrolled away from the focused game. Null when no card is on screen.</summary>
    internal Button? VisibleLibraryTile(Rect from)
    {
        if (State is not { } state)
            return null;

        var view = FocusNavigator.BoundsIn(PosterScroll, this);
        if (view.IsEmpty)
            return null;

        var tiles = new List<Button>();
        CollectPosters(PosterGrid, tiles);
        var shown = tiles.Where(t =>
        {
            var box = FocusNavigator.BoundsIn(t, this);
            return !box.IsEmpty && view.Contains(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2));
        }).ToList();

        if (shown.Count == 0)
            return null;

        var centreY = from.Top + from.Height / 2;
        return shown.FirstOrDefault(t => ReferenceEquals(t.DataContext, state.FocusedGame))
            ?? shown.OrderBy(t =>
            {
                var box = FocusNavigator.BoundsIn(t, this);
                return Math.Round(Math.Abs(box.Top + box.Height / 2 - centreY) / 8) * 1000 + box.Left; // nearest row first, then the left-most in it
            }).First();
    }

    private static void CollectPosters(DependencyObject root, List<Button> found)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsVisible: true, Focusable: true } button && button.Style == button.TryFindResource("PosterButton"))
                found.Add(button);

            CollectPosters(child, found);
        }
    }

    /// <summary>The Library card of a game, or null (for the tests).</summary>
    internal Button? FindPosterForTest(GameEntry game) => FindTile(PosterGrid, game);

    private static Button? FindTile(DependencyObject root, object context)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsVisible: true, Focusable: true } button && ReferenceEquals(button.DataContext, context)
                && (button is Controls.RibbonTile || button.Style == button.TryFindResource("PosterButton")))
                return button;

            if (FindTile(child, context) is { } nested)
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
