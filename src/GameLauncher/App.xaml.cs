using System.Windows;
using System.Windows.Threading;
using GameLauncher.Services;

namespace GameLauncher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled exception on the UI thread - the app will now close.", e.Exception);
        MessageBox.Show(
            $"Game Launcher hit an unexpected error and needs to close.\n\nDetails were saved to:\n{Logger.CurrentLogPath}",
            "Game Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        // Leave e.Handled false: the exception already logged, now let the process end normally
        // rather than pretend the app is still in a known-good state.
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled exception on a background thread.", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved exception from a fire-and-forget task.", e.Exception);
        e.SetObserved();
    }
}
