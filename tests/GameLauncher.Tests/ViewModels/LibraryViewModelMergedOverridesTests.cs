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
    public async Task DateOnlyWinner_AgainstFavoritedHiddenCustomNamedLoser_PreservesAllOfTheLosersExplicitChoices()
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
        await _sut.ApplyScanResultAsync(new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: new Dictionary<string, DateTime> { ["ea-awayout"] = winnerDate },
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: []));

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

    // ---- MigrateMergedOverrides: artwork precedence -------------------------------------------------

    private static ArtworkSelection MakeUserSelection(string assetId = "asset-1") => new()
    {
        Provider = ArtworkProvider.UserLocalFile,
        AssetId = assetId,
        AssetExtension = "png",
        IsUserSelected = true,
    };

    private static ArtworkSelection MakeAutomaticSelection() => new()
    {
        Provider = ArtworkProvider.SteamGridDb,
        MatchMethod = "ExactTitle",
        IsUserSelected = false,
    };

    [Fact]
    public void LoserUserSelection_BeatsWinnerAutomaticSelection()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var automatic = MakeAutomaticSelection();
        _sut.SetArtworkForTest("ea-awayout", automatic, revision: 1);
        var userPick = MakeUserSelection();
        _sut.SetArtworkForTest("manual-haze1", userPick, revision: 1);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Same(userPick, _sut.GetOverride("ea-awayout")!.Artwork);
    }

    [Fact]
    public void LoserUserSelection_FillsAnEmptySlot_WhenWinnerHasNoArtworkAtAll()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var userPick = MakeUserSelection();
        _sut.SetArtworkForTest("manual-haze1", userPick, revision: 1);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Same(userPick, _sut.GetOverride("ea-awayout")!.Artwork);
    }

    [Fact]
    public void WinnerUserSelection_BeatsLoserAutomaticSelection()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var userPick = MakeUserSelection();
        _sut.SetArtworkForTest("ea-awayout", userPick, revision: 1);
        _sut.SetArtworkForTest("manual-haze1", MakeAutomaticSelection(), revision: 1);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Same(userPick, _sut.GetOverride("ea-awayout")!.Artwork);
    }

    [Fact]
    public void BothSidesHaveDifferentUserSelections_WinnerKeepsItsOwn_LosersIsRecordedAsAConflict_NotDiscarded()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var winnerPick = MakeUserSelection("winner-asset");
        var loserPick = MakeUserSelection("loser-asset");
        _sut.SetArtworkForTest("ea-awayout", winnerPick, revision: 1);
        _sut.SetArtworkForTest("manual-haze1", loserPick, revision: 1);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        // The winner's own pick stays active - not silently replaced by the loser's.
        Assert.Same(winnerPick, _sut.GetOverride("ea-awayout")!.Artwork);

        // But the loser's pick is NOT simply gone - it's durably recorded, not just logged.
        var conflict = Assert.Single(_sut.ArtworkConflictsForTest);
        Assert.Equal("ea-awayout", conflict.WinnerGameId);
        Assert.Equal("manual-haze1", conflict.LoserGameId);
        Assert.Same(loserPick, conflict.LoserSelection);
    }

    [Fact]
    public void BothSidesHaveTheSameUserSelection_NoConflictIsRecorded()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var samePick = MakeUserSelection("same-asset");
        _sut.SetArtworkForTest("ea-awayout", samePick, revision: 1);
        _sut.SetArtworkForTest("manual-haze1", MakeUserSelection("same-asset"), revision: 1); // same AssetId, different instance

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Empty(_sut.ArtworkConflictsForTest);
    }

    [Fact]
    public void ArtworkRevision_AfterMerge_IsStrictlyGreaterThanBothSidesPriorValues()
    {
        // Not a copy of either side's number - a scan result computed against either side's PRE-merge
        // revision (or, by numeric coincidence, some entirely unrelated game's) must never be able to
        // validate against the post-merge id just because the numbers happen to match.
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        _sut.SetArtworkForTest("ea-awayout", MakeAutomaticSelection(), revision: 3);
        _sut.SetArtworkForTest("manual-haze1", MakeAutomaticSelection(), revision: 7);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.True(_sut.GetOverride("ea-awayout")!.ArtworkRevision > 7);
    }

    [Fact]
    public void ArtworkRevision_WinnerHadNoOverrideAtAll_StillBumpedPastTheLosersPriorValue()
    {
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        _sut.SetArtworkForTest("manual-haze1", MakeUserSelection(), revision: 5);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.True(_sut.GetOverride("ea-awayout")!.ArtworkRevision > 5);
    }

    [Fact]
    public void ArtworkRevision_LoserAtExhaustion_WinnerHadNoOverride_AdoptedAsIs_NeverWrapsToACollidableLowValue()
    {
        // Unchecked arithmetic would otherwise silently wrap long.MaxValue + 1 to long.MinValue - bad on
        // its own - but wrapping to a SMALL value (0 was an earlier, real, confirmed version of this bug)
        // is actually worse: 0 is the ordinary default for a never-touched override, so a wrapped counter
        // could coincide with a genuinely outstanding scan/lookup's captured revision from long before the
        // wrap and be wrongly treated as current - reachable through the counter's own normal lifecycle,
        // not only through corruption. The loser's own revision is already exhausted here, so
        // TryGetNextRevision can't bump it further - it's adopted onto the winner exactly as-is (still a
        // real, distinguishing change from the winner's own prior "no override at all" state) rather than
        // reusing a smaller, collidable value. Every future artwork change to this survivor id will itself
        // now be safely rejected too - see ArtworkRevision_WinnerAndLoserBothAtExhaustion... below for the
        // branch where an in-place merge (both sides already having overrides) hits that same boundary.
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        _sut.SetArtworkForTest("manual-haze1", MakeUserSelection(), revision: long.MaxValue);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Equal(long.MaxValue, _sut.GetOverride("ea-awayout")!.ArtworkRevision);
    }

    [Fact]
    public void ArtworkRevision_WinnerAndLoserBothAtExhaustion_ArtworkMigrationIsSkipped_ButLosersSelectionIsStillRecorded()
    {
        // The real property this covers: migrating the WINNER's artwork data while unable to bump its
        // revision would be an unsignaled change - an outstanding scan/lookup that already captured this
        // exact (exhausted) revision for the winner's OWN id would wrongly keep treating itself as still
        // current even though the merge just replaced the winner's artwork underneath it. Skipping the
        // artwork migration entirely at this boundary (Favorite/Hidden/CustomName/DateAdded are unrelated
        // and still migrate normally) is what keeps that guarantee intact.
        //
        // But the loser's own GameOverride is removed regardless (see MigrateMergedOverrides' very first
        // line) - if its explicit selection isn't recorded somewhere durable before that happens, its
        // identity is lost forever with nothing left even referencing it, not just "not activated". A
        // real, confirmed gap in an earlier version of this fix: it correctly left the winner's active
        // artwork/revision untouched but never recorded the loser's selection either.
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        var winnerPick = MakeUserSelection("winner-asset");
        var loserPick = MakeUserSelection("loser-asset");
        _sut.SetArtworkForTest("ea-awayout", winnerPick, revision: long.MaxValue);
        _sut.SetArtworkForTest("manual-haze1", loserPick, revision: long.MaxValue);
        _sut.ToggleFavoriteCommand.Execute(loser); // an unrelated field - must still migrate normally

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        var merged = _sut.GetOverride("ea-awayout")!;
        Assert.Same(winnerPick, merged.Artwork); // untouched - not replaced, not cleared
        Assert.Equal(long.MaxValue, merged.ArtworkRevision); // not "bumped" past itself - impossible at this boundary
        Assert.True(merged.Favorite); // the unrelated field still migrated correctly

        var conflict = Assert.Single(_sut.ArtworkConflictsForTest);
        Assert.Equal("ea-awayout", conflict.WinnerGameId);
        Assert.Equal("manual-haze1", conflict.LoserGameId);
        Assert.Same(loserPick, conflict.LoserSelection); // the loser's full selection is preserved, not just logged
    }

    [Fact]
    public void ArtworkRevision_WinnerAndLoserBothAtExhaustion_LoserHadOnlyAnAutomaticSelection_NothingRecorded()
    {
        // Only an EXPLICIT (user-selected) loser selection needs preserving - an automatic match has no
        // durable identity a user chose and cares about keeping; recording it would just be noise.
        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([winner, loser]);

        _sut.SetArtworkForTest("ea-awayout", MakeUserSelection("winner-asset"), revision: long.MaxValue);
        _sut.SetArtworkForTest("manual-haze1", MakeAutomaticSelection(), revision: long.MaxValue);

        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" });

        Assert.Empty(_sut.ArtworkConflictsForTest);
    }

    // ---- ApplyScanResult: the actual production publish path ------------------------------------------

    [Fact]
    public async Task ApplyScanResult_MergedGamesDateAdded_IsAppliedToTheSurvivingGameEntryImmediately()
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
            MergedGameIds: new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" },
            ArtworkResultsByGameId: []);

        await _sut.ApplyScanResultAsync(scanResult);

        Assert.Equal(realDate, winner.DateAdded); // corrected on the GameEntry itself, this same publish
    }

    [Fact]
    public async Task ApplyScanResult_RunningGameThatGetsMergedAway_KeepsItsBadgeUnderTheNewId()
    {
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var sessionId = _sut.MarkGameRunning(loser); // the manual entry is the one actively being played

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var scanResult = new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" },
            ArtworkResultsByGameId: []);

        await _sut.ApplyScanResultAsync(scanResult);

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
    public async Task MergedSessionSupersededByAnotherLaunchBeforeItsOwnCleanup_StillClearsTheMergedWinnersBadge()
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
        await _sut.ApplyScanResultAsync(new ScanResult([winner], [], [], new Dictionary<string, string> { ["manual-a"] = "ea-b" }, []));
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
    public async Task ApplyScanResult_NoMergeHappened_RunningGameIdIsLeftAlone()
    {
        var game = MakeGame("game-1");
        _sut.SimulateRefreshResult([game]);
        _sut.MarkGameRunning(game);

        var scanResult = new ScanResult(
            Games: [game],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: []);

        await _sut.ApplyScanResultAsync(scanResult);

        Assert.Equal("game-1", _sut.RunningGameId);
        Assert.True(game.IsRunning);
    }

    [Fact]
    public async Task AfterMergedGameExit_RelaunchingTheSameSurvivingGame_TracksItNormally()
    {
        // Proves the fix doesn't leave any residual reconciliation state behind - a completely ordinary
        // launch/exit cycle for the SAME (now-surviving) game right after a merge-and-exit must behave
        // exactly like any other game.
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var firstSessionId = _sut.MarkGameRunning(loser);

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        await _sut.ApplyScanResultAsync(new ScanResult([winner], [], [], new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" }, []));
        _sut.MarkGameNotRunning(loser, firstSessionId);

        var secondSessionId = _sut.MarkGameRunning(winner);
        Assert.Equal("ea-awayout", _sut.RunningGameId);
        Assert.True(winner.IsRunning);

        _sut.MarkGameNotRunning(winner, secondSessionId);
        Assert.Null(_sut.RunningGameId);
        Assert.False(winner.IsRunning);
    }

    [Fact]
    public async Task AfterMergedGameExit_LaunchingADifferentGame_TracksOnlyThatGame()
    {
        // No leftover state from the merge (a stale tracked id, a lingering badge) leaks into an
        // unrelated later session for a completely different game.
        var loser = MakeGame("manual-haze1");
        _sut.SimulateRefreshResult([loser]);
        var firstSessionId = _sut.MarkGameRunning(loser);

        var winner = MakeGame("ea-awayout", GameSource.Ea);
        var otherGame = MakeGame("game-2");
        await _sut.ApplyScanResultAsync(new ScanResult(
            [winner, otherGame], [], [], new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" }, []));
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
