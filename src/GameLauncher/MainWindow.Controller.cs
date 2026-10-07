using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The window's side of controller mode (see docs/design/controller-mode-plan.md): reads the pad while the launcher is the window in front, hands
/// presses to the <see cref="ControllerRouter"/>, and dresses the window for a couch - full screen over the taskbar, everything scaled up, the caption
/// gone, the mouse pointer hidden until the mouse moves.</summary>
public partial class MainWindow
{
    private ControllerInput? _pad;
    private ControllerRouter? _router;
    private WindowSurface? _surface;
    private bool _padInUse;
    private bool _controllerToastShown;
    private WindowState _stateBeforeController;
    private Rect _boundsBeforeController;
    private bool _topmostBeforeController;
    private Size _minSizeBeforeController;
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Point _lastMousePoint = new(double.NaN, double.NaN);

    /// <summary>The router that turns pad buttons into actions; tests press buttons through it without a real pad.</summary>
    internal ControllerRouter ControllerRouterForTest => _router!;

    /// <summary>The pad reader; tests give it a fake reading.</summary>
    internal ControllerInput PadForTest => _pad!;

    private void InitializeController(LibraryViewModel library, bool startRuntimeServices)
    {
        _surface = new WindowSurface(this);
        PadKeyboard.Closed += () =>
        {
            Shell.KeyboardOpen = false;
            UpdateFocusRing();
        };
        _router = new ControllerRouter(library, Shell, _surface);
        _router.LeaveRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (library.IsControllerMode && AppShell.Confirm("Leave controller mode?", "The launcher goes back to a normal window.", yes: "Leave", no: "Stay"))
                library.ExitControllerMode();
        });
        _router.EnterRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (library.IsControllerMode || IsModalOpen || _router.AskingToEnter)
                return;

            _router.AskingToEnter = true;
            try
            {
                if (AppShell.Confirm("Switch to controller mode?", "The launcher goes full screen with the PlayStation look, for the controller. Leaving it brings this look back.", yes: "Switch", no: "Stay"))
                    library.EnterControllerMode();
            }
            finally { _router.AskingToEnter = false; }
        });

        _pad = new ControllerInput(mayRead: () => PadMayRead(library),
            repeats: button => button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right
                || library.ControllerMap.Resolve(button) is PadFunction.PageUp or PadFunction.PageDown); // a button given another job fires once
        _pad.ButtonPressed += button =>
        {
            HideCursor();
            NotePadUse();
            // Directions and page keys repeat while held (about eight a second), so they are not logged one by one.
            if (button is not (PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right) && library.ControllerMap.Resolve(button) is not (PadFunction.PageUp or PadFunction.PageDown))
                Logger.Info($"Pad: {button} (mode {(library.IsControllerMode ? "on" : "off")}; {DescribeScreen(library)})");
            _router!.Handle(button);
        };
        _pad.ConnectionChanged += connected => HandlePadConnection(library, connected);
        _pad.BatteryChanged += battery =>
        {
            Logger.Info(battery is { } b ? $"Controller power: {b.Describe()}." : "The controller reports no battery.");
            library.SetControllerStatus(_pad.IsConnected, battery);
        };

        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsControllerMode))
                ApplyControllerMode(library);
        };

        // The pad's focus ring follows the focus, the layout and the scrolling; the right stick scrolls; a page or dialog that opens gets the pad at once.
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => UpdateFocusRing()), true);
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateFocusRing()), true);
        LayoutUpdated += (_, _) => UpdateFocusRing();
        _pad.RightStick += (_, y, seconds) => ScrollWithStick(library, y, seconds);
        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsPageOpen) && library.IsControllerMode && library.IsPageOpen)
                Dispatcher.BeginInvoke(() => _surface!.EnsureFocus(), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        _cursorTimer.Tick += (_, _) => HideCursor();
        PreviewMouseMove += OnControllerPreviewMouseMove;
        Deactivated += (_, _) =>
        {
            ShowCursor();
            if (library.IsControllerMode)
                Logger.Info("The launcher window lost the focus while controller mode is on (the pad is ignored until it is back in front).");
        };
        Activated += (_, _) =>
        {
            if (library.IsControllerMode)
                Logger.Info("The launcher window is in front again.");
        };

        if (!startRuntimeServices)
            return;

        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        heartbeat.Tick += (_, _) =>
        {
            if (library.IsControllerMode)
                Logger.Info($"Controller heartbeat: mode {(library.IsControllerMode ? "on" : "off")}, connected {_pad.IsConnected}, "
                    + $"{(PadGateReason(library) ?? "reading allowed")}, game running {library.HasRunningGame}, timer ticks {_pad.TickCount}, readings {_pad.PollCount}.");
        };

        Loaded += (_, _) =>
        {
            _pad.Start(Dispatcher);
            heartbeat.Start();
            Logger.Info("Controller input started (the pad is only read while this window is in front).");
        };
        Closed += (_, _) =>
        {
            heartbeat.Stop();
            _pad.Dispose();
            _cursorTimer.Stop();
            ShowCursor();
        };
    }

    /// <summary>XInput is read system-wide, so the pad is only read while this window is the one in front (otherwise pressing A in a
    /// game would press A here as well). A game running behind the launcher does not matter: when a game has the screen this window is not in front, and when
    /// the launcher is in front the pad is meant for it.</summary>
    private bool PadMayRead(LibraryViewModel library)
    {
        var reason = PadGateReason(library);
        if (reason != _lastGateReason)
        {
            _lastGateReason = reason;
            if (library.IsControllerMode)
                Logger.Info(reason is null ? "The pad is being read." : $"The pad is not being read: {reason}.");
        }

        return reason is null;
    }

    private string? _lastGateReason;

    /// <summary>Why the pad is not being read right now, or null when it is.</summary>
    internal string? PadGateReason(LibraryViewModel library) =>
        !IsVisible ? "window hidden"
        : WindowState == WindowState.Minimized ? "window minimized"
        : !IsActive ? "window not in front" // a game that has the screen is exactly this case
        : null;

    private string DescribeScreen(LibraryViewModel library) =>
        Shell.IsMenuOpen ? $"menu {Shell.MenuKind}" : IsModalOpen ? "dialog" : library.IsPageOpen ? "page" : Shell.NavOpen ? "control center" : $"{Shell.Tab} tab";

    /// <summary>A controller was found or went away (it was switched off, went to sleep, or was unplugged). The pill follows, and in controller mode the screen says so: a toast,
    /// a hint that stays, and the mouse pointer comes back so the mode can be left with a click. The mode itself stays on: a pad that sleeps mid-session wakes with a button press
    /// and carries on where it was.</summary>
    internal void HandlePadConnection(LibraryViewModel library, bool connected)
    {
        Logger.Info(connected ? "A controller was found." : "The last controller went away.");
        library.SetControllerStatus(connected, _pad?.Battery);

        if (library.IsControllerMode)
        {
            if (connected)
            {
                Shell.ShowFeedback("Controller connected", "Back in control");
                _cursorTimer.Start();
            }
            else
            {
                ShowCursor();
                Shell.ShowFeedback("Controller disconnected", "Press a button on it to wake it up, or click the controller button to leave");
            }

            return;
        }

        OnPadConnectionChanged(library, connected);
    }

    private void OnPadConnectionChanged(LibraryViewModel library, bool connected)
    {
        if (!connected || _controllerToastShown || library.IsControllerMode || library.IsConsoleTileTheme)
            return;

        _controllerToastShown = true;
        Shell.ShowFeedback("Controller found", "Press Start to use controller mode");
    }

    private void ApplyControllerMode(LibraryViewModel library)
    {
        Logger.Info(library.IsControllerMode ? "Controller mode on." : "Controller mode off.");
        _padInUse = library.IsControllerMode; // the mode is for the pad: its ring shows until the mouse is used
        if (library.IsControllerMode)
        {
            _stateBeforeController = WindowState;
            _boundsBeforeController = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _topmostBeforeController = Topmost;
            _minSizeBeforeController = new Size(MinWidth, MinHeight);

            var monitor = WindowWorkArea.MonitorOf(this);
            Logger.Info(monitor is { } found
                ? $"Controller mode: screen {found.Width:0}x{found.Height:0} (Windows scaling already applied), extra scale {ControllerScale.For(found.Width, found.Height)}."
                : "Controller mode: the screen could not be measured; keeping the window size.");
            var scale = monitor is { } size ? ControllerScale.For(size.Width, size.Height) : 1.0;
            RootGrid.LayoutTransform = new ScaleTransform(scale, scale);
            WindowState = WindowState.Normal;
            if (monitor is { } m)
            {
                MinWidth = 0;
                MinHeight = 0;
                Left = m.Left;
                Top = m.Top;
                Width = m.Width;
                Height = m.Height;
            }

            Topmost = true;
            Shell.ClearFeedback();
            if (!library.ControllerConnected)
                Shell.ShowFeedback("No controller connected", "Wake or plug in your controller, or click the controller button to leave");
            Dispatcher.BeginInvoke(() => RibbonView.FocusFocusedTile(), DispatcherPriority.Loaded);
            _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            ShowCursor();
            PadKeyboard.Close(restoreFocus: false); // the keyboard is for the pad: leaving the mode puts it away
            Topmost = _topmostBeforeController;
            RootGrid.LayoutTransform = Transform.Identity;
            MinWidth = _minSizeBeforeController.Width;
            MinHeight = _minSizeBeforeController.Height;
            Left = _boundsBeforeController.Left;
            Top = _boundsBeforeController.Top;
            Width = _boundsBeforeController.Width;
            Height = _boundsBeforeController.Height;
            if (_stateBeforeController == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
    }

    // ---- The mouse pointer is hidden while the pad is in use -------------------------------------------------------------

    private void OnControllerPreviewMouseMove(object sender, MouseEventArgs e) => NotePointerAt(e.GetPosition(this));

    /// <summary>The pad was used: its ring shows.</summary>
    internal void NotePadUse()
    {
        _padInUse = true;
        UpdateFocusRing();
    }

    /// <summary>The pointer was reported at this spot. Only a real move counts as using the mouse: Windows also reports the pointer, without it moving, whenever the screen
    /// changes under it (a page opening, the window resizing), and that must not take the ring away from the pad.</summary>
    internal void NotePointerAt(Point point)
    {
        if (double.IsNaN(_lastMousePoint.X) || (point - _lastMousePoint).Length < 2)
        {
            if (double.IsNaN(_lastMousePoint.X))
                _lastMousePoint = point; // the first report only says where the pointer is

            return;
        }

        _lastMousePoint = point;
        _padInUse = false; // the mouse is in use now: the pad's ring steps aside
        UpdateFocusRing();
        ShowCursor();
        if (DataContext is LibraryViewModel { IsControllerMode: true })
            _cursorTimer.Start();
    }

    private void HideCursor()
    {
        // Only while a pad is there to use: with none (it sleeps, or the mode was started with the mouse) the pointer stays, so the mouse can still be used.
        if (DataContext is LibraryViewModel { IsControllerMode: true, ControllerConnected: true })
            Mouse.OverrideCursor = Cursors.None;
    }

    private void ShowCursor()
    {
        if (Mouse.OverrideCursor == Cursors.None)
            Mouse.OverrideCursor = null;

        _cursorTimer.Stop();
    }

    // ---- The on-screen keyboard ---------------------------------------------------------------------------------------------

    /// <summary>Brings the keyboard up on a text box (A on the text box does this) and puts the pad on its keys.</summary>
    internal void OpenPadKeyboard(TextBox box)
    {
        PadKeyboard.Open(box);
        Shell.KeyboardOpen = true;
        box.BringIntoView();
        Dispatcher.BeginInvoke(() => _surface?.EnsureFocus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Keeps the keyboard in step with the dialogs: the search palette is no use without typing, so it comes up with the keyboard; and a keyboard whose text box has gone
    /// (its dialog closed) is put away.</summary>
    private void PadKeyboardFollowsDialogs()
    {
        if (PadKeyboard.IsOpen && PadKeyboard.Target is { } target && !target.IsVisible)
            PadKeyboard.Close(restoreFocus: false);

        if (!IsModalOpen || DataContext is not LibraryViewModel { IsControllerMode: true } || _modals.Peek().Content is not CommandPaletteDialog dialog)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!PadKeyboard.IsOpen && IsModalOpen && ReferenceEquals(_modals.Peek().Content, dialog) && DataContext is LibraryViewModel { IsControllerMode: true })
                OpenPadKeyboard(dialog.QueryBox);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---- The focus ring, and the right stick -------------------------------------------------------------------------------

    internal void EnsurePadFocus() => _surface?.EnsureFocus();

    /// <summary>Draws the ring round the control the pad is on (or hides it). Several controls draw no focus of their own (the drive rows, the cards), and the ones that do each
    /// do it differently, so there is one ring for all of them. Game tiles are left alone: they highlight themselves.</summary>
    internal void UpdateFocusRing()
    {
        if (!TryGetRingTarget(out var target))
        {
            if (FocusRing.Visibility != Visibility.Collapsed)
                FocusRing.Visibility = Visibility.Collapsed;

            return;
        }

        var box = FocusNavigator.BoundsIn(target, RootGrid);
        if (box.IsEmpty || box.Width < 2 || box.Height < 2 || !IsInsideScrollViews(target, box))
        {
            if (FocusRing.Visibility != Visibility.Collapsed)
                FocusRing.Visibility = Visibility.Collapsed;

            return;
        }

        var ring = Rect.Inflate(box, 4, 4);
        var radius = Math.Min(16, ring.Height / 2);
        SetIfChanged(FocusRing, Canvas.LeftProperty, ring.Left);
        SetIfChanged(FocusRing, Canvas.TopProperty, ring.Top);
        SetIfChanged(FocusRing, WidthProperty, ring.Width);
        SetIfChanged(FocusRing, HeightProperty, ring.Height);
        if (Math.Abs(FocusRing.CornerRadius.TopLeft - radius) > 0.5)
            FocusRing.CornerRadius = new CornerRadius(radius);

        if (FocusRing.Visibility != Visibility.Visible)
            FocusRing.Visibility = Visibility.Visible;
    }

    private static void SetIfChanged(DependencyObject element, DependencyProperty property, double value)
    {
        if (element.GetValue(property) is not double current || double.IsNaN(current) || Math.Abs(current - value) > 0.5)
            element.SetValue(property, value);
    }

    private bool TryGetRingTarget(out FrameworkElement target)
    {
        target = null!;
        if (!_padInUse || DataContext is not LibraryViewModel { IsControllerMode: true } || Keyboard.FocusedElement is not FrameworkElement focused)
            return false;

        if (!focused.IsVisible || FocusNavigator.IsGameTile(focused) || !FocusNavigator.IsWithin(focused, RootGrid))
            return false;

        if (IsModalOpen && _modals.Peek().Content is CommandPaletteDialog)
            return false; // the palette lights its own row

        target = focused;
        return true;
    }

    /// <summary>False when the control is scrolled out of sight (its ring would float over something else).</summary>
    private bool IsInsideScrollViews(FrameworkElement element, Rect box)
    {
        var centre = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller && (scroller.ScrollableHeight > 0 || scroller.ScrollableWidth > 0))
            {
                var view = FocusNavigator.BoundsIn(scroller, RootGrid);
                if (!view.IsEmpty && !view.Contains(centre))
                    return false;
            }
        }

        return true;
    }

    private const double StickScrollPixelsPerSecond = 1400;

    /// <summary>The right stick scrolls the page under the pad: up on the stick is up the page, and the further it is pushed the faster.</summary>
    private void ScrollWithStick(LibraryViewModel library, double stickY, double seconds)
    {
        if (!library.IsControllerMode || Shell.IsMenuOpen || stickY == 0)
            return;

        if (ScrollerForStick() is not { } scroller)
            return;

        _padInUse = true;
        Behaviors.SmoothScroll.Stop(scroller); // the stick is scrolling by hand now, not gliding somewhere
        scroller.ScrollToVerticalOffset(Math.Clamp(scroller.VerticalOffset - stickY * StickScrollPixelsPerSecond * seconds, 0, scroller.ScrollableHeight));
    }

    /// <summary>The scroll area the stick moves: the main content of the screen that is showing - the visible scroll area with the most to scroll. (Not simply the one round the
    /// focus: with the pad on a list of categories, the stick should still scroll the options beside it, not the list.)</summary>
    internal ScrollViewer? ScrollerForStick()
    {
        var layer = IsModalOpen ? (FrameworkElement)ModalLayer : DataContext is LibraryViewModel { IsPageOpen: true } ? PageHost : RibbonView;
        return FindScrollViewers(layer).Where(s => s.IsVisible && s.ScrollableHeight > 0).OrderByDescending(s => s.ScrollableHeight).FirstOrDefault();
    }

    internal void ScrollWithStickForTest(double stickY, double seconds) => ScrollWithStick((LibraryViewModel)DataContext, stickY, seconds);

    private static IEnumerable<ScrollViewer> FindScrollViewers(DependencyObject root)
    {
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
                continue;

            if (child is ScrollViewer scroller)
                yield return scroller;

            foreach (var nested in FindScrollViewers(child))
                yield return nested;
        }
    }

    /// <summary>Where the pad would go from a control in a direction (no focus is moved): for tests and reviews of the navigation.</summary>
    internal FrameworkElement? PadNeighbour(FrameworkElement current, ShellDirection direction) => _surface!.NeighbourOf(current, direction, _surface.ReachableControls());

    internal IReadOnlyList<FrameworkElement> PadControls() => _surface!.ReachableControls();

    // ---- What the router asks of the window ---------------------------------------------------------------------------

    private sealed class WindowSurface(MainWindow window) : IControllerSurface
    {
        /// <summary>The layer the focus belongs in right now: a dialog, else an open page, else the PlayStation home.</summary>
        private FrameworkElement ActiveLayer =>
            window.PadKeyboard.IsOpen ? window.PadKeyboard
            : window.IsModalOpen ? window.ModalLayer : window.DataContext is LibraryViewModel { IsPageOpen: true } ? window.PageHost : window.RibbonView;

        public bool GameTileHasFocus => window.RibbonView.GameTileHasFocus;

        /// <summary>Where a direction leads from a control, with the list-of-choices rules, without moving anything (the tests and the review dumps read the whole map with it).</summary>
        internal FrameworkElement? NeighbourOf(FrameworkElement current, ShellDirection direction, IReadOnlyList<FrameworkElement> controls) =>
            current is ListBoxItem row ? NextFromListRow(row, direction, controls) : NextFrom(current, direction, controls);

        internal List<FrameworkElement> ReachableControls() => Controls();

        /// <summary>Every control the pad can land on in the layer that is showing.</summary>
        private List<FrameworkElement> Controls() =>
            FocusNavigator.Reachable(ActiveLayer, window.Shell.FocusedGame, window.Shell.LibraryTileFocused);

        /// <summary>Puts keyboard focus on a control, scrolls it into view, and lets a list follow the pad (a Settings category is chosen as soon as the pad is on it).</summary>
        private void Land(FrameworkElement target, IReadOnlyList<FrameworkElement> controls)
        {
            Keyboard.Focus(target);
            FocusNavigator.EnsureVisible(target, controls);
            if (target is ListBoxItem row && ItemsControl.ItemsControlFromItemContainer(row) is ListBox)
                row.IsSelected = true;

            window.UpdateFocusRing();
        }

        public bool MoveFocus(ShellDirection direction)
        {
            if (Palette is { } palette)
            {
                if (direction == ShellDirection.Up) palette.MoveSelection(-1);
                else if (direction == ShellDirection.Down) palette.MoveSelection(1);
                return true; // Left and Right have nowhere to go in a list
            }

            if (Keyboard.FocusedElement is not FrameworkElement current)
                return false;

            // The A to Z rail beside the Library grid: A jumps to a letter (and puts the focused game there); Left goes back to that game in the grid.
            if (current is Button { DataContext: LetterItem } && direction == ShellDirection.Left)
                return window.RibbonView.TryFocusTile();

            // The nearest control that way, by where the controls really are on screen - the same on every page, whatever the page is built from. A list of choices (the Settings
            // categories) has its own rules: see below.
            var controls = Controls();
            var target = NeighbourOf(current, direction, controls);
            if (target is null)
                return false; // nothing lies that way: the focus stays put

            Land(target, controls);
            return true;
        }

        private static ListBox? ListOf(ListBoxItem row) => ItemsControl.ItemsControlFromItemContainer(row) as ListBox;

        /// <summary>From a row of a list of choices: Up and Down step through the list and stop at its ends (Up from the first row goes on to what is above the list); Right goes
        /// into the options beside it, to the first one; Left has nowhere to go.</summary>
        private FrameworkElement? NextFromListRow(ListBoxItem row, ShellDirection direction, IReadOnlyList<FrameworkElement> controls)
        {
            var list = ListOf(row);
            switch (direction)
            {
                case ShellDirection.Up or ShellDirection.Down:
                    var rows = controls.Where(c => c is ListBoxItem other && ReferenceEquals(ListOf(other), list)).ToList();
                    if (FocusNavigator.Next(row, rows, direction, window.RootGrid) is { } step)
                        return step;

                    return direction == ShellDirection.Down
                        ? null
                        : FocusNavigator.Next(row, controls.Where(c => c is not ListBoxItem).ToList(), direction, window.RootGrid);

                case ShellDirection.Right:
                    var edge = FocusNavigator.BoundsIn(row, window.RootGrid).Right; // the row, not the list: a list control can be wider than its rows
                    var beside = controls.Where(c => c is not ListBoxItem && FocusNavigator.BoundsIn(c, window.RootGrid).Left >= edge - 1).ToList();
                    return FocusNavigator.FirstInReadingOrder(beside, window.RootGrid);

                default:
                    return null;
            }
        }

        /// <summary>From any other control: the spatial neighbour - except that a list of choices is always entered at its chosen row (Down from the page's Back button lands on the
        /// category that is showing, not on whichever happens to be nearest), and Left from the options goes back to that chosen row from anywhere on the page.</summary>
        private FrameworkElement? NextFrom(FrameworkElement current, ShellDirection direction, IReadOnlyList<FrameworkElement> controls)
        {
            // Up and Down never step sideways into a list of choices that stands beside the options (the Settings categories): only a list straight above or below counts.
            // Left is how the pad goes back to such a list.
            var usable = controls;
            if (direction is ShellDirection.Up or ShellDirection.Down)
            {
                var from = FocusNavigator.BoundsIn(current, window.RootGrid);
                usable = controls.Where(c => c is not ListBoxItem || SpatialFocus.InBeam(from, FocusNavigator.BoundsIn(c, window.RootGrid), direction)).ToList();
            }

            var target = FocusNavigator.Next(current, usable, direction, window.RootGrid);

            if (direction == ShellDirection.Left && (target is null || target is ListBoxItem))
            {
                var left = FocusNavigator.BoundsIn(current, window.RootGrid).Left;
                var rowToTheLeft = controls.OfType<ListBoxItem>().FirstOrDefault(r => ListOf(r) is { } list && FocusNavigator.BoundsIn(list, window.RootGrid).Right <= left + 1);
                if (rowToTheLeft is not null)
                    return ChosenRow(ListOf(rowToTheLeft)!, controls) ?? rowToTheLeft;
            }

            if (target is ListBoxItem entered && ListOf(entered) is { } enteredList && !(current is ListBoxItem same && ReferenceEquals(ListOf(same), enteredList)))
                return ChosenRow(enteredList, controls) ?? target;

            return target;
        }

        private static ListBoxItem? ChosenRow(ListBox list, IReadOnlyList<FrameworkElement> controls) =>
            controls.OfType<ListBoxItem>().FirstOrDefault(r => ReferenceEquals(ListOf(r), list) && r.IsSelected)
            ?? controls.OfType<ListBoxItem>().FirstOrDefault(r => ReferenceEquals(ListOf(r), list));

        public bool Activate()
        {
            if (Palette is { } palette)
            {
                palette.Choose();
                return true;
            }

            // A text box is typed into with the on-screen keyboard.
            if (Keyboard.FocusedElement is TextBox box)
            {
                window.OpenPadKeyboard(box);
                return true;
            }

            return Keyboard.FocusedElement is UIElement element && Press(element);
        }

        public bool KeyboardOpen => window.PadKeyboard.IsOpen;

        public void KeyboardAction(PadKeyboardAction action) => window.PadKeyboard.Perform(action);

        public void CloseKeyboard() => window.PadKeyboard.Close();

        /// <summary>Opens the keyboard on the search palette's text box (the palette is no use without typing); false when the palette is not what is showing.</summary>
        public bool TypeIntoPalette()
        {
            if (window.PadKeyboard.IsOpen || !window.IsModalOpen || window._modals.Peek().Content is not CommandPaletteDialog dialog)
                return false;

            window.OpenPadKeyboard(dialog.QueryBox);
            return true;
        }

        /// <summary>Presses a control the way a click or Enter would, through its automation peer (buttons invoke, switches toggle, list rows select, drop-downs open).</summary>
        private static bool Press(UIElement element)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(element);

            // A list row (a Settings category) is chosen by pressing it. Its automation peer is not always there for a plain ListBoxItem, so it is done directly.
            if (element is ListBoxItem row)
            {
                row.IsSelected = true;
                return true;
            }

            if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            {
                invoke.Invoke();
                return true;
            }

            if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            {
                toggle.Toggle();
                return true;
            }

            if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider item)
            {
                item.Select();
                return true;
            }

            if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
            {
                if (expand.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Collapsed)
                    expand.Expand();
                else
                    expand.Collapse();

                return true;
            }

            if (element is ButtonBase)
            {
                element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return false;
        }

        public void FocusGameTile() => window.RibbonView.FocusFocusedTile();

        private IEnumerable<FrameworkElement> TopBar() =>
        [
            window.RibbonView.HomeTab, window.RibbonView.LibraryTab, window.RibbonView.SearchButton,
            window.RibbonView.ControllerButton, window.RibbonView.SettingsButton, window.RibbonView.PowerButton,
        ];

        public bool TopBarHasFocus => Keyboard.FocusedElement is FrameworkElement focused && TopBar().Any(c => ReferenceEquals(c, focused));

        public bool FocusTopBar()
        {
            var tab = window.Shell.Tab == ShellTab.Library ? (FrameworkElement)window.RibbonView.LibraryTab : window.RibbonView.HomeTab;
            if (!tab.IsVisible || !tab.IsEnabled)
                return false;

            Land(tab, Controls());
            return true;
        }

        public bool TryFocusGameTile() => window.RibbonView.TryFocusTile();

        public bool TryFocusLetterRail()
        {
            var letters = FocusNavigator.Reachable(window.RibbonView, window.Shell.FocusedGame, window.Shell.LibraryTileFocused)
                .Where(c => c is Button { DataContext: LetterItem }).ToList();
            if (letters.Count == 0)
                return false;

            var current = letters.FirstOrDefault(c => ((LetterItem)c.DataContext).IsCurrent) ?? letters[0];
            Land(current, Controls());
            return true;
        }

        public bool FocusPlay()
        {
            var play = window.RibbonView.HeroPlay;
            if (!play.IsVisible || !play.IsEnabled)
                return false;

            Land(play, Controls()); // scrolled into view too: on a short window Play is below the strip
            return true;
        }

        /// <summary>The Play, More, favorite star or Uninstall button on the home screen: the hero's row.</summary>
        public bool HeroHasFocus =>
            Keyboard.FocusedElement is { } focused
            && (ReferenceEquals(focused, window.RibbonView.HeroPlay) || ReferenceEquals(focused, window.RibbonView.HeroMore)
                || ReferenceEquals(focused, window.RibbonView.HeroFavorite) || ReferenceEquals(focused, window.RibbonView.HeroUninstall));

        public bool EnsureFocus()
        {
            if (Palette is not null)
                return false; // the palette keeps its own selection; it needs no keyboard focus to be driven

            var layer = ActiveLayer;
            if (Keyboard.FocusedElement is DependencyObject focused && focused is UIElement { IsVisible: true } && FocusNavigator.IsWithin(focused, layer))
                return false;

            // On the home the pad starts on the focused game's tile.
            if (ReferenceEquals(layer, window.RibbonView) && window.RibbonView.TryFocusTile())
                return true;

            // The keyboard starts on a letter near its middle.
            if (ReferenceEquals(layer, window.PadKeyboard) && window.PadKeyboard.DefaultKey is { } key)
            {
                Land(key, Controls());
                return true;
            }

            // Anywhere else it starts at the top left, but not on the page's Back button when there is something else to start on.
            var controls = Controls();
            if (FocusNavigator.FirstInReadingOrder(controls, window.RootGrid, c => c.Name == "PageBackButton" && controls.Count > 1) is not { } first)
                return false; // nothing to land on (an empty screen): the press carries on

            Land(first, controls);
            return true;
        }

        /// <summary>The command palette (the pad's X) keeps its selection in its view model, driven by keys: the pad drives that selection directly.</summary>
        private CommandPaletteViewModel? Palette =>
            !window.PadKeyboard.IsOpen && window.IsModalOpen && window._modals.Peek().Content is CommandPaletteDialog dialog ? dialog.Model : null;


        public void Scroll(int pages)
        {
            if (Palette is { } palette)
            {
                palette.MoveSelection(pages * 6);
                return;
            }

            ScrollUnderFocus(pages);
        }

        private void ScrollUnderFocus(int pages)
        {
            var start = Keyboard.FocusedElement as DependencyObject ?? ActiveLayer;
            for (var current = start; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is ScrollViewer { ScrollableHeight: > 0 } scroller)
                {
                    Behaviors.SmoothScroll.ToVertical(scroller, Behaviors.SmoothScroll.TargetOf(scroller) + pages * scroller.ViewportHeight * 0.9);
                    return;
                }
            }
        }

        public void CloseModal() => window.CloseTopModal();

        public Rect FocusedAnchor() =>
            Keyboard.FocusedElement is FrameworkElement focused ? ShellGeometry.RectOf(focused, window.RibbonView) : new Rect(0, 0, 0, 0);
    }
}
