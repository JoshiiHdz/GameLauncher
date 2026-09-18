using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for merge reconciliation: when GameScannerService's de-duplication merges a
/// superseded entry into a surviving one (e.g. a Manual entry reconciled into a launcher-detected one
/// for the same install - the real, confirmed A Way Out case), the superseded entry's
/// Favorite/Hidden/CustomName/DateAdded override must move with it (field by field, not "first one
/// present wins outright"), the surviving GameEntry's own DateAdded must reflect it immediately (not
/// only on a later refresh), and an actively-running game's badge must follow its id across the merge.
///
/// MigrateMergedOverrides tests exercise that one piece directly; the ApplyScanResult tests exercise the
/// actual production publish path RefreshAsync itself calls, per the explicit ask that this not be
/// covered only via the narrower dictionary-merge helper.
///
/// Same isolated-temp-directory fixture as LibraryViewModelRunningGameTests.
/// </summary>
public class LibraryViewModelMergedOverridesTests : IDisposable
{
    private readonly string _dataDir;
    private readonly LibraryViewModel _sut;

    public LibraryViewModelMergedOverridesTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, GameSource source = GameSource.Manual, string? installDir = null) => new()
    {
        Id = id,
        Name = "Test Game",
        ExecutablePath = (installDir ?? @"C:\Games\TestGame") + @"\game.exe",
        InstallDir = installDir ?? @"C:\Games\TestGame",
        Source = source,
    };

    // ---- MigrateMergedOverrides: the dictionary-level helper ------------------------------------------

    [Fact]
    public void FavoriteAndHiddenState_MovesFromLoserIdToWinnerId()
    {
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        _sut.ToggleFavoriteCommand.Execute(loser);
        _sut.ToggleHiddenCommand.Execute(loser);

        Assert.True(_sut.GetOverride("manual-haze1")!.Favorite);
        Assert.True(_sut.GetOverride("manual-haze1")!.Hidden);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Null(_sut.GetOverride("manual-haze1")); // no longer orphaned under the old id
        var migrated = _sut.GetOverride("ea-awayout");
        Assert.NotNull(migrated);
        Assert.True(migrated!.Favorite);
        Assert.True(migrated.Hidden);
    }

    [Fact]
    public void DateAdded_MovesFromLoserIdToWinnerId_NotResetToToday()
    {
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        _sut.ToggleFavoriteCommand.Execute(loser); // creates the override

        var originalDate = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        _sut.GetOverride("manual-haze1")!.DateAdded = originalDate;

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Equal(originalDate, _sut.GetOverride("ea-awayout")!.DateAdded);
    }

    [Fact]
    public void LoserWithNoOverride_MigratesNothing_WinnerStaysUntouched()
    {
        // No ToggleFavorite/ToggleHidden ever called for "manual-haze1" - it has no override at all.
        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Null(_sut.GetOverride("manual-haze1"));
        Assert.Null(_sut.GetOverride("ea-awayout"));
    }

    [Fact]
    public void DateOnlyWinner_AgainstFavoritedHiddenCustomNamedLoser_PreservesAllOfTheLosersExplicitChoices()
    {
        // Simulates two scans: first, EA detection succeeds with no Manual counterpart yet -
        // ApplyScanResult's own NewDateAddedByGameId path stamps a bare, AUTOMATIC DateAdded-only
        // override, with no explicit user choice behind it at all (exactly what GameScannerService's
        // real NewDateAddedByGameId does the first time any new id is seen). Later, a Manual entry for
        // the same game is ALSO discovered and gets merged into it - its own real, explicit
        // Favorite/Hidden must not be discarded just because the winner already had that bare record.
        // This is the exact confirmed gap: an earlier version of MigrateMergedOverrides treated ANY
        // pre-existing winner override, even a purely automatic one, as a reason to discard the loser's
        // real preferences outright instead of merging them in.
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var winnerDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _sut.ApplyScanResult(new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: new Dictionary<string, DateTime> { ["ea-awayout"] = winnerDate },
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>()));

        var bareOverride = _sut.GetOverride("ea-awayout");
        Assert.NotNull(bareOverride);
        Assert.False(bareOverride!.Favorite);
        Assert.False(bareOverride.Hidden);
        Assert.Null(bareOverride.CustomName);

        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);
        _sut.ToggleFavoriteCommand.Execute(loser);
        _sut.ToggleHiddenCommand.Execute(loser);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        var merged = _sut.GetOverride("ea-awayout");
        Assert.NotNull(merged);
        Assert.True(merged!.Favorite); // preserved from the loser, not blocked by the winner's bare record
        Assert.True(merged.Hidden);
        Assert.Equal(winnerDate, merged.DateAdded); // the winner's own DateAdded is untouched (already set)
    }

    [Fact]
    public void WinnersOwnExplicitChoice_IsNeverOverwrittenByALoserThatDoesNotHaveTheSameFieldSet()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        _sut.SimulateRefreshResult([winner]);
        _sut.ToggleFavoriteCommand.Execute(winner); // winner's own explicit choice: Favorite=true

        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);
        _sut.ToggleHiddenCommand.Execute(loser); // loser's own explicit choice: Hidden=true

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        // Both explicit choices survive - Favorite from the winner, Hidden from the loser - rather than
        // one blocking the other.
        var merged = _sut.GetOverride("ea-awayout");
        Assert.NotNull(merged);
        Assert.True(merged!.Favorite);
        Assert.True(merged.Hidden);
    }

    // ---- ApplyScanResult: the actual production publish path ------------------------------------------

    [Fact]
    public void ApplyScanResult_MergedGamesDateAdded_IsAppliedToTheSurvivingGameEntryImmediately()
    {
        // The scanner bakes a fresh (wrong, "today") DateAdded into a brand-new surviving id's GameEntry
        // before migration ever runs (see GameScannerService.ScanAllAsync) - without re-applying the
        // migrated, real DateAdded onto the GameEntry itself, "Recently Added" would show the wrong date
        // until a LATER refresh finally saw it in its own Overrides snapshot.
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var realDate = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _sut.ToggleFavoriteCommand.Execute(loser); // creates the override
        _sut.GetOverride("manual-haze1")!.DateAdded = realDate;

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        winner.DateAdded = DateTime.UtcNow; // what the scanner would have baked in for a "new" id

        var scanResult = new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(), // "ea-awayout" is NOT new-to-Overrides by the time ApplyScanResult's migration runs
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        _sut.ApplyScanResult(scanResult);

        Assert.Equal(realDate, winner.DateAdded); // corrected on the GameEntry itself, this same publish
    }

    [Fact]
    public void ApplyScanResult_RunningGameThatGetsMergedAway_KeepsItsBadgeUnderTheNewId()
    {
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var sessionId = _sut.MarkGameRunning(loser); // the manual entry is the one actively being played

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var scanResult = new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        _sut.ApplyScanResult(scanResult);

        Assert.Equal("ea-awayout", _sut.RunningGameId); // tracking followed the merge, not left pointing at a dead id
        Assert.True(winner.IsRunning); // the badge landed on the surviving GameEntry

        // The real, confirmed bug this covers: MainWindow holds onto and passes back the ORIGINAL
        // GameEntry instance it launched (`loser` here, whose own Id never changes to "ea-awayout" just
        // because a merge happened) - not the surviving `winner` instance. Clearing by loser.Id would
        // search _allGames for an id that no longer exists there at all, silently leaving winner.IsRunning
        // stuck true forever.
        _sut.MarkGameNotRunning(loser, sessionId);
        Assert.Null(_sut.RunningGameId);
        Assert.False(winner.IsRunning); // the actual, currently-displayed entry's badge is cleared
        Assert.False(loser.IsRunning); // the stale instance itself is also cleared, for consistency
    }

    [Fact]
    public void MergedSessionSupersededByAnotherLaunchBeforeItsOwnCleanup_StillClearsTheMergedWinnersBadge()
    {
        // The real, confirmed sequence this covers: session A (Manual) starts, gets merged into entry B
        // (EA) mid-play, and THEN an unrelated session C starts and supersedes A's tracking entirely -
        // all before A's own exit is ever observed by MainWindow. By the time A's belated cleanup call
        // finally arrives, _runningGameId/_runningSessionId have already moved on to C, so the superseded
        // branch (not the "still owns the session" branch the other test above covers) is what has to
        // resolve A's identity correctly.
        var loser = MakeGame("manual-a");
        _sut.SimulateRefreshResult([loser]);
        var sessionA = _sut.MarkGameRunning(loser);

        var winner = MakeGame("ea-b", GameSource.Ea);
        _sut.ApplyScanResult(new ScanResult([winner], [], [], new Dictionary<string, string> { ["manual-a"] = "ea-b" }));
        Assert.True(winner.IsRunning); // merge landed correctly, same as the test above

        var gameC = MakeGame("game-c");
        _sut.SimulateRefreshResult([winner, gameC]);
        var sessionC = _sut.MarkGameRunning(gameC); // supersedes A's tracking before A's own exit is seen

        Assert.Equal("game-c", _sut.RunningGameId);
        Assert.True(gameC.IsRunning);
        Assert.True(winner.IsRunning); // still running as far as the library knows - A hasn't exited yet

        // A's belated cleanup finally arrives - MainWindow passes the ORIGINAL entry it launched (loser)
        // and A's ORIGINAL session id, neither of which reflect the merge or the supersession.
        _sut.MarkGameNotRunning(loser, sessionA);

        Assert.False(winner.IsRunning); // B's badge is finally cleared - A really did exit
        Assert.Equal("game-c", _sut.RunningGameId); // C's own, newer session is completely untouched
        Assert.True(gameC.IsRunning);
    }

    [Fact]
    public void ApplyScanResult_NoMergeHappened_RunningGameIdIsLeftAlone()
    {
        var game = MakeGame("game-1");
        _sut.SimulateRefreshResult([game]);
        _sut.MarkGameRunning(game);

        var scanResult = new ScanResult(
            Games: [game],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>());

        _sut.ApplyScanResult(scanResult);

        Assert.Equal("game-1", _sut.RunningGameId);
        Assert.True(game.IsRunning);
    }

    [Fact]
    public void AfterMergedGameExit_RelaunchingTheSameSurvivingGame_TracksItNormally()
    {
        // Proves the fix doesn't leave any residual reconciliation state behind - a completely ordinary
        // launch/exit cycle for the SAME (now-surviving) game right after a merge-and-exit must behave
        // exactly like any other game.
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var firstSessionId = _sut.MarkGameRunning(loser);

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        _sut.ApplyScanResult(new ScanResult([winner], [], [], new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" }));
        _sut.MarkGameNotRunning(loser, firstSessionId);

        var secondSessionId = _sut.MarkGameRunning(winner);
        Assert.Equal("ea-awayout", _sut.RunningGameId);
        Assert.True(winner.IsRunning);

        _sut.MarkGameNotRunning(winner, secondSessionId);
        Assert.Null(_sut.RunningGameId);
        Assert.False(winner.IsRunning);
    }

    [Fact]
    public void AfterMergedGameExit_LaunchingADifferentGame_TracksOnlyThatGame()
    {
        // No leftover state from the merge (a stale tracked id, a lingering badge) leaks into an
        // unrelated later session for a completely different game.
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var firstSessionId = _sut.MarkGameRunning(loser);

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var otherGame = MakeGame("game-2");
        _sut.ApplyScanResult(new ScanResult(
            [winner, otherGame], [], [], new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" }));
        _sut.MarkGameNotRunning(loser, firstSessionId);

        var secondSessionId = _sut.MarkGameRunning(otherGame);
        Assert.Equal("game-2", _sut.RunningGameId);
        Assert.True(otherGame.IsRunning);
        Assert.False(winner.IsRunning); // the previously-merged winner is untouched by the new session

        _sut.MarkGameNotRunning(otherGame, secondSessionId);
        Assert.Null(_sut.RunningGameId);
        Assert.False(otherGame.IsRunning);
    }
}
