using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>How the pad moves focus around a screen, in one place so the window and the tests use the very same thing: which controls can be reached, which one a direction leads
/// to (<see cref="SpatialFocus"/> over their real positions, instead of WPF's own directional focus, which is hard to predict), and how a page scrolls so the focused control is
/// always on screen - and scrolls back when the focus goes back up.</summary>
internal static class FocusNavigator
{
    /// <summary>A game's tile in the strip, or its poster in the Library grid (or the Library tile): drawn with its own highlight and moved by the shell state.</summary>
    internal static bool IsGameTile(object? element) => element is Button { DataContext: GameEntry or RibbonLibraryItem };

    /// <summary>Every control on <paramref name="layer"/> the pad can land on, in no particular order. Of the game tiles only the focused game's counts: the shell state moves
    /// between tiles, and anything else steps onto "the" tile, never a neighbour of it.</summary>
    internal static List<FrameworkElement> Reachable(FrameworkElement layer, GameEntry? focusedGame, bool libraryTileFocused)
    {
        var found = new List<FrameworkElement>();
        Collect(layer, focusedGame, libraryTileFocused, found);
        return found;
    }

    private static void Collect(DependencyObject parent, GameEntry? focusedGame, bool libraryTileFocused, List<FrameworkElement> found)
    {
        var count = parent is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(parent) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
                continue; // a collapsed branch holds nothing reachable

            if (child is FrameworkElement element && IsReachable(element) && TileAllowed(element, focusedGame, libraryTileFocused))
                found.Add(element);

            Collect(child, focusedGame, libraryTileFocused, found);
        }
    }

    private static bool TileAllowed(FrameworkElement element, GameEntry? focusedGame, bool libraryTileFocused) => element.DataContext switch
    {
        // A game's ribbon item counts only while that game (not the Library tile) is the focused one; its poster in the Library grid counts whenever it is.
        GameEntry game when IsGameTile(element) => ReferenceEquals(game, focusedGame) && (element is not Controls.RibbonTile || !libraryTileFocused),
        RibbonLibraryItem when IsGameTile(element) => libraryTileFocused,
        _ => true,
    };

    /// <summary>A control that takes the pad: a button (a switch, a radio button), a text box, a drop-down or a list row - visible, enabled, with a size, and not just a part
    /// inside another control's template.</summary>
    internal static bool IsReachable(FrameworkElement e) =>
        (e is ButtonBase or TextBoxBase or ComboBox or ListBoxItem) && e.IsVisible && e.Focusable && e.IsEnabled && e.ActualWidth >= 2 && e.ActualHeight >= 2
        && (e.TemplatedParent is null || e.TemplatedParent is ContentPresenter); // a template's own parts (a scroll bar's arrows) are not controls of their own; an item's button, drawn from a data template, is

    /// <summary>The control's box in the coordinates of <paramref name="root"/> (scrolling and the controller scale included).</summary>
    internal static Rect BoundsIn(FrameworkElement element, Visual root)
    {
        try
        {
            return element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    /// <summary>The control a direction leads to from <paramref name="from"/>, or null when nothing lies that way.</summary>
    internal static FrameworkElement? Next(FrameworkElement from, IReadOnlyList<FrameworkElement> controls, ShellDirection direction, Visual root)
    {
        var origin = BoundsIn(from, root);
        if (origin.IsEmpty)
            return null;

        var others = controls.Where(c => !ReferenceEquals(c, from)).ToList();
        var boxes = others.Select(c => BoundsIn(c, root)).ToList();
        for (var i = boxes.Count - 1; i >= 0; i--)
        {
            if (boxes[i].IsEmpty)
            {
                boxes.RemoveAt(i);
                others.RemoveAt(i);
            }
        }

        var picked = SpatialFocus.Pick(origin, boxes, direction);
        return picked < 0 ? null : others[picked];
    }

    /// <summary>Where the pad starts on a screen it has not touched yet: the control nearest the top left.</summary>
    internal static FrameworkElement? FirstInReadingOrder(IReadOnlyList<FrameworkElement> controls, Visual root, Func<FrameworkElement, bool>? skip = null)
    {
        FrameworkElement? best = null;
        Rect bestBox = default;
        foreach (var control in controls)
        {
            if (skip?.Invoke(control) == true)
                continue;

            var box = BoundsIn(control, root);
            if (box.IsEmpty)
                continue;

            if (best is null || box.Top < bestBox.Top - 12 || (Math.Abs(box.Top - bestBox.Top) <= 12 && box.Left < bestBox.Left))
            {
                best = control;
                bestBox = box;
            }
        }

        return best;
    }

    /// <summary>Scrolls every scroll area around <paramref name="element"/> so it is in view with some room to spare. The topmost control of an area shows the very top of it (the
    /// title and the notes above the first option), and the bottom-most the very bottom; so going back up scrolls back up, which plain "bring into view" does not do.</summary>
    internal static void EnsureVisible(FrameworkElement element, IReadOnlyList<FrameworkElement> controls)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroller || (scroller.ScrollableHeight <= 0 && scroller.ScrollableWidth <= 0))
                continue;

            Rect box;
            try
            {
                box = element.TransformToAncestor(scroller).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (scroller.ScrollableHeight > 0)
                Behaviors.SmoothScroll.ToVertical(scroller, VerticalTarget(element, scroller, box, controls));

            if (scroller.ScrollableWidth > 0)
            {
                var margin = Math.Min(24, scroller.ViewportWidth / 4);
                var target = scroller.HorizontalOffset;
                if (box.Left < margin)
                    target += box.Left - margin;
                else if (box.Right > scroller.ViewportWidth - margin)
                    target += box.Right - (scroller.ViewportWidth - margin);

                scroller.ScrollToHorizontalOffset(Math.Clamp(target, 0, scroller.ScrollableWidth));
            }
        }
    }

    private static double VerticalTarget(FrameworkElement element, ScrollViewer scroller, Rect box, IReadOnlyList<FrameworkElement> controls)
    {
        var margin = Math.Min(28, scroller.ViewportHeight / 4);

        // Worked out against where the area is heading (a glide may be part way there), so a quick second press carries on from the same destination.
        var heading = Behaviors.SmoothScroll.TargetOf(scroller);
        var shift = scroller.VerticalOffset - heading;
        var top = box.Top + shift;
        var bottom = box.Bottom + shift;
        var target = heading;
        if (top < margin)
            target += top - margin;
        else if (bottom > scroller.ViewportHeight - margin)
            target += bottom - (scroller.ViewportHeight - margin);

        // Where the element sits in the scrolled content, to tell whether anything else in this area is above or below it.
        var contentTop = top + heading;
        var neighbours = controls.Where(c => !ReferenceEquals(c, element) && IsWithin(c, scroller)).ToList();
        bool Above(FrameworkElement c) => ContentTop(c, scroller) < contentTop - 1;
        bool Below(FrameworkElement c) => ContentTop(c, scroller) > contentTop + 1;

        if (!neighbours.Any(Above))
            target = 0;
        else if (!neighbours.Any(Below))
            target = scroller.ScrollableHeight;

        return Math.Clamp(target, 0, scroller.ScrollableHeight);
    }

    private static double ContentTop(FrameworkElement control, ScrollViewer scroller)
    {
        try
        {
            return control.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y + scroller.VerticalOffset;
        }
        catch (InvalidOperationException)
        {
            return double.NaN;
        }
    }

    internal static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }
}
