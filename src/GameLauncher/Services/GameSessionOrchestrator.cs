using System.Diagnostics;
using GameLauncher.Models;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

/// <summary>
/// Thin orchestration layer around GameSessionWatcher that starts a logging-only GameWindowObserver
/// observation ALONGSIDE the real, authoritative process-exit watch for the same launch session, and
/// guarantees the window-side diagnostic can never affect the process-side result GameSessionWatcher
/// hands back. See GameWindowObserver's own remarks for what "logging-only" means and its current known
/// blind spot, and AppSettings.EnableWindowExitDiagnostics for how this is enabled.
///
/// GameSessionWatcher.WaitForExitAsync's own result, timing, cancellation, and disposal behavior pass
/// through completely unchanged - with diagnostics off, this calls straight through with no wrapping at
/// all. With diagnostics on, this starts a SEPARATE, parallel task the caller never awaits directly, whose
/// own completion (success, failure, or never) has zero effect on what gets returned here:
///  - Completing early: simply stops logging further transitions for this session; the real watch keeps
///    running exactly as it would have anyway.
///  - Throwing: caught and logged (see RunObserverSafely) - never rethrown, never becomes an unobserved
///    task exception.
///  - Never completing: harmless, since it is never awaited on the return path - only cancelled.
///  - Superseded (the caller's own `ct` is cancelled): the observer's linked token cancels automatically,
///    stopping it independently of whatever GameSessionWatcher itself does with that same cancellation.
///
/// The observer is cancelled - never awaited - the instant the real watch concludes (success, failure, or
/// cancellation alike, via the `finally` below), so its own cleanup can never add latency to the caller's
/// restoration path.
/// </summary>
public sealed class GameSessionOrchestrator
{
    private readonly GameSessionWatcher _sessionWatcher;
    private readonly Func<bool> _diagnosticsEnabled;
    private readonly Func<int, GameEntry, Func<IReadOnlySet<int>>, CancellationToken, Task> _observe;

    public GameSessionOrchestrator(GameSessionWatcher sessionWatcher, Func<bool> diagnosticsEnabled)
        : this(sessionWatcher, diagnosticsEnabled, GameWindowObserver.ObserveAsync)
    {
    }

    /// <summary>Test-only seam - GameLauncher.Tests substitutes a scripted `observe` function here to
    /// simulate the observer completing early, throwing, or never completing, without depending on real
    /// Win32 window enumeration. Production always goes through the two-argument constructor above.</summary>
    internal GameSessionOrchestrator(
        GameSessionWatcher sessionWatcher,
        Func<bool> diagnosticsEnabled,
        Func<int, GameEntry, Func<IReadOnlySet<int>>, CancellationToken, Task> observe)
    {
        _sessionWatcher = sessionWatcher;
        _diagnosticsEnabled = diagnosticsEnabled;
        _observe = observe;
    }

    /// <summary>Same contract as GameSessionWatcher.WaitForExitAsync(GameEntry, Process?, ...) - `sessionId`
    /// is the same id MainWindow already tracks for the "Running" badge/restore logic, used here purely to
    /// correlate this session's diagnostic log lines, never to affect behavior.</summary>
    public Task<bool> WaitForExitAsync(int sessionId, GameEntry game, Process? launched, CancellationToken ct) =>
        WaitForExitAsyncCore(sessionId, game, ct,
            onProcessBatchChanged => _sessionWatcher.WaitForExitAsync(game, launched, ct, onProcessBatchChanged));

    /// <summary>Same contract as above, but against the IGameProcess seam directly - this is the one
    /// GameLauncher.Tests calls, with a fake `launched` (or none) instead of a real OS process.</summary>
    internal Task<bool> WaitForExitAsync(int sessionId, GameEntry game, IGameProcess? launched, CancellationToken ct) =>
        WaitForExitAsyncCore(sessionId, game, ct,
            onProcessBatchChanged => _sessionWatcher.WaitForExitAsync(game, launched, ct, onProcessBatchChanged));

    private async Task<bool> WaitForExitAsyncCore(
        int sessionId, GameEntry game, CancellationToken ct, Func<Action<IReadOnlySet<int>>?, Task<bool>> runSessionWatch)
    {
        if (!_diagnosticsEnabled())
            return await runSessionWatch(null);

        var snapshot = new ProcessIdSnapshotPublisher();

        // Linked, not `ct` directly: this must stop the observer the moment the real watch concludes for
        // ANY reason (see the `finally` below), not only when the caller's own token is cancelled -
        // otherwise a session that ends normally (the game genuinely exited) would leave its observer
        // running indefinitely instead of being stopped promptly.
        var observerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget with respect to the return path below (see the class remarks for why this must
        // never be awaited here), but its own faults are still fully observed - see RunObserverSafely -
        // so nothing from this ever becomes an unobserved task exception.
        _ = RunObserverSafely(sessionId, game, () => snapshot.Current, observerCts.Token);

        try
        {
            return await runSessionWatch(snapshot.Publish);
        }
        finally
        {
            // Signalled, never awaited: the observer notices and cleans up in the background regardless
            // of how long that takes, so this can never delay returning to the caller (and so never
            // delays restoration).
            observerCts.Cancel();
        }
    }

    private async Task RunObserverSafely(int sessionId, GameEntry game, Func<IReadOnlySet<int>> getTrackedProcessIds, CancellationToken ct)
    {
        try
        {
            await _observe(sessionId, game, getTrackedProcessIds, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[win-diag session={sessionId} game={game.Id}] observer task itself failed unexpectedly - "
                + "diagnostics only, no effect on launching or restoration.", ex);
        }
    }
}
