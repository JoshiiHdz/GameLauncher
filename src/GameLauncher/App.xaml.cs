using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Services;
using Wpf.Ui.Appearance;

namespace GameLauncher;

public partial class App : Application
{
    // Sampled from the app icon itself (the dominant fill color across its red/orange artwork)
    // so every accented control - primary buttons, focus rings, the sort dropdown's selection -
    // matches the logo instead of WPF-UI's default Windows blue.
    private static readonly Color BrandAccent = Color.FromRgb(0xE2, 0x23, 0x1A);

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // Generated Main() calls InitializeComponent() - which merges App.xaml's static
    // ui:ThemesDictionary - right after the constructor returns but before Run()/OnStartup. Applying
    // the theme/accent in the constructor got silently overwritten by that later merge (the app
    // stayed a plain, un-accented dark instead of picking up the logo's red). OnStartup runs after
    // InitializeComponent, so this is the first point our own theme actually sticks.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // App.xaml's ui:ThemesDictionary only sets the *resource* theme (colors/brushes). The
        // Acrylic backdrop applied to the window is a separate DWM system material that WPF-UI
        // tints by reading the current Windows light/dark setting unless told otherwise - so on a
        // Windows Light PC the glass came out light-tinted behind our dark chrome and text.
        // Applying the theme here pins both to the same value regardless of the OS setting.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Dark);
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
