namespace GameLauncher.Services.SessionTracking;

/// <summary>GameWindowTracker's timing tunables, pulled out the same way GameSessionWatcherOptions'
/// process-side ones are - so tests can run the same state machine with millisecond-scale values (paired
/// with a fake TimeProvider) instead of actually waiting the real-world durations production uses.</summary>
public sealed record GameWindowTrackerOptions(TimeSpan WindowPollInterval, TimeSpan StabilizationPeriod, TimeSpan ReplacementDebounce)
{
    public static GameWindowTrackerOptions Default { get; } = new(
        // Windows does offer an async "tell me when a window is destroyed" notification
        // (SetWinEventHook with EVENT_OBJECT_DESTROY) - polling a raw HWND isn't the only option. This
        // uses polling anyway, as a deliberate simplicity choice: it reuses the exact Task.Delay/
        // TimeProvider pattern GameSessionWatcher's process-side tracking already relies on (so this
        // stays trivially testable with a fake clock, no message-pump/thread-affinity/hook-lifetime
        // concerns to manage), at a cost (worst-case ~300ms of extra latency noticing a destruction)
        // small enough not to matter next to the stabilization/debounce periods below. 300ms keeps
        // restoration feeling close to instant without spending meaningfully more CPU than the existing
        // 1-2s process-side polling intervals already do.
        WindowPollInterval: TimeSpan.FromMilliseconds(300),

        // How long a candidate must hold the OS foreground continuously before GameWindowTracker arms it
        // as the trusted gameplay window - but ONLY once it's already cleared the process-ownership bar
        // (see GameWindowTracker's own remarks on baselineProcessId): this alone was proven insufficient
        // by a real two-stage bootstrap (a splash screen, then a separate launcher window from the SAME
        // process, holding the foreground for several seconds before the real game ever appeared) -
        // window ORDER doesn't establish gameplay ownership, only a genuinely different owning process
        // does. There is deliberately no longer-timeout fallback for a candidate that never clears that
        // bar: no fixed duration is an actual guarantee that a still-unproven candidate isn't a launcher
        // (a slow download/update screen can sit in the foreground indefinitely) - GameSessionWatcher's
        // process-exit tracking is the correct, safe answer for that ambiguous case, not a bigger number
        // here.
        StabilizationPeriod: TimeSpan.FromSeconds(1.5),

        // How long to wait for a replacement gameplay window after an ARMED one is destroyed before
        // concluding the session is genuinely over - exists for the same reason HandoffGracePeriod does
        // on the process side: a resolution/fullscreen-mode switch can destroy and recreate a game's
        // actual render window (most engines resize/restyle the same HWND instead, but not all), and a
        // genuine handoff can briefly leave no gameplay window while a new one spins up. Deliberately NOT
        // applied before a candidate is armed - see StabilizationPeriod above for that earlier gate - so
        // a splash/launcher window closing during discovery returns to discovery immediately rather than
        // waiting out this debounce for nothing. Unlike arming, a replacement candidate is never required
        // to hold the foreground (see GameWindowTracker's WaitForReplacementAsync remarks - the player may
        // well have Alt-Tabbed away at the exact moment a fullscreen switch recreates the game's window),
        // so identity history (never-seen-before, not foreground) is what protects against a stale window
        // being mistaken for the game continuing instead.
        ReplacementDebounce: TimeSpan.FromSeconds(1.5));
}
