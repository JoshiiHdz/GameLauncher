using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

/// <summary>
/// Tracks a game's gameplay window across its process lifecycle, as GameSessionWatcher's PREFERRED exit
/// signal. A real gaming-PC log showed the visible game window close while a background process (cloud
/// sync, a crash reporter, an anti-cheat service) lingered roughly 20 more seconds - GameSessionWatcher's
/// process-exit tracking correctly waited for that straggler, but from the player's perspective the
/// launcher just sat hidden for 20 seconds after they'd already closed the game. Restoring as soon as the
/// window the player was actually looking at is destroyed - and nothing replaces it within a short
/// debounce - matches what the player experiences far better than waiting on every last process.
///
/// A candidate top-level window (IGameWindowProvider.FindGameplayWindows) only proves "looks like a real
/// app window" - a launcher or splash screen routinely passes the same heuristic a real game window does.
/// Window ORDER alone cannot establish gameplay ownership either: a real two-stage bootstrap (a splash
/// screen, followed by a SEPARATE launcher window from the same still-running process, holding the
/// foreground for several seconds before the actual game ever appears) defeats any rule based on "is this
/// the first window observed" - the second stage is just as much a launcher as the first, no matter how
/// long it sits there. What DOES meaningfully change between a launcher's own screens and the real game is
/// the OWNING PROCESS - a genuine gameplay stage is, in every case this was designed from, a different
/// process than whichever one produced the very first candidate window this watch ever observed. So a
/// candidate is only OBSERVED - tentatively, provisionally - once it belongs to a process other than that
/// original "baseline" one, and only becomes ARMED (the window whose destruction this method will
/// actually act on) once it has additionally held the OS foreground continuously for StabilizationPeriod.
/// A candidate whose process never changes from the baseline is NEVER observed at all, no matter how long
/// any of its windows sit in the foreground - there is deliberately no "it's been so long it must be real"
/// fallback: no fixed duration is an actual guarantee against a launcher (a stuck download/update screen
/// can sit in the foreground indefinitely).
///
/// KNOWN, UNRESOLVED LIMITATION: this only ever excludes the ONE original baseline process. A bootstrap
/// chain of three or more genuinely separate processes (a standalone splash.exe, then a SEPARATE
/// launcher.exe, then game.exe) is not caught - the second, still-not-real process differs from the
/// baseline just as much as the real game does, so it can still satisfy StabilizationPeriod and arm
/// prematurely. This is not a case this design safely defers to process-exit tracking: GameSessionWatcher
/// races this signal against its own, and a premature true from here wins that race and restores while
/// gameplay is still starting - racing does not rescue a false positive. Closing this gap needs
/// corroboration this tracker does not have access to (e.g. GameSessionWatcher's own process-chain
/// confirmation state); see GameWindowTrackerTests for the tracked, currently-skipped regression.
///
/// SEPARATE, ACCEPTED tradeoff (not a bug): a game with no separate launcher stage at all - the very
/// first candidate this watch ever observes genuinely IS the real game - never arms via this signal for
/// its entire session, since the first candidate is always the baseline. GameSessionWatcher's process-exit
/// tracking is the only signal for those launches; this design does not attempt to recover the faster
/// restoration for them.
///
/// Every candidate window seen WHILE NOTHING IS CURRENTLY ARMED - selected or not, foreground or not - is
/// recorded into a running identity history (see everSeenIdentities) purely so WaitForReplacementAsync can
/// recognize a stale, merely-still-open window (a launcher that never closed, and was never even selected
/// because it never took the foreground) and refuse to treat it as a genuine replacement later. Recording
/// is deliberately withheld while something IS armed and alive: a window first appearing during that
/// tenure needs to remain eligible in case it turns out to be the very replacement for that armed window
/// once it's destroyed - being observed once, incidentally, while something else already held trust is not
/// by itself proof of being stale clutter the way being observed BEFORE anything ever armed is. See
/// WaitForGameplayWindowExitAsync's own remarks for the residual ambiguity this narrower rule still leaves
/// open.
///
/// This never itself concludes "still running": WaitForGameplayWindowExitAsync only ever completes with
/// true (an armed window is genuinely gone) or false (cancelled). If a candidate never manages to arm at
/// all - a windowless bootstrapper, a launch that fails before anything renders, or a launcher/game that
/// never leaves a single process - this simply never completes, by design; GameSessionWatcher is expected
/// to race this against its own process-exit tracking and act on whichever concludes first, falling back
/// to process-exit alone whenever this signal never fires or identity stays ambiguous.
/// </summary>
public sealed class GameWindowTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly IGameWindowProvider _windowProvider;
    private readonly GameWindowTrackerOptions _options;
    private readonly string _logPrefix;

    public GameWindowTracker()
        : this(TimeProvider.System, new Win32GameWindowProvider(), GameWindowTrackerOptions.Default)
    {
    }

    /// <summary>Test-only seam - GameLauncher.Tests substitutes a FakeTimeProvider and a scripted
    /// IGameWindowProvider here. Production always goes through the parameterless constructor above,
    /// except for GameWindowObserver's logging-only trial, which supplies `diagnosticTag` (typically the
    /// launch session id and game id) so every log line below can be correlated back to a specific
    /// session in a shared log file without this class needing to know anything about sessions,
    /// GameEntry, or the diagnostics feature itself - purely a string prefix, nothing else changes.</summary>
    internal GameWindowTracker(
        TimeProvider timeProvider, IGameWindowProvider windowProvider, GameWindowTrackerOptions options, string? diagnosticTag = null)
    {
        _timeProvider = timeProvider;
        _windowProvider = windowProvider;
        _options = options;
        _logPrefix = diagnosticTag is null ? "" : $"[{diagnosticTag}] ";
    }

    /// <summary>A candidate window not yet trusted as the real gameplay window - tracked only long enough
    /// to see whether it stabilizes (see AdvanceObservation) or gets dropped first. Only ever created for
    /// a candidate whose process already differs from baselineProcessId.</summary>
    private sealed record ObservedCandidate(GameWindow Window, DateTimeOffset FirstSeenUtc);

    /// <summary>
    /// Polls until an armed gameplay window is genuinely destroyed with nothing replacing it in time, or
    /// `ct` is cancelled. `getTrackedProcessIds` is invoked fresh on every poll rather than captured once,
    /// so a caller whose watched process set changes over time (GameSessionWatcher's own handoff
    /// detection) doesn't need to restart this tracker - it always evaluates against whichever process
    /// ids are current at that instant. Each call must return an immutable snapshot (a set that is never
    /// mutated in place after being returned - reassign a new instance instead) rather than a live,
    /// concurrently-mutable view: this method enumerates the returned set on a different cadence than
    /// whatever else may be updating it, and a shared mutable collection would race.
    ///
    /// Returns true once the session is confirmed over by this signal; false only on cancellation
    /// (mirrors GameSessionWatcher.WaitForExitAsync's own contract - the caller must treat false as
    /// "something else now owns this state," never as "the game is still running").
    /// </summary>
    public async Task<bool> WaitForGameplayWindowExitAsync(Func<IReadOnlySet<int>> getTrackedProcessIds, CancellationToken ct)
    {
        ObservedCandidate? observing = null;
        GameWindow? armed = null;
        int? baselineProcessId = null;

        // Every (handle, process id) identity this watch has seen among current gameplay candidates ON A
        // TICK WHERE NOTHING WAS CURRENTLY ARMED - i.e. only during discovery, before anything has proven
        // itself (see the class remarks for why arming suppresses this) - never removed, only grown. Keyed
        // on the PAIR, not the handle alone: Windows can reuse
        // a numeric HWND value for a genuinely different, unrelated window, and keying on the handle alone
        // would wrongly let an old identity's number veto a legitimate new one that happens to reuse it,
        // exactly the way IsWindowAlive needs both pieces to detect the opposite (a stale identity still
        // resolving to a live handle).
        var everSeenIdentities = new HashSet<(nint Handle, int ProcessId)>();

        while (!ct.IsCancellationRequested)
        {
            var trackedIds = getTrackedProcessIds();
            var currentCandidates = _windowProvider.FindGameplayWindows(trackedIds);

            if (armed is { } armedWindow)
            {
                if (!trackedIds.Contains(armedWindow.ProcessId))
                {
                    // The process-side watch has moved past this window's owner (a handoff) - it may
                    // still exist, but it's no longer a signal worth trusting; resume discovery against
                    // whichever processes are tracked now rather than either concluding on it or
                    // continuing to trust a stale window.
                    Logger.Info($"{_logPrefix}Gameplay window's process (pid {armedWindow.ProcessId}) is no "
                        + "longer tracked - resuming discovery rather than trusting a stale window.");
                    armed = null;
                }
                else if (!_windowProvider.IsWindowAlive(armedWindow.Handle, armedWindow.ProcessId))
                {
                    Logger.Info($"{_logPrefix}Gameplay window destroyed: pid {armedWindow.ProcessId}, "
                        + $"handle {armedWindow.Handle}, title '{SanitizeTitle(armedWindow.Title)}'.");

                    // The game can destroy and recreate its window within a single poll interval - by the
                    // time this tick's `currentCandidates` was fetched (above), it may already contain the
                    // legitimate replacement. That has to be checked FIRST, against history as it stood
                    // BEFORE this tick's own candidates are recorded below - otherwise this tick's own
                    // bookkeeping would mark the replacement "already seen" and WaitForReplacementAsync
                    // would then reject the very same window it was just handed.
                    var replacement = currentCandidates.FirstOrDefault(w => IsEligibleReplacement(w, everSeenIdentities, baselineProcessId))
                        ?? await WaitForReplacementAsync(everSeenIdentities, baselineProcessId, getTrackedProcessIds, ct);

                    if (ct.IsCancellationRequested)
                        return false;

                    if (replacement is null)
                        return true; // genuinely over - nothing replaced the destroyed window in time

                    // A same-session replacement (a fullscreen/resolution switch recreating the window,
                    // or a handoff continuing the same confirmed chain) inherits trust immediately rather
                    // than re-proving itself via observation - the same "stays confirmed" precedent
                    // GameSessionWatcher's own confirmedRealSession flag already follows on the process
                    // side.
                    Logger.Info($"{_logPrefix}Replacement gameplay window detected: pid {replacement.ProcessId}, "
                        + $"handle {replacement.Handle}, title '{SanitizeTitle(replacement.Title)}'.");
                    everSeenIdentities.Add((replacement.Handle, replacement.ProcessId));
                    armed = replacement;
                }
            }

            if (armed is null)
            {
                // Only recorded while nothing is currently armed - i.e. only candidates seen DURING
                // discovery, before anything has proven itself. A window that first appears while another
                // one is already armed and alive (e.g. a game creating its new render surface moments
                // before destroying the old one, rather than the other way around) must NOT be poisoned by
                // this bookkeeping just because it happened to coexist with an armed window for a tick or
                // two - it needs to remain eligible in case the armed window is destroyed shortly after and
                // this one is the legitimate replacement. Recording unconditionally on every tick (the
                // previous version of this method) could never tell that case apart from a genuinely stale
                // background window, because both look identical at the instant they're first observed:
                // "previously observed" alone isn't sufficient signal, only WHEN it was first observed
                // relative to the current armed tenure is. This correctly excludes a background window
                // that was already present - and so already recorded - during the discovery phase that
                // preceded arming; it does NOT, and cannot, guarantee every real background window is
                // caught this way (see the residual ambiguity noted below for the one case it doesn't
                // cover).
                //
                // Residual, accepted ambiguity: a coincidental, unrelated window that happens to first
                // appear WHILE something is already armed, and that is still alive when the armed window is
                // later destroyed, is indistinguishable from a legitimate replacement by this same
                // reasoning - there is no further signal available here (short of an untested,
                // undocumented same-process restriction that would also break the intentional
                // cross-process handoff case WaitForReplacementAsync exists for) to tell them apart. This is
                // the same category of trade-off as the three-process-bootstrap limitation above: not
                // solved, called out rather than hidden.
                foreach (var candidateWindow in currentCandidates)
                    everSeenIdentities.Add((candidateWindow.Handle, candidateWindow.ProcessId));

                observing = AdvanceObservation(observing, trackedIds, currentCandidates, ref armed, ref baselineProcessId);
            }

            try
            {
                await Task.Delay(_options.WindowPollInterval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Runs one tick of the not-yet-armed state machine. Drops an observed candidate that's been
    /// destroyed, whose process has left the tracked set, or that's lost the foreground before proving
    /// itself stable - in every case simply resuming discovery, never concluding anything. Arms a
    /// candidate that's held the foreground continuously for StabilizationPeriod. Otherwise starts
    /// observing whichever window, if any, is currently both a gameplay candidate and the OS foreground
    /// window - this is also what resolves multiple simultaneous candidates (e.g. a launcher window still
    /// open behind the real game): only the one actually in the foreground is ever considered. A candidate
    /// whose process equals baselineProcessId (the very first candidate's own process - set once, from the
    /// first candidate ever seen, and never changed afterward) is never selected at all, regardless of how
    /// long it holds the foreground - see the class remarks for why process ownership, not window order,
    /// is what has to change before a candidate is worth observing.</summary>
    private ObservedCandidate? AdvanceObservation(
        ObservedCandidate? observing,
        IReadOnlySet<int> trackedIds,
        IReadOnlyList<GameWindow> currentCandidates,
        ref GameWindow? armed,
        ref int? baselineProcessId)
    {
        if (observing is { } candidate)
        {
            var stillValid = trackedIds.Contains(candidate.Window.ProcessId)
                && _windowProvider.IsWindowAlive(candidate.Window.Handle, candidate.Window.ProcessId)
                && _windowProvider.GetForegroundWindow() == candidate.Window.Handle;

            if (stillValid)
            {
                if (_timeProvider.GetUtcNow() - candidate.FirstSeenUtc < _options.StabilizationPeriod)
                    return candidate; // still stabilizing - nothing else to do this tick

                Logger.Info($"{_logPrefix}Gameplay window armed: pid {candidate.Window.ProcessId}, "
                    + $"handle {candidate.Window.Handle}, title '{SanitizeTitle(candidate.Window.Title)}'.");
                armed = candidate.Window;
                return null;
            }

            // Dropped without a word either way on why (destroyed vs. lost foreground vs. handed off) -
            // discovery below re-derives the current truth fresh regardless of which one it was.
        }

        var foregroundHandle = _windowProvider.GetForegroundWindow();
        var newCandidate = currentCandidates.FirstOrDefault(w => w.Handle == foregroundHandle);
        if (newCandidate is null)
            return null;

        baselineProcessId ??= newCandidate.ProcessId;
        if (newCandidate.ProcessId == baselineProcessId)
        {
            // Still the same process that produced the very first candidate this watch ever observed -
            // not yet proven to be anything other than the launcher/bootstrapper itself, no matter how
            // long any individual window from it stays foreground (see the class remarks for the
            // two-stage-bootstrap case this specifically guards against). Never becomes `observing`;
            // process-exit tracking remains the only signal for the rest of this watch unless a
            // genuinely different process's window eventually takes the foreground.
            return null;
        }

        Logger.Info($"{_logPrefix}Gameplay window candidate observed: pid {newCandidate.ProcessId}, "
            + $"handle {newCandidate.Handle}, title '{SanitizeTitle(newCandidate.Title)}'.");
        return new ObservedCandidate(newCandidate, _timeProvider.GetUtcNow());
    }

    // A window title comes straight from the OS/the target application, not from anything this codebase
    // controls - unlike every other value logged alongside it (a pid, a handle, a boolean), it has no
    // practical bound on length or content. Bounded and newline-stripped purely for log hygiene (a
    // single log line staying one line, and a pathological title not bloating the log file), not for any
    // security property - Logger already writes plain text to a local file, nothing here is ever
    // rendered as markup or executed.
    private const int MaxLoggedTitleLength = 80;

    private static string SanitizeTitle(string title)
    {
        var oneLine = title.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= MaxLoggedTitleLength ? oneLine : oneLine[..MaxLoggedTitleLength] + "…";
    }

    /// <summary>Whether `window` could ever be accepted as a genuine replacement for a just-destroyed
    /// armed window: its (handle, process id) identity must never have been seen before, AND its process
    /// must not be the baseline - the baseline is "permanently ineligible" (see the class remarks), and
    /// that has to hold here exactly as it does for initial arming in AdvanceObservation, or the baseline
    /// process could simply open a brand-new window later and be accepted purely because that SPECIFIC
    /// identity happens to be new.</summary>
    private static bool IsEligibleReplacement(
        GameWindow window, HashSet<(nint Handle, int ProcessId)> everSeenIdentities, int? baselineProcessId) =>
        !everSeenIdentities.Contains((window.Handle, window.ProcessId)) && window.ProcessId != baselineProcessId;

    /// <summary>Polls for an eligible replacement candidate (see IsEligibleReplacement) - covers both a
    /// resolution/fullscreen switch that recreates the same game's window under a new handle, and a
    /// genuine handoff leaving a brief gap before the next stage's window appears - for up to
    /// ReplacementDebounce.
    ///
    /// Deliberately does NOT require the candidate to be the current foreground window, unlike
    /// AdvanceObservation's initial arming check: the player may well have Alt-Tabbed away to another
    /// application at the exact moment a fullscreen/resolution switch recreates the game's window, and
    /// requiring foreground here would wrongly conclude the session is over while the game is still
    /// running, just not focused. What protects against a stale window instead is `everSeenIdentities`: a
    /// pre-existing, merely-still-open window - a launcher that never closed, whether or not it was ever
    /// selected/foreground itself - was already present, and so already recorded, during the discovery
    /// phase that preceded arming (see WaitForGameplayWindowExitAsync - recording only happens while
    /// nothing is currently armed), so it can never be accepted as a "new" replacement no matter what has
    /// focus.
    ///
    /// Returns null (no replacement) on a genuine timeout or cancellation alike - the caller
    /// (WaitForGameplayWindowExitAsync) tells those apart via ct itself.</summary>
    private async Task<GameWindow?> WaitForReplacementAsync(
        HashSet<(nint Handle, int ProcessId)> everSeenIdentities,
        int? baselineProcessId,
        Func<IReadOnlySet<int>> getTrackedProcessIds,
        CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + _options.ReplacementDebounce;

        while (true)
        {
            var replacement = _windowProvider.FindGameplayWindows(getTrackedProcessIds())
                .FirstOrDefault(w => IsEligibleReplacement(w, everSeenIdentities, baselineProcessId));

            if (replacement is not null || _timeProvider.GetUtcNow() >= deadline || ct.IsCancellationRequested)
                return replacement;

            try
            {
                await Task.Delay(_options.WindowPollInterval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }
}
