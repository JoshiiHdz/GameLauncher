using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Which game is running, and the play time tracked while it runs (launched from here or noticed running outside).</summary>
public partial class LibraryViewModel
{
    // Id of the game GameSessionWatcher is currently tracking, kept independent of any particular
    // GameEntry instance. RefreshAsync replaces every entry in _allGames wholesale on each rescan,
    // so tracking "is a game running" via GameEntry.IsRunning alone would let DownloadUpdateCommand's
    // running-game guard go blind the moment a rescan happens mid-session - it would only ever see
    // the freshly-scanned entries, which all start not-running. MainWindow owns the actual watcher
    // lifecycle and calls MarkGameRunning/MarkGameNotRunning instead of touching GameEntry.IsRunning
    // directly, so this id and the badge can never disagree.
    private string? _runningGameId;

    // Ownership token for the session identified by _runningGameId. MarkGameNotRunning only clears
    // tracking when the session id it's given still matches this value. Without it, relaunching the
    // *same* game right after a refresh is broken: MainWindow calls MarkGameRunning(newEntry) for the
    // new session and then MarkGameNotRunning(oldEntry) to clean up the one it superseded - but
    // oldEntry and newEntry share the same game id, so a plain id comparison in MarkGameNotRunning
    // can't tell "the session I'm cleaning up" apart from "the session that just replaced it," and
    // would wrongly clear the brand new session no matter which order the two calls happen in. A
    // monotonically increasing session id makes that distinction unambiguous regardless of call
    // order. See MarkGameRunning/MarkGameNotRunning.
    private int _runningSessionId;

    private int _sessionCounter;

    // Every session's own CURRENT tracked game id, from MarkGameRunning until its own MarkGameNotRunning
    // call removes it - not just the single "whichever session is canonical right now" pair above.
    // Needed for a real, confirmed case: a session can be MERGED (see ReconcileRunningGameId - a Manual
    // entry's id folded into a launcher-detected one mid-play, e.g. EA's "A Way Out") and THEN
    // superseded by a newer, unrelated session before its own cleanup call ever runs. By the time that
    // cleanup arrives, _runningGameId/_runningSessionId above have already moved on to the newer
    // session, so the merged session's own reconciled identity would otherwise be lost - MarkGameNotRunning
    // would fall back to the caller's own (stale, pre-merge) GameEntry.Id, which no longer names anything
    // in the current library, and the merged-into entry's badge would never get cleared. Every session
    // that starts here gets an entry; ReconcileRunningGameId keeps ALL of them (not just the current one)
    // up to date as merges happen, and MarkGameNotRunning removes its own entry exactly once, when that
    // session is finally cleaned up.
    private readonly Dictionary<int, string> _sessionGameIds = new();

    /// <summary>Marks a game as the one session-watching currently tracks - called by MainWindow when
    /// a launch starts, never by setting GameEntry.IsRunning directly, so the update guard's
    /// _runningGameId can never drift out of sync with the badge. Returns a session id the caller
    /// must hold onto and pass back to MarkGameNotRunning for this exact session - see
    /// _runningSessionId's remarks for why that matters.</summary>
    public int MarkGameRunning(GameEntry game)
    {
        ++_sessionActivityGeneration;
        StopPassiveTrackingForGame(game.Id);
        var sessionId = ++_sessionCounter;
        _runningGameId = game.Id;
        _runningSessionId = sessionId;
        _sessionGameIds[sessionId] = game.Id;
        _sessionStartedUtc[sessionId] = SessionClock();
        game.IsRunning = true;
        return sessionId;
    }

    /// <summary>Test seam: the clock play-time accumulation is measured against. Real time by default;
    /// a test can advance it deliberately instead of sleeping.</summary>
    internal Func<DateTime> SessionClock { get; set; } = () => DateTime.UtcNow;

    // Start timestamp per session id, so a session that gets superseded (or whose game is merged into
    // a different entry mid-play) still accumulates against the right id - same reasoning as
    // _sessionGameIds, which this mirrors entry-for-entry.
    private readonly Dictionary<int, DateTime> _sessionStartedUtc = new();

    /// <summary>Adds this session's elapsed time to the game's persisted total and stamps LastPlayed.
    /// Keyed by the session's CURRENT tracked id (not game.Id), so a mid-session rescan that merged
    /// this install into a different entry credits the surviving one - the same hazard
    /// MarkGameNotRunning's own remarks describe for the badge.</summary>
    private void RecordPlayTime(int sessionId, string trackedId)
    {
        if (!_sessionStartedUtc.Remove(sessionId, out var startedUtc))
            return;

        var elapsed = SessionClock() - startedUtc;

        // A negative span (clock change) or an implausibly short one isn't play time worth recording -
        // a failed launch that exits instantly would otherwise accumulate noise onto a real total.
        if (elapsed < TimeSpan.FromSeconds(10))
            return;

        AddTrackedPlayTime(trackedId, (long)elapsed.TotalSeconds, SessionClock());
    }

    private void AddTrackedPlayTime(string trackedId, long seconds, DateTime lastPlayedUtc)
    {
        if (!_settings.Overrides.TryGetValue(trackedId, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[trackedId] = over;
        }

        over.TotalPlaySeconds += seconds;
        over.LastPlayedUtc = lastPlayedUtc;
        PlayHistory.Record(over, lastPlayedUtc, seconds);
        _settingsService.Save(_settings);

        if (_allGames.FirstOrDefault(g => g.Id == trackedId) is { } entry)
        {
            entry.TotalPlaySeconds = over.TotalPlaySeconds;
            entry.LastPlayedUtc = over.LastPlayedUtc;
        }

        ApplyFilter(); // the hero and the Recently played row are both ordered by what just changed
    }

    /// <summary>Clears tracking for the session identified by sessionId once GameSessionWatcher
    /// confirms the game exited (or it was superseded by a newer launch) - see MarkGameRunning. Two
    /// cases:
    /// - sessionId still owns the active session (a genuine, non-superseded exit): clears
    ///   _runningGameId/_runningSessionId and the badge, both on game itself and on whichever entry
    ///   in the *current* library actually shares this session's CURRENT tracked id.
    /// - sessionId has been superseded by a newer session: tracking state is left untouched (the newer
    ///   session already owns it), and the badge is cleared *only* if the newer session is for a
    ///   different game id. If it's the same id - a relaunch of this exact game, which is what makes
    ///   the superseded and current sessions share a game id despite being different sessions - the
    ///   badge belongs to that newer session and must be left alone.
    ///
    /// Neither case clears by `game.Id` directly: `game` is whatever GameEntry instance the caller
    /// launched and has held onto ever since (MainWindow holds it for the whole session), whose own Id
    /// never changes - but if a rescan merged THIS session's game into a different surviving entry while
    /// it was still running (see ReconcileRunningGameId, a real confirmed case for EA's "A Way Out"),
    /// this session's tracked id was redirected to that NEW one. _sessionGameIds (not the single
    /// _runningGameId pair, which only ever reflects whichever session is CURRENTLY canonical) is what
    /// keeps that redirected identity available for THIS specific session's own cleanup, even after a
    /// newer, different session has already superseded it and moved _runningGameId on. A real, confirmed
    /// case this fixes: session A (Manual) gets merged into entry B (EA) mid-play, then session C (an
    /// unrelated game) starts before A's own exit is ever observed - clearing by game.Id (A's original,
    /// now-gone id) would find nothing in the current library, leaving B's badge stuck on forever.</summary>
    public void MarkGameNotRunning(GameEntry game, int sessionId)
    {
        ++_sessionActivityGeneration;
        var trackedId = _sessionGameIds.Remove(sessionId, out var id) ? id : game.Id;
        RecordPlayTime(sessionId, trackedId);

        if (sessionId == _runningSessionId)
        {
            _runningGameId = null;
            _runningSessionId = 0;
            ClearBadge(game, trackedId);
            return;
        }

        if (_runningGameId != trackedId)
            ClearBadge(game, trackedId);
    }

    /// <summary>Clears game's own badge, plus whichever entry in the *current* library shares
    /// `idToClear` if that's a different instance (see MarkGameNotRunning - `idToClear` is the
    /// session's CURRENT tracked id, which is not always game.Id).</summary>
    private void ClearBadge(GameEntry game, string? idToClear)
    {
        var stillObserved = _passiveSessions.Keys.Any(t => _sessionGameIds.GetValueOrDefault(t) == idToClear);
        game.IsRunning = stillObserved;

        var current = _allGames.FirstOrDefault(g => g.Id == idToClear);
        if (current is not null && !ReferenceEquals(current, game))
            current.IsRunning = stillObserved;
    }

    /// <summary>Reapplies the running badge to whichever entry in _allGames matches the tracked
    /// session - called after RefreshAsync replaces every GameEntry wholesale, so an active session's
    /// badge doesn't vanish just because a rescan happened mid-game. The update guard itself never
    /// needs this: DownloadUpdateAsync checks _runningGameId directly, which isn't tied to any
    /// particular GameEntry instance.</summary>
    private void ReapplyRunningBadge()
    {
        var active = _passiveSessions.Keys.Select(t => _sessionGameIds.GetValueOrDefault(t)).ToHashSet();
        if (_runningGameId is { } id) active.Add(id);
        foreach (var game in _allGames) game.IsRunning = active.Contains(game.Id);
    }

    /// <summary>Exposed for tests that need to assert on running-game tracking directly - exercising
    /// it through DownloadUpdateCommand would also require faking a real update check just to
    /// populate _pendingUpdate first. See LibraryViewModelRunningGameTests.</summary>
    internal string? RunningGameId => _runningGameId;

    /// <summary>How many sessions are currently tracked in _sessionGameIds - every MarkGameRunning call
    /// adds one, every MarkGameNotRunning call (for that exact session) removes it. Exposed so tests can
    /// directly assert that a session's entry is genuinely retired, not merely that the badge looks
    /// right - a real, confirmed leak (MainWindow skipping cleanup entirely for a same-instance relaunch)
    /// left the badge looking correct while still leaking a stale map entry underneath it.</summary>
    internal int TrackedSessionCount => _sessionGameIds.Count;

    /// <summary>Redirects running-game tracking from a merged-away id onto the surviving id it was
    /// folded into - without this, a game that's actively running at the exact moment a rescan merges
    /// its id away (e.g. EA detection for "A Way Out" starts succeeding mid-session, superseding the
    /// Manual entry that was running) would lose its "Running" badge: ReapplyRunningBadge looks up
    /// _runningGameId against the freshly-replaced _allGames, which no longer contains ANY entry under
    /// the old id at all. The session-tracking fields themselves (_runningSessionId, the update guard)
    /// are untouched - only WHICH id they're associated with changes, so MarkGameNotRunning's later call
    /// for this exact session still correctly owns and clears it.</summary>
    private void ReconcileRunningGameId(Dictionary<string, string> mergedGameIds)
    {
        if (mergedGameIds.Count == 0)
            return;

        if (_runningGameId is { } runningId && mergedGameIds.TryGetValue(runningId, out var newRunningId))
            _runningGameId = newRunningId;

        // Every OTHER still-tracked session too, not just whichever one is currently canonical - a
        // session that's already been superseded (but hasn't had its own MarkGameNotRunning call yet)
        // still needs its own tracked identity kept correct, or its eventual cleanup would resolve
        // against a stale, already-merged-away id. See MarkGameNotRunning's own remarks.
        foreach (var sessionId in _sessionGameIds.Keys.ToList())
        {
            if (mergedGameIds.TryGetValue(_sessionGameIds[sessionId], out var winnerId))
                _sessionGameIds[sessionId] = winnerId;
        }
    }
}
