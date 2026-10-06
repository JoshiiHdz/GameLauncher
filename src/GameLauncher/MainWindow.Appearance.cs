using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using GameLauncher.Behaviors;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The window's side of theming: the view model records which theme is chosen; applying it (swapping resource dictionaries,
/// placing the page host, sizing the caption) is done here because the window owns the resources. Nothing about the library is touched
/// by a switch - the same library, collections, settings and running services carry on under the new look.</summary>
public partial class MainWindow
{
    /// <summary>View state for the console themes (focus, tab, menus, feedback). Created once; it survives theme switches.</summary>
    internal ShellState Shell { get; private set; } = null!;

    /// <summary>The caption (drag area and window buttons) is 44 px in Axis, where it is the title bar, and 32 px in the console themes.</summary>
    private double CurrentCaptionHeight => ThemeManager.Current == ThemeId.Axis ? 44 : 32;

    private void InitializeAppearance(LibraryViewModel library)
    {
        Shell = new ShellState(library);
        library.ShowGameOnRibbonHome = game =>
        {
            Shell.ShowGameOnHome(game);
            RevealFocused();
        };

        library.PropertyChanged += OnAppearanceChanged;
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Closed += (_, _) => library.PropertyChanged -= OnAppearanceChanged;
        Loaded += (_, _) => UpdateLayoutMetrics();
        ApplyThemeShell();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.AppearanceTheme) || sender is not LibraryViewModel library)
            return;

        ThemeManager.Apply(library.AppearanceTheme);
        ApplyThemeShell();
    }

    /// <summary>Publishes the layout values for the current window size (normal, or the 760 x 480 "small" set).</summary>
    private void UpdateLayoutMetrics()
    {
        var small = ThemeLayout.IsSmall(ActualWidth, ActualHeight);
        ThemeLayout.Apply(small);
        if (Shell is not null)
            Shell.IsCompact = small;
    }

    /// <summary>Everything about the shell that depends on which theme is showing and cannot be a binding: where the page host sits, how
    /// tall the caption is.</summary>
    private void ApplyThemeShell()
    {
        PageHost.Clip = null;
        PageHost.SizeChanged -= RoundPageTop;
        if (ThemeManager.Current == ThemeId.ConsoleRibbon)
            PageHost.SizeChanged += RoundPageTop;

        UpdateLayoutMetrics();
        PlacePageHost(ThemeManager.Current);
        Shell.RequestBackdrop();

        // Only the showing console view is attached to the shell state. A hidden view with a data context keeps its whole tree alive (over a thousand
        // elements, every game's tile) and re-evaluates it on every focus change, which cost most of the time a hover took; detached, it is empty.
        TileView.DataContext = ThemeManager.Current == ThemeId.ConsoleTile ? Shell : null;
        RibbonView.DataContext = ThemeManager.Current == ThemeId.ConsoleRibbon ? Shell : null;

        if (WindowChrome.GetWindowChrome(this) is { IsFrozen: false } chrome)
            chrome.CaptionHeight = CurrentCaptionHeight;
    }

    // The Console-ribbon page is a layer with rounded top corners (22 px); the bottom corners run off the window.
    /// <summary>Called whenever a dialog opens or closes, so the Console-ribbon home can recede behind it.</summary>
    internal void NotifyModalChanged() => Shell.ModalOpen = IsModalOpen;

    private void RoundPageTop(object sender, SizeChangedEventArgs e) =>
        PageHost.Clip = new RectangleGeometry(new Rect(0, 0, PageHost.ActualWidth, PageHost.ActualHeight + 40), 22, 22);

    /// <summary>The one page presenter serves every theme (so a page is never built twice); only where it sits and what it is painted
    /// with change. Axis: beside the sidebar. Console-tile: under the top bar, a light dim so the art still shows.</summary>
    private void PlacePageHost(ThemeId theme)
    {
        switch (theme)
        {
            case ThemeId.ConsoleTile:
                Grid.SetRow(PageHost, 0);
                Grid.SetRowSpan(PageHost, 3);
                Grid.SetColumn(PageHost, 0);
                Grid.SetColumnSpan(PageHost, 2);
                PageHost.Margin = new Thickness(0, 84, 0, 0);
                PageHost.Background = new SolidColorBrush(Color.FromArgb(0x59, 0x07, 0x08, 0x0B));
                break;
            case ThemeId.ConsoleRibbon:
                Grid.SetRow(PageHost, 0);
                Grid.SetRowSpan(PageHost, 3);
                Grid.SetColumn(PageHost, 0);
                Grid.SetColumnSpan(PageHost, 2);
                PageHost.Margin = new Thickness(0, 32, 0, 0);
                PageHost.Background = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromArgb(0xF7, 0x0F, 0x11, 0x17), 0),
                    new GradientStop(Color.FromArgb(0xEB, 0x0F, 0x11, 0x17), 0.6),
                    new GradientStop(Color.FromArgb(0xD1, 0x0F, 0x11, 0x17), 1),
                ], new Point(0, 0.5), new Point(1, 0.5));
                break;
            default:
                Grid.SetRow(PageHost, 1);
                Grid.SetRowSpan(PageHost, 1);
                Grid.SetColumn(PageHost, 1);
                Grid.SetColumnSpan(PageHost, 1);
                PageHost.Margin = new Thickness(0);
                PageHost.SetResourceReference(Panel.BackgroundProperty, "Bg");
                break;
        }
    }

    // ---- Dialogs and the palette, per theme ---------------------------------------------------------------------------

    private double ActiveValue(string key, double fallback) => TryFindResource(key) is double d ? d : fallback;

    /// <summary>Dresses the shared dialog layer for the theme: a centred card in Axis, a right-hand sheet in Console-tile (and the command
    /// palette, which draws its own card, as a dropdown under the search field). Every property is set in every branch, so switching
    /// themes while a dialog is open leaves nothing behind from the previous look.</summary>
    private void StyleModalForTheme(bool bare, string title)
    {
        var theme = ThemeManager.Current;
        var console = theme != ThemeId.Axis;
        Grid.SetRow(ModalLayer, console ? 0 : 1);
        Grid.SetRowSpan(ModalLayer, console ? 3 : 2);
        ModalTitle.Text = title;

        MotionExtensions.SetEnter(ModalScrim, MotionExtensions.EnterKind.Fade);
        MotionExtensions.SetEnterDurationKey(ModalScrim, "DurPage");

        switch (theme)
        {
            case ThemeId.ConsoleTile:
                ModalScrim.SetResourceReference(Border.BackgroundProperty, bare ? "ScrimLight" : "Scrim");
                ModalCard.SetResourceReference(Border.CornerRadiusProperty, bare ? "RadiusPalette" : "RadiusDialog");
                ModalCard.Padding = new Thickness(0);
                if (bare)
                {
                    // The palette: a dropdown at the top right, 38 px down, level with the page's right gutter.
                    var gutter = ActiveValue("ActivePageGutter", 36);
                    ModalCard.HorizontalAlignment = HorizontalAlignment.Right;
                    ModalCard.VerticalAlignment = VerticalAlignment.Top;
                    ModalCard.Margin = new Thickness(24, 38, gutter, 24);
                    ModalCard.Width = double.NaN;
                    ModalCard.SetResourceReference(Border.BackgroundProperty, "Bg2");
                    ModalCard.BorderThickness = new Thickness(1);
                    ModalCard.SetResourceReference(Border.BorderBrushProperty, "Stroke");
                    ModalCard.Effect = null;
                    MotionExtensions.SetEnter(ModalCard, MotionExtensions.EnterKind.DropIn);
                    MotionExtensions.SetEnterDurationKey(ModalCard, "DurMenu");
                }
                else
                {
                    // A dialog: a sheet along the right edge, full height, a 1 px line on its left.
                    ModalCard.HorizontalAlignment = HorizontalAlignment.Right;
                    ModalCard.VerticalAlignment = VerticalAlignment.Stretch;
                    ModalCard.Margin = new Thickness(0);
                    ModalCard.Width = ActiveValue("ActiveDialogWidth", 400);
                    ModalCard.SetResourceReference(Border.BackgroundProperty, "Bg2");
                    ModalCard.BorderThickness = new Thickness(1, 0, 0, 0);
                    ModalCard.SetResourceReference(Border.BorderBrushProperty, "Stroke");
                    ModalCard.Effect = null;
                    MotionExtensions.SetEnter(ModalCard, MotionExtensions.EnterKind.SlideRight);
                    MotionExtensions.SetEnterDurationKey(ModalCard, "DurPanel");

                    // The title becomes the small green label above the content; there is no close button (Esc, the scrim, Cancel).
                    ModalTitle.Text = title.ToUpperInvariant();
                    ModalTitle.FontSize = 11;
                    ModalTitle.FontWeight = FontWeights.Bold;
                    ModalTitle.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                    ModalHeader.Margin = new Thickness(24, 48, 24, 4);
                    ModalCloseButton.Visibility = Visibility.Collapsed;
                    ModalScroll.Margin = new Thickness(24, 0, 24, 20);
                    if (_modals.Peek().Content is { } sheetContent)
                        sheetContent.Width = ModalCard.Width - 48;

                    return;
                }

                break;
            case ThemeId.ConsoleRibbon:
                ModalScrim.SetResourceReference(Border.BackgroundProperty, "Scrim");
                ModalCard.SetResourceReference(Border.CornerRadiusProperty, bare ? "RadiusPalette" : "RadiusDialog");
                ModalCard.Padding = new Thickness(0);
                ModalCard.HorizontalAlignment = HorizontalAlignment.Center;
                ModalCard.Width = bare ? double.NaN : ActiveValue("ActiveDialogWidth", 440);
                ModalCard.SetResourceReference(Border.BackgroundProperty, "Surface");
                ModalCard.BorderThickness = new Thickness(1);
                ModalCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                ModalCard.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 80, ShadowDepth = 30, Direction = 270, Opacity = 0.8, Color = Colors.Black };
                if (bare)
                {
                    ModalCard.VerticalAlignment = VerticalAlignment.Top;
                    ModalCard.Margin = new Thickness(24, ActiveValue("ActivePaletteTop", 110) - 0, 24, 24);
                }
                else
                {
                    ModalCard.VerticalAlignment = VerticalAlignment.Center;
                    ModalCard.Margin = new Thickness(24);
                    ModalCard.Padding = new Thickness(26, 22, 26, 26);
                    if (_modals.Peek().Content is { } cardContent)
                        cardContent.Width = ModalCard.Width - 52;

                    ModalTitle.FontSize = 22;
                    ModalTitle.FontWeight = FontWeights.Light;
                    ModalTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                    ModalHeader.Margin = new Thickness(0, 0, 0, 6);
                    ModalCloseButton.Visibility = Visibility.Visible;
                    ModalScroll.Margin = new Thickness(0);
                    MotionExtensions.SetEnter(ModalCard, MotionExtensions.EnterKind.ScaleIn);
                    MotionExtensions.SetEnterDurationKey(ModalCard, "DurDialog");
                    return;
                }

                MotionExtensions.SetEnter(ModalCard, MotionExtensions.EnterKind.ScaleIn);
                MotionExtensions.SetEnterDurationKey(ModalCard, bare ? "DurMenu" : "DurDialog");
                break;
            default:
                ModalScrim.SetResourceReference(Border.BackgroundProperty, "Scrim");
                ModalCard.SetResourceReference(Border.CornerRadiusProperty, "RadiusDialog");
                ModalCard.Padding = new Thickness(0);
                ModalCard.HorizontalAlignment = HorizontalAlignment.Center;
                ModalCard.Width = double.NaN;
                MotionExtensions.SetEnter(ModalCard, MotionExtensions.EnterKind.None);
                break;
        }

        // Everything but the Console-tile sheet keeps the card layout: its own title row and a close button.
        ModalTitle.FontSize = 17;
        ModalTitle.FontWeight = FontWeights.SemiBold;
        ModalTitle.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        ModalHeader.Margin = new Thickness(24, 14, 10, 2);
        ModalCloseButton.Visibility = Visibility.Visible;
        ModalScroll.Margin = new Thickness(0);
    }

    // ---- Keyboard (spec 07 section 7.1) -------------------------------------------------------------------------------

    private static bool IsTyping() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    /// <summary>The console themes' keys. Returns true when the key was used. Axis keeps the keys it always had; G folds its sidebar.</summary>
    private bool ThemeKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (DataContext is not LibraryViewModel library)
            return false;

        if (ThemeManager.Current == ThemeId.Axis)
        {
            if (key == Key.G && !IsTyping() && !IsModalOpen && Keyboard.Modifiers == ModifierKeys.None)
            {
                library.ToggleSidebarCommand.Execute(null);
                return true;
            }

            return false;
        }

        // The console themes have no keyboard shortcuts (they are driven by the mouse, and later a controller). The focus and menu logic below is kept for that
        // controller, but nothing here is reachable from the keyboard.
        if (!ThemeState.Instance.ShortcutsEnabled)
            return false;

        // Esc closes the topmost layer: a menu, then (the window's own handling) a dialog or the palette, then the Guide or Control
        // center, then an open page, then the Library tab back to Home.
        if (key == Key.Escape)
        {
            if (Shell.IsMenuOpen)
            {
                Shell.CloseMenu();
                return true;
            }

            if (IsModalOpen)
                return false;

            if (Shell.NavOpen)
            {
                Shell.NavOpen = false;
                return true;
            }

            if (library.IsPageOpen)
                return false;

            if (Shell.Tab == ShellTab.Library)
            {
                Shell.ShowHomeCommand.Execute(null);
                return true;
            }

            return false;
        }

        if (key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && IsModalOpen && _modals.Peek().Content is CommandPaletteDialog)
        {
            CloseTopModal();
            return true; // Ctrl+K toggles the palette
        }

        if (IsModalOpen || IsTyping())
            return false;

        if (Shell.IsMenuOpen)
        {
            switch (key)
            {
                case Key.Down:
                    Shell.MoveMenuSelection(1);
                    return true;
                case Key.Up:
                    Shell.MoveMenuSelection(-1);
                    return true;
                case Key.Enter:
                    Shell.RunActiveMenuItem();
                    return true;
            }

            return true; // a menu swallows the rest
        }

        if (key == Key.G && Keyboard.Modifiers == ModifierKeys.None && ThemeManager.Current == ThemeId.ConsoleTile)
        {
            Shell.ToggleNavCommand.Execute(null);
            return true;
        }

        if (Shell.NavOpen || library.IsPageOpen)
            return false;

        if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var moved = Shell.MoveFocus(key switch
            {
                Key.Left => ShellDirection.Left,
                Key.Right => ShellDirection.Right,
                Key.Up => ShellDirection.Up,
                _ => ShellDirection.Down,
            });
            if (moved)
                RevealFocused();

            return true; // handled either way, so the scroll viewers do not also scroll
        }

        var plain = Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase;
        if (key == Key.Enter && plain)
        {
            Shell.OpenFocused();
            return true;
        }

        var menuKey = key == Key.M || key == Key.Apps || (key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift);
        if (menuKey && Shell.HeroGame is { } game && !Shell.LibraryTileFocused)
        {
            OpenFocusedCardMenu(game);
            return true;
        }

        return false;
    }

    /// <summary>Scrolls the focused item into view in whichever console view is showing.</summary>
    private void RevealFocused()
    {
        if (ThemeManager.Current == ThemeId.ConsoleTile)
            TileView.RevealFocused();
        else if (ThemeManager.Current == ThemeId.ConsoleRibbon)
            RibbonView.RevealFocused();
    }

    private void OpenFocusedCardMenu(GameEntry game)
    {
        if (ThemeManager.Current == ThemeId.ConsoleTile)
            TileView.OpenCardMenuForFocused(game);
        else if (ThemeManager.Current == ThemeId.ConsoleRibbon)
            RibbonView.OpenCardMenuForFocused(game);
    }
}
