using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
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

    /// <summary>Controls whose own keyboard-focus look was switched off while the pad is in use (the one ring is the only highlight); put back when the mode ends.</summary>
    private readonly List<WeakReference<Control>> _quietedFocusVisuals = new();
    private bool _controllerToastShown;
    private WindowState _stateBeforeController;
    private Rect _boundsBeforeController;
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
            var traced = library.IsControllerMode && !_router!.AskingToEnter;
            var screen = DescribeScreen(library);
            var from = traced ? DescribeControl(Keyboard.FocusedElement) : "";
            _router!.Handle(button);
            if (traced)
                TraceStep(screen, button, from);
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
        AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => QuietOwnFocusVisual(library, e.NewFocus)), true);
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => UpdateFocusRing()), true);
        LayoutUpdated += (_, _) => UpdateFocusRing();
        _pad.RightStick += (_, y, seconds) => ScrollWithStick(library, y, seconds);
        library.PropertyChanged += (_, e) =>
        {
            // A page that opens gets the pad at once; one that closes (a button on it did something: Stats' "Show them") hands the pad back to the screen under it, or the
            // focus would be left on a control that is gone and the next press would only show where it is.
            if (e.PropertyName == nameof(LibraryViewModel.IsPageOpen) && library.IsControllerMode)
                Dispatcher.BeginInvoke(() =>
                {
                    if (library.IsControllerMode && !IsModalOpen)
                        _surface!.EnsureFocus();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
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

        // A closed window must not keep hiding the pointer for the whole process (its timer would otherwise tick on).
        Closed += (_, _) =>
        {
            _cursorTimer.Stop();
            ShowCursor();
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

        // Empty controller slots are not asked about on a timer (on some PCs each question makes Windows flash its busy pointer). They are looked at again when Windows
        // says a device came or went, and when the launcher is brought to the front; the first reading and a once-a-minute net sit under those (see ControllerInput).
        var deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        deviceDebounce.Tick += (_, _) =>
        {
            deviceDebounce.Stop();
            Trail.Note("Windows reported a device change: looking in the controller slots again");
            _pad.RequestProbe();
        };
        SourceInitialized += (_, _) =>
        {
            const int wmDeviceChange = 0x0219, devNodesChanged = 0x0007, deviceArrival = 0x8000, deviceRemoveComplete = 0x8004;
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            {
                source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == wmDeviceChange && wParam.ToInt64() is devNodesChanged or deviceArrival or deviceRemoveComplete)
                    {
                        deviceDebounce.Stop(); // a plug usually brings several of these at once: one look, a moment after the last
                        deviceDebounce.Start();
                    }

                    return IntPtr.Zero;
                });
            }
        };
        Activated += (_, _) => _pad.RequestProbe();

        // Why does the loading circle show over the launcher, if it does? The watch (BusyCursorWatch) writes it to the log with what the launcher did just before.
        BusyCursorWatch? busyWatch = null;
        void ApplyBusyWatch()
        {
            if (library.BusyCursorLog && busyWatch is null && IsLoaded)
            {
                busyWatch = new BusyCursorWatch(Dispatcher, () => _inView, DescribePointer);
                busyWatch.Start();
            }
            else if (!library.BusyCursorLog && busyWatch is not null)
            {
                busyWatch.Dispose();
                busyWatch = null;
            }
        }

        void NoteInView() => _inView = IsVisible && WindowState != WindowState.Minimized;
        StateChanged += (_, _) => NoteInView();
        IsVisibleChanged += (_, _) => NoteInView();
        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.BusyCursorLog))
                ApplyBusyWatch();
        };

        Loaded += (_, _) =>
        {
            _pad.Start(Dispatcher);
            heartbeat.Start();
            NoteInView();
            ApplyBusyWatch();
            Logger.Info("Controller input started (the pad is only read while this window is in front).");
        };
        Closed += (_, _) =>
        {
            heartbeat.Stop();
            deviceDebounce.Stop();
            busyWatch?.Dispose();
            _pad.Dispose();
            _cursorTimer.Stop();
            ShowCursor();
        };
    }

    /// <summary>Read by the busy-pointer watch on the UI thread: the pointer WPF itself has asked for under the mouse (an override, or the element's own), if it is
    /// anything but the usual arrow.</summary>
    private static string DescribePointer()
    {
        if (Mouse.OverrideCursor is { } over)
            return "override " + over;

        return Mouse.DirectlyOver is FrameworkElement { Cursor: { } own } ? own.ToString() ?? "" : "";
    }

    private volatile bool _inView;

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

    /// <summary>The control and its place on screen, in words a person reading the navigation log can follow: "Button#HeroPlay[Play] @612,340 96x40".</summary>
    internal string DescribeControl(object? element)
    {
        if (element is not FrameworkElement control)
            return "nothing";

        var label = control switch
        {
            ContentControl { Content: string text } => text,
            _ => System.Windows.Automation.AutomationProperties.GetName(control) is { Length: > 0 } name ? name : control.ToolTip as string ?? "",
        };

        if (label.Length == 0)
        {
            label = control.DataContext switch
            {
                GameEntry game => game.Name,
                DriveSpaceInfo drive => drive.Letter,
                { } other => other.GetType().Name,
                _ => "",
            };
        }

        var box = FocusNavigator.BoundsIn(control, RootGrid);
        var where = box.IsEmpty ? "" : $" @{box.Left:0},{box.Top:0} {box.Width:0}x{box.Height:0}";
        return $"{control.GetType().Name}#{control.Name}[{(label.Length > 40 ? label[..40] : label)}]{where}";
    }

    /// <summary>The navigation trace (see <see cref="NavigationTrace"/>): one line per press in controller mode, and a report that says where the pad hit walls or was taken back.</summary>
    internal NavigationTrace NavTrace { get; } = new(Logger.LogDir);

    private void TraceStep(string screen, PadButton button, string from)
    {
        var rule = _router!.LastRule;
        var stick = _pad!.LastStick;
        var stickText = Math.Abs(stick.X) >= 8000 || Math.Abs(stick.Y) >= 8000 ? $"{stick.X},{stick.Y}" : null;

        // The focus settles a moment after the press (a tile takes it when the layout is done), so what it landed on is read then.
        Dispatcher.BeginInvoke(() =>
        {
            var to = DescribeControl(Keyboard.FocusedElement);
            NavTrace.IgnoredDiagonals = _pad.IgnoredDiagonals;
            NavTrace.Record(new NavStep(DateTime.Now, screen, button.ToString(), from, to, rule, to != from, stickText));
            if (NavTrace.Steps % 40 == 0)
                NavTrace.WriteSummary();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private string DescribeScreen(LibraryViewModel library) =>
        Shell.IsMenuOpen ? $"menu {Shell.MenuKind}" : IsModalOpen ? "dialog" : library.IsPageOpen ? "page" : Shell.NavOpen ? "control center" : $"{Shell.Tab} tab";

    /// <summary>A controller was found or went away (it was switched off, went to sleep, or was unplugged). The pill follows. When it goes away in controller mode the mode ends
    /// (nothing is left to drive it) and a toast says so; waking the controller and pressing Start comes back.</summary>
    internal void HandlePadConnection(LibraryViewModel library, bool connected)
    {
        Logger.Info(connected ? "A controller was found." : "The last controller went away.");
        var wasInMode = library.IsControllerMode;
        library.SetControllerStatus(connected, _pad?.Battery); // a controller that goes away takes controller mode with it

        if (wasInMode && !connected)
        {
            ShowCursor();
            _controllerToastShown = false; // when it wakes up, "Controller found - press Start" says how to come back
            Shell.ShowFeedback("Controller disconnected", "Controller mode is off. Wake the controller and press Start to come back");
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

            // Not Topmost: the mode is full screen, but it is an ordinary window, so Alt+Tab, the Windows key and other apps work; a window that covers the whole screen
            // while it is in front hides the taskbar by itself.
            Activate();
            Shell.ClearFeedback();
            Dispatcher.BeginInvoke(() => RibbonView.FocusFocusedTile(), DispatcherPriority.Loaded);
            _cursorTimer.Start();
        }
        else
        {
            RestoreOwnFocusVisuals();
            if (NavTrace.Steps > 0)
            {
                NavTrace.IgnoredDiagonals = _pad!.IgnoredDiagonals;
                NavTrace.WriteSummary();
                Logger.Info("Controller navigation: " + NavTrace.Summary().Split(Environment.NewLine)[0] + " (the report is controller-navigation-summary.txt in the logs folder).");
            }

            _cursorTimer.Stop();
            ShowCursor();
            PadKeyboard.Close(restoreFocus: false); // the keyboard is for the pad: leaving the mode puts it away
            UpdateFocusRing(); // the window answers the mouse again
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
        if (IsVisible && DataContext is LibraryViewModel { IsControllerMode: true, ControllerConnected: true })
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

    /// <summary>The pad's ring is the one highlight for the control it is on. Buttons and switches also draw a focus look of their own (a dotted or double outline, a hover
    /// tint), and the two together read as a smear: a ring round something that is also outlined, or two things lit at once. So while the pad is in use, the control's own
    /// focus look is switched off, and put back when the mode ends (a keyboard in Axis still needs it).</summary>
    internal void QuietOwnFocusVisual(LibraryViewModel library, IInputElement? focus)
    {
        if (!library.IsControllerMode || focus is not Control control || control.FocusVisualStyle is null)
            return;

        control.FocusVisualStyle = null;
        _quietedFocusVisuals.Add(new WeakReference<Control>(control));
    }

    private void RestoreOwnFocusVisuals()
    {
        foreach (var reference in _quietedFocusVisuals)
        {
            if (reference.TryGetTarget(out var control))
                control.ClearValue(FocusVisualStyleProperty);
        }

        _quietedFocusVisuals.Clear();
    }

    /// <summary>Draws the ring round the control the pad is on (or hides it). Several controls draw no focus of their own (the drive rows, the cards), and the ones that do each
    /// do it differently, so there is one ring for all of them. Game tiles are left alone: they highlight themselves.</summary>
    internal void UpdateFocusRing()
    {
        // While the pad is in use nothing under the (hidden) pointer may light up: a control the pointer was last over would keep its hover tint beside the pad's ring. The
        // mouse moving (NotePointerAt) makes everything live again, and a click follows a move, so the mouse is not lost.
        var padOnly = _padInUse && DataContext is LibraryViewModel { IsControllerMode: true };
        if (RootGrid.IsHitTestVisible == padOnly)
            RootGrid.IsHitTestVisible = !padOnly;

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

        // A control that draws its own ring on keyboard focus (the PlayStation buttons, tabs and cards) is highlighted already: a second ring over it was the smear of two
        // outlines. The overlay is for the controls that draw nothing of their own (the Settings switches and buttons, the drive rows).
        if (HasOwnFocusRing(focused) && OwnRingIsLit(focused))
            return false;

        target = focused;
        return true;
    }

    /// <summary>The control's own ring is showing right now. A template can have the trigger and still not show the ring (a hover animation that was left holding the ring at
    /// zero keeps a later focus trigger from lighting it), and then the overlay has to mark the control or the pad has no visible place.</summary>
    internal static bool OwnRingIsLit(FrameworkElement control)
    {
        if (control is not Control { Template: { } template } c)
            return false;

        foreach (var name in new[] { "Ring", "SelectedRing" })
        {
            if (template.FindName(name, c) is FrameworkElement { Visibility: Visibility.Visible, Opacity: > 0.5 })
                return true;
        }

        return false;
    }

    /// <summary>The control's template lights a ring of its own when it takes keyboard focus: a trigger on IsKeyboardFocused that sets a part called "Ring" (or "SelectedRing" on
    /// the tiles). Looked for in the triggers, not by name alone: another library's template may have a part of that name that does nothing on focus.</summary>
    internal static bool HasOwnFocusRing(FrameworkElement control) =>
        control is Control { Template: { } template }
        && template.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusedProperty
            && t.Setters.OfType<Setter>().Any(setter => setter.TargetName is "Ring" or "SelectedRing"));

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
        RibbonView.FollowRailAfterScroll();
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

        public string? FocusedCollection =>
            Keyboard.FocusedElement is FrameworkElement { DataContext: CollectionItem collection } focused && focused is RadioButton ? collection.Name : null;

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

            // A list of choices (the Settings categories) is not chosen by moving over it: the row is only marked by the pad, and A (or Right) chooses it and drops the pad
            // on the first option of what it shows (see EnterChoice).
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

            // Right from a row of a list of choices chooses that row and goes into what it shows.
            if (current is ListBoxItem chosenRow && direction == ShellDirection.Right && ListOf(chosenRow) is not null && EnterChoice(chosenRow))
                return true;

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

        /// <summary>Chooses a row of a list of choices (a Settings category) and drops the pad on the first option of what it now shows: the one nearest the top that is
        /// beside the list and on screen. False when there is nothing beside the list to go to (the row is still chosen).</summary>
        private bool EnterChoice(ListBoxItem row)
        {
            row.IsSelected = true;
            window.UpdateLayout();
            var controls = Controls();
            var edge = FocusNavigator.BoundsIn(row, window.RootGrid).Right;
            var beside = controls.Where(c => c is not ListBoxItem && FocusNavigator.BoundsIn(c, window.RootGrid).Left >= edge - 1).ToList();
            var onScreen = beside.Where(c => FocusNavigator.IsInView(c, window.RootGrid)).ToList();
            var first = FocusNavigator.FirstInReadingOrder(onScreen.Count > 0 ? onScreen : beside, window.RootGrid);
            if (first is null)
                return true;

            Land(first, controls);
            return true;
        }

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
                    // The first option that is on screen: with the options scrolled down (the right stick) the very first one is out of sight, and landing there threw the page
                    // back to the top.
                    var onScreen = beside.Where(c => FocusNavigator.IsInView(c, window.RootGrid)).ToList();
                    return FocusNavigator.FirstInReadingOrder(onScreen.Count > 0 ? onScreen : beside, window.RootGrid);

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

            // From a card in the first column of the Library grid, Left is the filters (the chosen one), whatever row the card is on.
            if (direction == ShellDirection.Left && FocusNavigator.IsGameTile(current) && window.Shell.Tab == ShellTab.Library
                && FocusNavigator.NextAcrossPanels(current, controls, direction, window.RootGrid) is { } filters)
                return filters;

            var target = FocusNavigator.Next(current, usable, direction, window.RootGrid);

            // A control level with this one but scrolled out of sight is not "the next one across" - the card of a grid that has been scrolled away from the filters is
            // the case - so it counts as nothing there.
            if (target is not null && direction is ShellDirection.Left or ShellDirection.Right && !FocusNavigator.IsInView(target, window.RootGrid))
                target = null;

            // Nothing level in this row: across to the scroll area beside this one (the Library's filters from a card at the bottom of the grid, and back), wherever the
            // grid has been scrolled to.
            if (target is null && direction is ShellDirection.Left or ShellDirection.Right)
            {
                target = FocusNavigator.NextAcrossPanels(current, controls, direction, window.RootGrid);

                // The grid's other cards are not controls of their own (only the focused game's card is), so with that card scrolled out of sight there is nothing to cross
                // to: the pad lands on the card on screen nearest in height instead, and that game becomes the focused one.
                if (target is null && direction == ShellDirection.Right && window.Shell.Tab == ShellTab.Library && window.DataContext is not LibraryViewModel { IsPageOpen: true } && !FocusNavigator.IsGameTile(current))
                    target = window.RibbonView.VisibleLibraryTile(FocusNavigator.BoundsIn(current, window.RootGrid));
            }

            // Left from any option - whatever lies level to its left - goes back to the list of choices beside the page, to its chosen row.
            if (direction == ShellDirection.Left && current is not ListBoxItem)
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

            // A on a row of a list of choices (a Settings category): choose it and drop the pad on its first option.
            if (Keyboard.FocusedElement is ListBoxItem row && ListOf(row) is not null)
                return EnterChoice(row);

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
            window.RibbonView.HomeTab, window.RibbonView.LibraryTab, window.RibbonView.UpdateButton, window.RibbonView.SearchButton,
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

            // A page with a list of choices that is already on one (Settings opened on a category by a shortcut, the palette or the Optimize tile) starts the pad on that
            // choice: starting at the top of the list would mark General while the options of another category are showing.
            var controls = Controls();
            if (controls.OfType<ListBoxItem>().Where(r => r.IsSelected).OrderBy(r => FocusNavigator.BoundsIn(r, window.RootGrid).Top).FirstOrDefault() is { } chosen)
            {
                Land(chosen, controls);
                return true;
            }

            // Anywhere else it starts at the top left, but not on the page's Back button when there is something else to start on.
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
