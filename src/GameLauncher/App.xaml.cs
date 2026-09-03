using System.Threading;
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

    // Fixed, randomly-generated names so they can never collide with some other app's mutex/event -
    // deliberately not "Global\" prefixed since this only ever needs to matter within one user's own
    // desktop session, and the Global\ namespace has its own privilege requirements under Terminal
    // Services that a plain desktop app has no reason to take on.
    private const string SingleInstanceMutexName = "GameLauncher-SingleInstance-B2E1F4A6-9C3D-4B8E-8F1A-2D5E7C9A4B3F";
    private const string ShowRequestedEventName = "GameLauncher-ShowRequest-B2E1F4A6-9C3D-4B8E-8F1A-2D5E7C9A4B3F";

    // Held for the app's entire lifetime purely so the OS doesn't reclaim it early - ownership is
    // never released/acquired again after the constructor; its only job is to exist so a second
    // launch's identically-named Mutex constructor call comes back with createdNew: false.
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showRequestedEvent;

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
        // Checked before base.OnStartup() runs - that call is what actually creates and shows the
        // StartupUri window (App.xaml's MainWindow.xaml), so a duplicate launch has to bail out
        // before it, not after, or a second window would flash open even briefly.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance of Game Launcher is already running - bringing it to the front instead of opening a second one.");
            SignalExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // App.xaml's ui:ThemesDictionary only sets the *resource* theme (colors/brushes). The
        // Acrylic backdrop applied to the window is a separate DWM system material that WPF-UI
        // tints by reading the current Windows light/dark setting unless told otherwise - so on a
        // Windows Light PC the glass came out light-tinted behind our dark chrome and text.
        // Applying the theme here pins both to the same value regardless of the OS setting.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        ApplicationAccentColorManager.Apply(BrandAccent, ApplicationTheme.Dark);

        StartShowRequestListener();
    }

    /// <summary>Wakes up the real instance's StartShowRequestListener loop. Best-effort: if the
    /// event can't be opened (the real instance is somehow mid-shutdown, or hasn't created it yet
    /// in some unlikely race), this instance still exits via Shutdown() either way - never leaving a
    /// duplicate process running is the priority, a missed foreground-restore is a minor miss.</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var existingShowEvent = EventWaitHandle.OpenExisting(ShowRequestedEventName);
            existingShowEvent.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            Logger.Warn("Couldn't signal the already-running instance to come to the foreground.", ex);
        }
    }

    /// <summary>Runs only in the one real instance. A background thread parked on WaitOne() rather
    /// than a timer/poll - this fires the instant a second launch is attempted instead of up to a
    /// poll interval later, and costs nothing while idle (blocked in the kernel, not spinning).</summary>
    private void StartShowRequestListener()
    {
        _showRequestedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestedEventName);

        var thread = new Thread(() =>
        {
            while (true)
            {
                _showRequestedEvent.WaitOne();
                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is MainWindow mainWindow)
                        mainWindow.RestoreFromTray();
                });
            }
        })
        {
            IsBackground = true, // never keeps the process alive on its own at shutdown
            Name = "SingleInstanceListener",
        };
        thread.Start();
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
