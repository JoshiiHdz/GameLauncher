using System.Windows;
using System.Windows.Media;

namespace GameLauncher.Tests.TestSupport;

/// <summary>
/// Shows one dialog control in a plain, off-screen window, closing the window when the control asks to be closed - the same contract
/// the main window's dialog layer offers. For tests that only need a dialog's own bindings and buttons; anything about the dialog
/// layer itself (sizing, Esc, the scrim) is tested through the real main window instead (<see cref="ShellTestSupport"/>).
/// </summary>
internal sealed class DialogTestHost<T> : Window where T : FrameworkElement
{
    public DialogTestHost(T dialog, double width, double height)
    {
        Dialog = dialog;
        Content = dialog;
        Width = width;
        Height = height;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -5000;
        Top = -5000;
        ShowActivated = false;
        ShowInTaskbar = false;
        AddHandler(AppShell.CloseRequestedEvent, new RoutedEventHandler((_, e) =>
        {
            e.Handled = true;
            Close();
        }));
        Closed += (_, _) => AppShell.NotifyClosed(dialog);
    }

    public T Dialog { get; }
}
