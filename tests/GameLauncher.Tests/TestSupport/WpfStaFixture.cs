using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace GameLauncher.Tests.TestSupport;

/// <summary>
/// A dedicated STA thread with a running Dispatcher, for tests that need to construct real WPF elements
/// (Window, ItemsControl, ContextMenu/Popup) - none of which tolerate being touched from a plain
/// ThreadPool thread. Shared across every test in a class via IClassFixture, rather than spun up fresh per
/// test: System.Windows.Application enforces "created at most once per process," so a fresh Application
/// per test method would throw on the second test.
///
/// Not a general-purpose WPF test framework - just enough to let xUnit's plain [Fact]/async Task tests
/// drive real WPF objects synchronously via Invoke/InvokeAsync, with the dispatcher pumped for the
/// duration of each call so layout, binding evaluation, and Popup opening actually complete before the
/// test's assertions run.
/// </summary>
public sealed class WpfStaFixture : IDisposable
{
    private readonly Thread _thread;

    public Dispatcher Dispatcher { get; }

    public WpfStaFixture()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            // Pack URIs (ResourceDictionary Source="...") and every FrameworkElement's default style
            // lookup require a live Application instance to resolve against - a bare Dispatcher thread
            // alone is not enough. ShutdownMode.OnExplicitShutdown: this Application must NOT shut down
            // just because it (momentarily) has no visible windows between two tests.
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                // Merges the same two dictionaries App.xaml itself declares (ui:ThemesDictionary +
                // ui:ControlsDictionary) - without these, ui:Button/ui:TitleBar/ContextMenu/MenuItem
                // fall back to bare, unstyled defaults, which would make a visual inspection of
                // ChangeCoverWindow meaningless (it wouldn't look anything like what the app actually
                // ships). Deliberately NOT constructing the real GameLauncher.App class itself: its
                // constructor wires DispatcherUnhandledException to a blocking MessageBox.Show, which
                // would hang an automated, headless test run if anything under test ever threw
                // unexpectedly - exactly the failure mode a test run needs to report, not freeze on.
                app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new ControlsDictionary());
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        ready.Wait();
        Dispatcher = dispatcher!;
    }

    /// <summary>Runs `body` to completion on the STA thread, pumping the dispatcher for the duration so
    /// any genuine async continuations inside it (a Task.Run-based decode, an awaited command) actually
    /// get to run rather than deadlocking against the very thread they need to resume on.</summary>
    public void RunAsync(Func<Task> body)
    {
        Exception? error = null;

        Dispatcher.Invoke(() =>
        {
            var frame = new DispatcherFrame();

            async Task RunAndSignal()
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            }

            _ = RunAndSignal();
            Dispatcher.PushFrame(frame);
        });

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
