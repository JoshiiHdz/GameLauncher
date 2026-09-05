namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// GameSessionWatcher's timing tunables, pulled out so tests can run the same logic with
/// millisecond-scale values (paired with a fake TimeProvider) instead of actually waiting the 2s/12s/
/// 10-minute real-world durations production uses. Default reproduces exactly what was previously
/// hardcoded as private static readonly fields on GameSessionWatcher - see each field below for the
/// reasoning behind its value. These are empirical heuristics calibrated against a small number of real
/// gaming-PC logs, not vendor-documented or otherwise guaranteed bounds - Steam, Epic, EA, Ubisoft, and
/// Rockstar launchers can all front a wide variety of downstream anti-cheat/bootstrapper processes with
/// no common, documented timing contract between them, so a value here should be read as "long enough
/// that every real chain logged so far fits comfortably on the right side of it," not as a proven upper
/// or lower bound. Revisit the relevant value (with the new log, the same way each one below was
/// originally set) if a genuine counterexample ever turns up.
/// </summary>
public sealed record GameSessionWatcherOptions(
    TimeSpan PollInterval,
    TimeSpan DiscoveryTimeout,
    TimeSpan HandoffGracePeriod,
    TimeSpan LongSessionHandoffCheck,
    TimeSpan HandoffPollInterval,
    TimeSpan LongSessionThreshold,
    TimeSpan ChainConfirmationThreshold)
{
    public static GameSessionWatcherOptions Default { get; } = new(
        PollInterval: TimeSpan.FromSeconds(2),

        // Generous on purpose: real logs showed anti-cheat-heavy titles taking well over the old
        // 2-minute cap to spawn their real process, and this only exists as an outer sanity bound, not
        // the normal path - every confirmed-working launch in those same logs discovered its process
        // within 15 seconds. Past this, a launch that's still found nothing has most likely failed
        // outright (crashed, blocked on a prompt, cancelled) rather than still being "slow," so it's
        // treated the same as a real exit.
        DiscoveryTimeout: TimeSpan.FromMinutes(10),

        // How long to keep looking for a handoff process after the watched one(s) exit before
        // believing the game is actually closed. Found from real logs: gamelaunchhelper.exe (Xbox) and
        // an EA trial-launcher stub both exit within half a second of starting, well before the real
        // game process exists yet. This is ONLY the wait applied to a process that itself only just
        // started (see LongSessionThreshold below). The only real handoff timing measured so far (EA
        // SPORTS FC 26's trial-to-anti-cheat handoff) took 7 seconds; 12s keeps comfortable margin.
        HandoffGracePeriod: TimeSpan.FromSeconds(12),

        // Applied instead of HandoffGracePeriod when the process that just exited had clearly been the
        // real game (see LongSessionThreshold) - a genuine "I'm done playing" exit has no handoff to
        // wait for, so this is just one quick poll-or-two, kept non-zero only to still catch a game
        // restarting itself internally (an update-and-relaunch cycle).
        LongSessionHandoffCheck: TimeSpan.FromSeconds(2),

        HandoffPollInterval: TimeSpan.FromSeconds(1),

        // A bootstrapper/anti-cheat-init stage realistically never runs this long before handing off
        // or dying; if a watched process lived at least this long before exiting, it was almost
        // certainly the actual game being played, not a stub.
        LongSessionThreshold: TimeSpan.FromSeconds(60),

        // Derived from a single real FC26 session logged all the way through. The log's own timestamps
        // put ~8.4s between "began watching the initial process" and "EAAntiCheat.GameServiceLauncher
        // observed running" - but that figure isn't purely the first process's own runtime, since it
        // also folds in an unknown amount of exit-detection and handoff-polling delay, so it's not relied
        // on here. What the log DOES directly evidence is EAAntiCheat.GameServiceLauncher itself: once
        // observed, it ran for ~49.2s before disappearing - a figure bounded by two directly-observed
        // events, not by proxy through an intermediate stage - and 49.2s alone already exceeds this 45s
        // threshold, without needing the less certain first-stage figure at all. Neither stage individually
        // reached LongSessionThreshold (60s). GameSessionWatcher used to judge every handoff purely on
        // that batch's own uptime, so this whole chain still fell back to the full 12s HandoffGracePeriod
        // on its second handoff - the window sat un-restored for 12 seconds after a stage that had, on
        // its own, already run for longer than this threshold.
        //
        // ChainConfirmationThreshold lets GameSessionWatcher reach that same "this is probably a real
        // session" conclusion from the sum of an uninterrupted chain's own ACTIVE running time - time a
        // process was genuinely being watched, excluding handoff gaps spent waiting to see whether
        // anything would replace it (see cumulativeActiveDuration in WaitForExitAsync) - not just a
        // single batch's own runtime. This is an empirical heuristic calibrated against the one real
        // chain logged so far, not a vendor-documented or otherwise guaranteed bound on how long a
        // launcher/anti-cheat bootstrapper stage can legitimately run (see this record's own remarks) -
        // treat 45s as "long enough that the one real chain logged so far crossed it, and no bootstrapper
        // stage has yet been observed to," not as a proven ceiling. If a genuinely slow bootstrapper chain
        // is ever found taking this long, this constant needs revisiting with that log.
        //
        // Once reached - via either signal - confirmation stays sticky for every later handoff in the
        // same chain (see confirmedRealSession in GameSessionWatcher.WaitForExitAsync). Deliberately
        // lower than LongSessionThreshold and comfortably past the ~15s "every confirmed-working launch
        // discovers its process by here" figure DiscoveryTimeout's own remarks cite, so an early, still-
        // unconfirmed launcher/bootstrapper handoff (the 7-8s cases HandoffGracePeriod exists for) is
        // nowhere near it.
        ChainConfirmationThreshold: TimeSpan.FromSeconds(45));
}
