using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameLauncher.Behaviors;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>
/// The window's part of the in-app shell: dialogs shown on a dimmed layer inside the window (<see cref="IModalHost"/>), and the keys that
/// close them or the open page. Pages themselves need no code here - the window just shows <c>LibraryViewModel.CurrentPage</c>.
/// </summary>
public partial class MainWindow : IModalHost
{
    private sealed record ModalEntry(FrameworkElement Content, string Title, DispatcherFrame Frame, IInputElement? PreviousFocus);

    private readonly Stack<ModalEntry> _modals = new();
    private Brush? _cardBackground;
    private Thickness _cardBorder;
    private Effect? _cardEffect;

    /// <summary>True while at least one dialog is showing.</summary>
    internal bool IsModalOpen => _modals.Count > 0;

    /// <summary>Wires the dialog layer and the page host to the shell. Called once, before the window's own Loaded work, so a dialog can
    /// be shown from the very first thing that runs after the window opens (the "What's new" card).</summary>
    private void InitializeShell()
    {
        Loaded += (_, _) => AppShell.Host = this;
        Closing += (_, _) => CloseAllModals();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(AppShell.Host, this))
                AppShell.Host = null;
        };
        SizeChanged += (_, _) => FitModalToWindow();

        ModalLayer.AddHandler(AppShell.CloseRequestedEvent, new RoutedEventHandler((_, e) =>
        {
            e.Handled = true;
            CloseTopModal();
        }));
        PageHost.AddHandler(AppShell.CloseRequestedEvent, new RoutedEventHandler((_, e) =>
        {
            e.Handled = true;
            (DataContext as LibraryViewModel)?.ClosePage();
        }));
    }

    public bool? ShowModal(string title, FrameworkElement content)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("The launcher window is not showing, so there is nowhere to show this dialog.");

        var entry = new ModalEntry(content, title, new DispatcherFrame(), Keyboard.FocusedElement);
        AppShell.SetResult(content, null);
        _modals.Push(entry);
        PresentTopModal(animate: true);

        // The calling command carries on exactly as it did with a dialog window: it waits here until the dialog closes, then reads
        // the answer back off the dialog's own view model. Everything else in the window keeps running while it waits.
        Dispatcher.PushFrame(entry.Frame);
        return AppShell.GetResult(content);
    }

    private void PresentTopModal(bool animate)
    {
        _cardBackground ??= ModalCard.Background;
        _cardBorder = _cardBorder == default ? ModalCard.BorderThickness : _cardBorder;
        _cardEffect ??= ModalCard.Effect;

        if (_modals.Count == 0)
        {
            ModalLayer.Visibility = Visibility.Collapsed;
            ModalContent.Content = null;
            SearchBox.IsEnabled = true;
            return;
        }

        var top = _modals.Peek();
        var bare = AppShell.GetChromeless(top.Content);

        // A dialog that draws its own card (the command palette) gets no title bar, border or shadow from the window, and sits high up.
        ModalHeader.Visibility = bare ? Visibility.Collapsed : Visibility.Visible;
        ModalTitle.Text = top.Title;
        ModalCard.Background = bare ? Brushes.Transparent : _cardBackground;
        ModalCard.BorderThickness = bare ? new Thickness(0) : _cardBorder;
        ModalCard.Effect = bare ? null : _cardEffect;
        ModalCard.VerticalAlignment = bare ? VerticalAlignment.Top : VerticalAlignment.Center;
        ModalCard.Margin = bare ? new Thickness(24, Math.Max(24, (RootGrid.ActualHeight - WindowDragBar.ActualHeight) * 0.14), 24, 24) : new Thickness(24);

        ModalContent.Content = top.Content;
        FitModalToWindow();
        ModalLayer.Visibility = Visibility.Visible;
        SearchBox.IsEnabled = false;

        if (animate && !bare)
            Motion.PlayFade(ModalCard);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            // A dialog that already put focus where it wants it (its own Loaded handler) keeps it.
            if (_modals.Count > 0 && !_modals.Peek().Content.IsKeyboardFocusWithin)
                _modals.Peek().Content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    /// <summary>Keeps the open dialog inside the window: a dialog with a fixed size (Identify Game) shrinks to fit a small window
    /// instead of running off its edges.</summary>
    private void FitModalToWindow()
    {
        if (_modals.Count == 0)
            return;

        var content = _modals.Peek().Content;
        var bare = AppShell.GetChromeless(content);
        var headerHeight = bare ? 0 : 52;

        // Measured from the window's own grid, not from the layer: the layer has no size yet the first time it is shown.
        var layerWidth = RootGrid.ActualWidth;
        var layerHeight = Math.Max(0, RootGrid.ActualHeight - WindowDragBar.ActualHeight);
        var available = Math.Max(200, layerHeight - 48 - headerHeight);
        content.MaxWidth = Math.Max(240, layerWidth - 48);

        if (bare)
        {
            content.MaxHeight = Math.Max(200, layerHeight * 0.7);
            ModalScroll.MaxHeight = double.PositiveInfinity;
        }
        else if (double.IsNaN(content.Height))
        {
            // Sized by what it holds (Feedback, a confirmation): too tall for the window means the card scrolls.
            content.MaxHeight = double.PositiveInfinity;
            ModalScroll.MaxHeight = available;
        }
        else
        {
            // A fixed-size dialog with its own lists and scrolling (Identify Game, Collections): it shrinks to fit.
            content.MaxHeight = available;
            ModalScroll.MaxHeight = double.PositiveInfinity;
        }
    }

    private void CloseTopModal()
    {
        if (_modals.Count == 0)
            return;

        var entry = _modals.Pop();
        entry.Frame.Continue = false;
        PresentTopModal(animate: false);
        AppShell.NotifyClosed(entry.Content);

        if (_modals.Count == 0 && entry.PreviousFocus is UIElement { IsVisible: true } previous)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(previous));
    }

    private void CloseAllModals()
    {
        while (_modals.Count > 0)
            CloseTopModal();
    }

    private void ModalClose_Click(object sender, RoutedEventArgs e) => CloseTopModal();

    private void ModalScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_modals.Count > 0 && AppShell.GetLightDismiss(_modals.Peek().Content))
        {
            e.Handled = true;
            CloseTopModal();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Behind a dialog the shortcuts that would open another one stay quiet.
        if (IsModalOpen && (e.Key == Key.F11 || (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Esc is the way out of whatever is on top: a dialog first, otherwise the open page. A control that wants Esc for itself (an
        // open dropdown) handles it first, and then it never gets here.
        if (e.Handled || e.Key != Key.Escape)
            return;

        if (IsModalOpen)
        {
            CloseTopModal();
            e.Handled = true;
        }
        else if (DataContext is LibraryViewModel { IsPageOpen: true } vm)
        {
            vm.ClosePage();
            e.Handled = true;
        }
    }
}
