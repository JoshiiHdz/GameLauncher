using System.Windows;
using System.Windows.Threading;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.TestSupport;

/// <summary>A dialog shown inside the main window the way the app shows it. Closing it ends the blocking call, so tests always close
/// what they open.</summary>
internal sealed class ShownDialog(FrameworkElement content, DispatcherOperation<bool?> operation)
{
    public FrameworkElement Content { get; } = content;

    /// <summary>Completes when the dialog has closed, with the result it recorded (null when dismissed).</summary>
    public DispatcherOperation<bool?> Operation { get; } = operation;

    /// <summary>Closes the dialog as its own Close/Cancel button would.</summary>
    public async Task CloseAsync()
    {
        AppShell.RequestClose(Content);
        await ShellTestSupport.SettleAsync();
    }
}

/// <summary>Opening the real main window off-screen and showing pages and dialogs in it - what the old tests did by constructing each
/// window directly.</summary>
internal static class ShellTestSupport
{
    public static MainWindow OpenMain(LibraryViewModel vm, double width = 1100, double height = 760)
    {
        var window = new MainWindow(vm, startRuntimeServices: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -5000, Top = -5000, Width = width, Height = height,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    public static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Waits for a dialog opened by the code under test (a confirmation, say) to be on screen.</summary>
    public static async Task WaitForModalAsync(MainWindow window)
    {
        for (var i = 0; i < 60 && !window.IsModalOpen; i++)
            await Dispatcher.Yield(DispatcherPriority.Background);

        await SettleAsync();
        window.UpdateLayout();
        if (!window.IsModalOpen)
            throw new InvalidOperationException("No dialog opened.");
    }

    /// <summary>Shows the dialog on the window's dialog layer, as <c>AppShell.ShowModal</c> does, and returns once it is on screen.</summary>
    public static async Task<ShownDialog> ShowDialogAsync(MainWindow window, string title, FrameworkElement dialog)
    {
        await SettleAsync(); // the window's Loaded work (it registers itself as the dialog host) has run
        var operation = window.Dispatcher.InvokeAsync(() => AppShell.ShowModal(title, dialog));
        for (var i = 0; i < 40 && !window.IsModalOpen; i++)
            await Dispatcher.Yield(DispatcherPriority.Background);

        await SettleAsync();
        window.UpdateLayout();
        if (!window.IsModalOpen)
            throw new InvalidOperationException("The dialog did not open.");

        return new ShownDialog(dialog, operation);
    }
}
