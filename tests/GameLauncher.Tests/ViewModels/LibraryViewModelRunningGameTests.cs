using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for running-game tracking (MarkGameRunning/MarkGameNotRunning/
/// ReapplyRunningBadge in LibraryViewModel): DownloadUpdateCommand's "don't restart the app while a
/// game is running" guard, and the "Running" badge itself, both depend on this staying correct across
/// a rescan - RefreshAsync replaces every GameEntry in the library wholesale, so anything that keys
/// off object identity alone (instead of the stable GameEntry.Id and a session id, as implemented
/// here) silently loses track of an active session.
///
/// Uses the internal SettingsService/PendingUpdateNotesService-injecting constructor to point both at
/// an isolated temp directory rather than the real %AppData%\GameLauncher - same idea as
/// SettingsServiceTests. Both, not just settings: this suite doesn't care about update notes at all,
/// but LibraryViewModel's constructor still reads a pending-update marker if one exists, and a
/// settings-only isolation seam would let these tests delete a real one out from under an installed
/// copy that happened to have one pending.
/// </summary>
public class LibraryViewModelRunningGameTests : IDisposable
{
    private readonly string _dataDir;
    private readonly LibraryViewModel _sut;

    public LibraryViewModelRunningGameTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id = "game-1") => new()
    {
        Id = id,
        Name = "Test Game",
        ExecutablePath = @"C:\Games\TestGame\game.exe",
        InstallDir = @"C:\Games\TestGame",
        Source = GameSource.Manual,
    };

    [Fact]
    public void MarkGameRunning_SetsRunningGameIdAndBadge()
    {
        var game = MakeGame();

        _sut.MarkGameRunning(game);

        Assert.Equal(game.Id, _sut.RunningGameId);
        Assert.True(game.IsRunning);
    }

    [Fact]
    public void MarkGameNotRunning_MatchingSession_ClearsRunningGameId()
    {
        var game = MakeGame();
        var sessionId = _sut.MarkGameRunning(game);

        _sut.MarkGameNotRunning(game, sessionId);

        Assert.Null(_sut.RunningGameId);
        Assert.False(game.IsRunning);
    }

    /// <summary>
    /// The bug: after a rescan, the watcher (MainWindow) is still holding the pre-refresh GameEntry
    /// instance, but the library's Games collection now shows a *different* GameEntry object with the
    /// same stable Id. Without an id-based lookup inside MarkGameNotRunning, clearing the stale
    /// object's IsRunning would leave the currently-displayed card stuck showing "Running" forever.
    /// </summary>
    [Fact]
    public void RefreshThenExit_ClearsBadgeOnCurrentDisplayedEntry_NotJustTheStaleOne()
    {
        var original = MakeGame();
        var sessionId = _sut.MarkGameRunning(game: original);

        // Simulate a rescan completing while the game is still running: every GameEntry is replaced,
        // including a brand new instance for the same game id - ReapplyRunningBadge should mark it
        // running (see the next test), but the *watcher* still only knows about `original`.
        var replacement = MakeGame();
        _sut.SimulateRefreshResult([replacement]);
        Assert.True(replacement.IsRunning); // sanity: refresh reconciliation kept the badge alive

        // The game exits. MainWindow calls MarkGameNotRunning with the stale `original` reference,
        // since that's the only GameEntry it ever held onto.
        _sut.MarkGameNotRunning(original, sessionId);

        Assert.False(original.IsRunning);
        Assert.False(replacement.IsRunning); // the actually-visible card must be cleared too
        Assert.Null(_sut.RunningGameId);
    }

    /// <summary>
    /// The bug: relaunching the *same* game after a refresh means the superseded GameEntry and the
    /// new one share the same game id. MainWindow marks the new session running, then cleans up the
    /// superseded one by calling MarkGameNotRunning on the old GameEntry - which, without session-id
    /// ownership, would look like "the currently running game" (same id) and wrongly clear the brand
    /// new session's tracking. Verified in both call orders: the fix must not depend on MainWindow
    /// getting the ordering right.
    /// </summary>
    [Fact]
    public void RelaunchSameGameAfterRefresh_CleanupCalledAfterNewSession_DoesNotClearNewSession()
    {
        var original = MakeGame();
        var oldSessionId = _sut.MarkGameRunning(original);

        var replacement = MakeGame(); // same default id as `original` - simulates a post-refresh relaunch
        var newSessionId = _sut.MarkGameRunning(replacement);
        Assert.NotEqual(oldSessionId, newSessionId);

        // Cleanup for the superseded session arrives *after* the new one already started.
        _sut.MarkGameNotRunning(original, oldSessionId);

        Assert.Equal(replacement.Id, _sut.RunningGameId);
        Assert.True(replacement.IsRunning);
    }

    /// <summary>
    /// The bug the previous two tests missed: they never placed `replacement` into _allGames, so
    /// MarkGameNotRunning's current-library-entry lookup always found nothing and that branch went
    /// unexercised. With `replacement` actually in the library (via SimulateRefreshResult, exactly
    /// like a real refresh), a stale cleanup call for the superseded session must not reach into
    /// _allGames and clear the *new* session's badge there, even though it shares the same game id.
    /// </summary>
    [Fact]
    public void RefreshThenRelaunchSameGame_StaleCleanupDoesNotClearNewSessionsLibraryBadge()
    {
        var original = MakeGame();
        var originalSessionId = _sut.MarkGameRunning(original);

        var replacement = MakeGame(); // same default id as `original` - simulates a post-refresh relaunch
        _sut.SimulateRefreshResult([replacement]);
        Assert.True(replacement.IsRunning); // sanity: refresh reconciliation kept the old session's badge alive

        var newSessionId = _sut.MarkGameRunning(replacement);
        Assert.NotEqual(originalSessionId, newSessionId);

        _sut.MarkGameNotRunning(original, originalSessionId);

        Assert.Equal(replacement.Id, _sut.RunningGameId);
        Assert.True(replacement.IsRunning);
    }

    [Fact]
    public void RelaunchSameGameAfterRefresh_CleanupCalledBeforeNewSession_TracksNewSession()
    {
        var original = MakeGame();
        var oldSessionId = _sut.MarkGameRunning(original);

        // Cleanup for the superseded session arrives *before* the new one starts (the order
        // MainWindow actually uses) - must behave identically to the reversed order above.
        _sut.MarkGameNotRunning(original, oldSessionId);

        var replacement = MakeGame();
        var newSessionId = _sut.MarkGameRunning(replacement);

        Assert.Equal(replacement.Id, _sut.RunningGameId);
        Assert.True(replacement.IsRunning);
        Assert.NotEqual(oldSessionId, newSessionId);
    }

    [Fact]
    public void MarkGameNotRunning_StaleSessionId_NeverClearsANewerSession()
    {
        var gameA = MakeGame("game-a");
        var sessionA = _sut.MarkGameRunning(gameA);

        var gameB = MakeGame("game-b");
        _sut.MarkGameRunning(gameB); // a different game entirely supersedes gameA's session

        // A late/duplicate cleanup call for the long-superseded session A must not touch B's tracking.
        _sut.MarkGameNotRunning(gameA, sessionA);

        Assert.Equal(gameB.Id, _sut.RunningGameId);
        Assert.True(gameB.IsRunning);
    }

    /// <summary>
    /// The bug: MarkGameNotRunning must not treat every stale/superseded cleanup call the same way.
    /// When game A is superseded by a *different* game B, A's own badge - wherever it currently lives
    /// in the library, which after a refresh may be a different instance than the one this cleanup
    /// call was holding - is genuinely stale and must be cleared. A version that blanket-refuses to
    /// touch the library whenever the calling session isn't current (correct for the same-game
    /// relaunch case) leaves A's replacement stuck showing "Running" forever alongside B.
    /// </summary>
    [Fact]
    public void RefreshThenDifferentGameSupersedes_ClearsOldGamesCurrentBadge_KeepsNewGameRunning()
    {
        var gameA = MakeGame("game-a");
        var oldSessionId = _sut.MarkGameRunning(gameA);

        var replacementA = MakeGame("game-a");
        var gameB = MakeGame("game-b");
        _sut.SimulateRefreshResult([replacementA, gameB]);
        Assert.True(replacementA.IsRunning); // sanity: refresh reconciliation kept A's badge alive

        _sut.MarkGameRunning(gameB); // a different game supersedes A's session

        // Cleanup for the superseded A session arrives, holding the stale pre-refresh instance.
        _sut.MarkGameNotRunning(gameA, oldSessionId);

        Assert.False(replacementA.IsRunning); // nothing tracks A anymore - its current badge must clear
        Assert.True(gameB.IsRunning);
        Assert.Equal(gameB.Id, _sut.RunningGameId);
    }

    /// <summary>
    /// Edge case: if the very same GameEntry instance is reused across two sessions for the same game
    /// (no refresh in between), a stale cleanup call for the *older* of the two sessions must not
    /// clear that instance's badge out from under the newer session that's still actively using it.
    /// </summary>
    [Fact]
    public void MarkGameNotRunning_StaleSessionForSameInstanceReused_DoesNotClearNewerSessionsBadge()
    {
        var game = MakeGame();
        var oldSessionId = _sut.MarkGameRunning(game);
        var newSessionId = _sut.MarkGameRunning(game); // same instance, relaunched into a new session
        Assert.NotEqual(oldSessionId, newSessionId);

        _sut.MarkGameNotRunning(game, oldSessionId);

        Assert.True(game.IsRunning);
        Assert.Equal(game.Id, _sut.RunningGameId);
    }
}
