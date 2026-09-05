namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// GameSessionWatcher's timing tunables, pulled out so tests can run the same logic with
/// millisecond-scale values (paired with a fake TimeProvider) instead of actually waiting the 2s/12s/
/// 10-minute real-world durations production uses. Default reproduces exactly what was previously
/// hardcoded as private static readonly fields on GameSessionWatcher - see each field below for the
/// reasoning (taken from real gaming-PC logs) behind its value.
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

        // A real FC26 session logged all the way through: the initial process was watched ~8.4s before
        // handing off to EAAntiCheat.GameServiceLauncher, which itself ran ~49.2s before exiting - a
        // continuous chain of ~57.7s, well past "still a bootstrapper" territory, but with neither
        // individual batch ever reaching LongSessionThreshold on its own. GameSessionWatcher used to
        // judge every handoff purely on that batch's own uptime, so this whole chain still fell back to
        // the full 12s HandoffGracePeriod on its second handoff - the window sat un-restored for 12
        // seconds after a session anyone would call "clearly real" had ended. ChainConfirmationThreshold
        // lets GameSessionWatcher reach that same "this is a real session" conclusion from the sum of an
        // uninterrupted handoff chain, not just a single batch's own runtime, and - once reached - stays
        // confirmed for every later handoff in the same chain (see confirmedRealSession in
        // GameSessionWatcher.WaitForExitAsync). Deliberately lower than LongSessionThreshold: 45s is
        // comfortably past the ~15s "every confirmed-working launch discovers its process by here" figure
        // DiscoveryTimeout's own remarks cite, so an early, still-unconfirmed launcher/bootstrapper
        // handoff (the 7-8s cases HandoffGracePeriod exists for) can never accidentally cross it.
        ChainConfirmationThreshold: TimeSpan.FromSeconds(45));
}
