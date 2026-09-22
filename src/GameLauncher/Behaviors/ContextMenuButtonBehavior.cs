using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace GameLauncher.Behaviors;

/// <summary>
/// Opens an element's own ContextMenu on a plain left click, instead of needing a right-click/
/// Shift+F10 gesture - used for the game card's overflow "..." icon, which sits *inside* the
/// card's own clickable Button (that Button launches the game). Deliberately targets
/// PreviewMouseLeftButtonDown, not Click/a nested Button: a Button nested inside another Button
/// is a well-known WPF pitfall (the click can bubble and fire the outer Button's own command too,
/// launching the game underneath the menu). Marking the Preview event handled here stops it
/// reaching the outer Button at all, so a plain FrameworkElement (e.g. a Border) works for the
/// icon itself - no nested-button double-firing possible.
/// </summary>
public static class ContextMenuButtonBehavior
{
    public static readonly DependencyProperty OpensContextMenuOnClickProperty =
        DependencyProperty.RegisterAttached(
            "OpensContextMenuOnClick",
            typeof(bool),
            typeof(ContextMenuButtonBehavior),
            new PropertyMetadata(false, OnOpensContextMenuOnClickChanged));

    public static void SetOpensContextMenuOnClick(UIElement element, bool value) =>
        element.SetValue(OpensContextMenuOnClickProperty, value);

    public static bool GetOpensContextMenuOnClick(UIElement element) =>
        (bool)element.GetValue(OpensContextMenuOnClickProperty);

    private static void OnOpensContextMenuOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element)
        {
            return;
        }

        // Stops the outer card Button from ever seeing this click (see class remarks) - without
        // this, clicking the overflow icon would open the menu AND launch the game underneath it.
        e.Handled = true;

        // Set explicitly rather than relying on WPF's own right-click auto-wiring, which this
        // click-driven open bypasses entirely - PlacementTarget is what the menu's own
        // PlacementTarget.Tag/PlacementTarget.DataContext bindings resolve against.
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
