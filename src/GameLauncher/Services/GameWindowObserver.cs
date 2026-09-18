using GameLauncher.Models;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

/// <summary>
/// Logging-only trial of GameWindowTracker as GameSessionWatcher's PREFERRED exit signal - see
/// GameWindowTracker's own remarks for the motivation. This NEVER restores the window, cancels process
/// monitoring, or clears running state; it only measures, via structured logging, how this signal would
/// have behaved alongside the process-exit signal GameSessionWatcher.WaitForExitAsync actually acts on -
/// so real gameplay logs (Marvel Rivals, FC26, Fortnite, Xbox/Steam titles) can be examined before this
/// becomes a live, user-visible signal. Started only when AppSettings.EnableWindowExitDiagnostics is set
/// (off by default) - see GameSessionOrchestrator, which is also what actually starts/stops one of these
/// per launch session and guarantees this can never affect that session's real outcome.
///
/// GameWindowTracker itself already logs every transition that matters (candidate observed, armed,
/// destroyed, replacement detected) - tagged with `sessionId`/`game.Id` via its diagnosticTag constructor
/// parameter so those lines can be correlated back to a specific session in the shared log file. This
/// method only adds the start/stop bracket and the "would restore" conclusion around that, and guarantees
/// every failure mode (an exception, cancellation, or simply never completing) turns into nothing more
/// than a log line - nothing here is allowed to propagate an exception to its caller or affect launching
/// or restoration in any way.
///
/// KNOWN, INTENTIONAL BLIND SPOT: `getTrackedProcessIds` only ever reflects GameSessionWatcher's CURRENT
/// batch (see GameSessionWatcher.PublishBatch/ProcessIdSnapshotPublisher). GameSessionWatcher itself only
/// discovers a handoff's replacement process AFTER its current batch has fully exited (WaitForHandoffAsync
/// runs strictly after the loop's exit-wait, not concurrently with it). So while an old launcher batch is
/// still being watched, a new game process that has already started is simply invisible to this observer -
/// exactly as invisible as it is to GameSessionWatcher itself. This diagnostic does not, and cannot in
/// this increment, see a replacement any earlier than GameSessionWatcher does; publishing batches alone
/// does not solve this, and this is not something this increment attempts to change.
/// </summary>
internal static class GameWindowObserver
{
    public static async Task ObserveAsync(int sessionId, GameEntry game, Func<IReadOnlySet<int>> getTrackedProcessIds, CancellationToken ct)
    {
        var tag = $"win-diag session={sessionId} game={game.Id}";
        var tracker = new GameWindowTracker(TimeProvider.System, new Win32GameWindowProvider(), GameWindowTrackerOptions.Default, tag);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Logger.Info($"[{tag}] starting logging-only window observation (diagnostics only - does not affect restoration).");

        bool wouldRestore;
        try
        {
            wouldRestore = await tracker.WaitForGameplayWindowExitAsync(getTrackedProcessIds, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{tag}] observation failed after {stopwatch.Elapsed.TotalSeconds:0.0}s - diagnostics only, no effect on launching or restoration.", ex);
            return;
        }

        if (ct.IsCancellationRequested)
        {
            Logger.Info($"[{tag}] observation stopped after {stopwatch.Elapsed.TotalSeconds:0.0}s (session ended, superseded, or app closing).");
            return;
        }

        if (wouldRestore)
        {
            Logger.Info($"[{tag}] WOULD RESTORE now (window signal) after {stopwatch.Elapsed.TotalSeconds:0.0}s of "
                + "observation - compare this timestamp against the process-exit restoration log for this session.");
        }
    }
}
