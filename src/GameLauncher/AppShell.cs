using System.Windows;

namespace GameLauncher;

/// <summary>Whatever hosts in-app dialogs (the main window). Kept as an interface so the shell has no dependency on MainWindow.</summary>
internal interface IModalHost
{
    /// <summary>Shows the content on a dimmed layer inside the window and blocks until it is closed. Returns the result the content
    /// set through <see cref="AppShell.SetResult"/> (null when it was dismissed with Esc or the close button).</summary>
    bool? ShowModal(string title, FrameworkElement content);
}

/// <summary>
/// How pages and dialogs talk to the window that hosts them. Pages (Settings, Stats, ...) and dialogs (Collections, Feedback, ...)
/// are plain UserControls: to close themselves they raise <see cref="CloseRequestedEvent"/>, which bubbles up to whichever host
/// is showing them, so a control never needs to know whether it sits on a page or in a dialog.
/// </summary>
public static class AppShell
{
    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        "CloseRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(AppShell));

    /// <summary>Raised directly on a dialog control the moment its host has taken it down - by its own Close/Cancel, Esc, or the card's
    /// close button alike - so it can stop whatever it still has running.</summary>
    public static readonly RoutedEvent DialogClosedEvent = EventManager.RegisterRoutedEvent(
        "DialogClosed", RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(AppShell));

    public static readonly DependencyProperty ResultProperty = DependencyProperty.RegisterAttached(
        "Result", typeof(bool?), typeof(AppShell), new PropertyMetadata(null));

    /// <summary>A dialog that draws its own card: the window adds no title bar or close button, and places it in the upper-middle.</summary>
    public static readonly DependencyProperty ChromelessProperty = DependencyProperty.RegisterAttached(
        "Chromeless", typeof(bool), typeof(AppShell), new PropertyMetadata(false));

    /// <summary>A dialog that closes when the dimmed area around it is clicked (the command palette). Others ignore stray clicks.</summary>
    public static readonly DependencyProperty LightDismissProperty = DependencyProperty.RegisterAttached(
        "LightDismiss", typeof(bool), typeof(AppShell), new PropertyMetadata(false));

    public static bool GetChromeless(DependencyObject element) => (bool)element.GetValue(ChromelessProperty);

    public static void SetChromeless(DependencyObject element, bool value) => element.SetValue(ChromelessProperty, value);

    public static bool GetLightDismiss(DependencyObject element) => (bool)element.GetValue(LightDismissProperty);

    public static void SetLightDismiss(DependencyObject element, bool value) => element.SetValue(LightDismissProperty, value);

    public static bool? GetResult(DependencyObject element) => (bool?)element.GetValue(ResultProperty);

    public static void SetResult(DependencyObject element, bool? value) => element.SetValue(ResultProperty, value);

    /// <summary>Tells a dialog control that it has been closed. Called by the host, once, as it removes the control.</summary>
    internal static void NotifyClosed(FrameworkElement content) =>
        content.RaiseEvent(new RoutedEventArgs(DialogClosedEvent, content));

    /// <summary>The window currently able to show dialogs; null in tests that never create one.</summary>
    internal static IModalHost? Host { get; set; }

    /// <summary>Asks whichever host shows <paramref name="source"/> to close it, optionally recording the dialog's result first.</summary>
    public static void RequestClose(FrameworkElement source, bool? result = null)
    {
        if (result.HasValue)
            SetResult(source, result);

        source.RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, source));
    }

    /// <summary>Shows a dialog card inside the main window and waits for it to close. Throws when there is no window to show it in.</summary>
    internal static bool? ShowModal(string title, FrameworkElement content) =>
        (Host ?? throw new InvalidOperationException("There is no launcher window to show this dialog in.")).ShowModal(title, content);

    /// <summary>What a question gets when there is no launcher window to ask it in: (title, question, warning) -> answer. Null means a
    /// plain Windows message box. The tests set it to "No", so a question nobody planned for can never leave a test waiting for a click.</summary>
    internal static Func<string, string, bool, bool>? AnswerWithNoWindow { get; set; }

    /// <summary>A yes/no question in the launcher's own style. No is the default, so Enter never confirms something destructive.
    /// Falls back to a plain Windows message box only when there is no launcher window.</summary>
    internal static bool Confirm(string title, string question, string yes = "Yes", string no = "No", bool warning = false)
    {
        if (Host is null)
        {
            if (AnswerWithNoWindow is { } answer)
                return answer(title, question, warning);

            return MessageBox.Show(question, title, MessageBoxButton.YesNo,
                warning ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        var dialog = new ConfirmDialog(question, yes, no, warning);
        return Host.ShowModal(title, dialog) == true;
    }
}
