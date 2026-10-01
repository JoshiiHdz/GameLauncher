using System.IO;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Regression coverage for the LegacyId transition (see GameEntry.LegacyId and
/// ScanResult.LegacyIdRemap): when a scanner's own detection method changes what Id the SAME real
/// install computes to (Xbox's package-registration redesign is the first case of this), a game's
/// saved Favorite/Hidden/DateAdded/artwork/identity and running-game tracking must all move onto the
/// new id - not just be readable via a fallback lookup during the one scan that discovers the change.
///
/// The real, confirmed gap this covers: GameScannerService.ScanAllAsync used to read the OLD override
/// into its private per-scan snapshot (so the freshly-built GameEntry looked right for one scan), but
/// never migrated the PERSISTED settings.Overrides entry itself - so LibraryViewModel.
/// ApplyScanResultAsync's own re-apply-from-live-overrides loop (which runs after GameScannerService's
/// snapshot is already gone) found nothing under the new id and immediately reset Favorite/Hidden back
/// to false on that same publish. The fix routes LegacyIdRemap through the exact same migration
/// machinery MergedGameIds already used for within-scan dedup merges (see
/// LibraryViewModelMergedOverridesTests for that side) - these tests exercise it through the actual
/// ApplyScanResultAsync publish path, per the explicit ask that this not be covered only by a narrower,
/// dictionary-only helper test.
///
/// Same isolated-temp-directory fixture as LibraryViewModelMergedOverridesTests.
/// </summary>
public class LibraryViewModelLegacyIdTests : IDisposable
{
    private readonly string _dataDir;
    private readonly LibraryViewModel _sut;

    public LibraryViewModelLegacyIdTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry MakeGame(string id, string? legacyId = null, GameSource source = GameSource.Xbox) => new()
    {
        Id = id,
        LegacyId = legacyId,
        Name = "Test Game",
        ExecutablePath = @"C:\XboxGames\TestGame\game.exe",
        InstallDir = @"C:\XboxGames\TestGame",
        Source = source,
    };

    [Fact]
    public async Task LegacyIdTransition_FavoriteAndHiddenState_MovesFromTheOldIdToTheNewIdImmediately()
    {
        // "Old-ID saved state": a game the user already favorited/hid, found under its old id.
        var oldEntry = MakeGame("xbox-oldfolder");
        _sut.SimulateRefreshResult([oldEntry]);
        _sut.ToggleFavoriteCommand.Execute(oldEntry);
        _sut.ToggleHiddenCommand.Execute(oldEntry);
        Assert.True(_sut.GetOverride("xbox-oldfolder")!.Favorite);
        Assert.True(_sut.GetOverride("xbox-oldfolder")!.Hidden);

        // "New-ID entry with LegacyId": the next scan's detection-method change computes a different id
        // for the SAME real install, carrying the old one forward via LegacyId.
        var newEntry = MakeGame("xbox-family", legacyId: "xbox-oldfolder");
        var scanResult = new ScanResult(
            Games: [newEntry],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: [],
            LegacyIdRemap: new Dictionary<string, string> { ["xbox-oldfolder"] = "xbox-family" });

        // "Publish".
        await _sut.ApplyScanResultAsync(scanResult);

        // The old id is no longer orphaned; the new id inherited its state.
        Assert.Null(_sut.GetOverride("xbox-oldfolder"));
        var migrated = _sut.GetOverride("xbox-family");
        Assert.NotNull(migrated);
        Assert.True(migrated!.Favorite);
        Assert.True(migrated.Hidden);

        // The real, confirmed bug: the GameEntry itself must reflect this on THIS SAME publish, not just
        // the override record - ApplyScanResultAsync's own re-apply-from-live-overrides loop runs AFTER
        // migration precisely so this doesn't require a second refresh to show correctly.
        Assert.True(newEntry.Favorite);
        Assert.True(newEntry.Hidden);
    }

    [Fact]
    public async Task LegacyIdTransition_DateAdded_MovesToTheNewId_NotResetToToday()
    {
        var oldEntry = MakeGame("xbox-oldfolder");
        _sut.SimulateRefreshResult([oldEntry]);
        _sut.ToggleFavoriteCommand.Execute(oldEntry); // creates the override
        var originalDate = new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        _sut.GetOverride("xbox-oldfolder")!.DateAdded = originalDate;

        var newEntry = MakeGame("xbox-family", legacyId: "xbox-oldfolder");
        newEntry.DateAdded = DateTime.UtcNow; // what the scanner bakes in for what it thinks is a brand-new id

        await _sut.ApplyScanResultAsync(new ScanResult(
            Games: [newEntry],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: [],
            LegacyIdRemap: new Dictionary<string, string> { ["xbox-oldfolder"] = "xbox-family" }));

        Assert.Equal(originalDate, _sut.GetOverride("xbox-family")!.DateAdded);
        Assert.Equal(originalDate, newEntry.DateAdded); // corrected on the GameEntry itself, this same publish
    }

    [Fact]
    public async Task LegacyIdTransition_RunningGameBadge_FollowsToTheNewId_EvenWithNoSavedOverrideAtAll()
    {
        // No ToggleFavorite/ToggleHidden ever happened for "xbox-oldfolder" - nothing is saved under it
        // at all. The real, confirmed gap this covers: an earlier version of ComputeLegacyIdRemap only
        // reported a transition when the OVERRIDES SNAPSHOT already had an entry under the old id, which
        // meant a plain running game (favorited or not) lost its badge across the transition. The map
        // here is the REAL one GameScannerService would compute - not a hand-written dictionary - so this
        // test actually exercises that fix, rather than bypassing it the way supplying the mapping
        // manually would.
        var oldEntry = MakeGame("xbox-oldfolder");
        _sut.SimulateRefreshResult([oldEntry]);
        var sessionId = _sut.MarkGameRunning(oldEntry);

        var newEntry = MakeGame("xbox-family", legacyId: "xbox-oldfolder");
        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newEntry]);

        await _sut.ApplyScanResultAsync(new ScanResult(
            Games: [newEntry],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: [],
            LegacyIdRemap: legacyIdRemap));

        Assert.Equal("xbox-family", _sut.RunningGameId);
        Assert.True(newEntry.IsRunning);

        _sut.MarkGameNotRunning(oldEntry, sessionId);
        Assert.Null(_sut.RunningGameId);
        Assert.False(newEntry.IsRunning);
    }

    [Fact]
    public async Task LegacyIdTransition_ComputedAgainstNoSavedStateAtAll_StillMigrates_AnOverrideCreatedBeforePublicationButAfterTheMapWasComputed()
    {
        // The real race this covers: ComputeLegacyIdRemap only ever looks at this scan's own GameEntry
        // list (never the overrides snapshot) specifically so a transition is still reported even when
        // nothing is saved under the old id AT THE TIME THE SCAN STARTS. A scan can take a while, though -
        // a user action can land on the OLD id after the scanner has already moved on, but before this
        // scan's result is ever published. An earlier version that gated the map on the SNAPSHOT would
        // have missed this override entirely, since it didn't exist when the snapshot was taken.
        var newEntry = MakeGame("xbox-family", legacyId: "xbox-oldfolder");
        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newEntry]);
        Assert.Equal("xbox-family", legacyIdRemap["xbox-oldfolder"]); // reported even with nothing saved anywhere yet

        var oldEntry = MakeGame("xbox-oldfolder");
        _sut.ToggleFavoriteCommand.Execute(oldEntry); // lands on the old id after the map was computed, before publish
        Assert.True(_sut.GetOverride("xbox-oldfolder")!.Favorite);

        await _sut.ApplyScanResultAsync(new ScanResult(
            Games: [newEntry],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: [],
            LegacyIdRemap: legacyIdRemap));

        Assert.Null(_sut.GetOverride("xbox-oldfolder"));
        Assert.True(_sut.GetOverride("xbox-family")!.Favorite);
        Assert.True(newEntry.Favorite);
    }

    [Fact]
    public void LegacyIdWithNoSavedOverride_MigratesNothing_NewIdStaysUntouched()
    {
        // ComputeLegacyIdRemap reports this transition regardless of whether anything is saved under the
        // old id (see the tests above) - ApplyScanResultAsync/MigrateMergedOverrides must still handle a
        // transition with genuinely nothing to migrate harmlessly.
        _sut.MigrateMergedOverrides(new Dictionary<string, string> { ["xbox-oldfolder"] = "xbox-family" });

        Assert.Null(_sut.GetOverride("xbox-oldfolder"));
        Assert.Null(_sut.GetOverride("xbox-family"));
    }

    [Fact]
    public async Task LegacyIdTransition_SurvivesASaveAndReload_ThenAppliesCorrectlyOnASecondScan()
    {
        // Session 1: the game is discovered and favorited under its OLD id, and that's really persisted
        // to disk (ToggleFavoriteCommand saves immediately, exactly like the real app).
        var oldEntry = MakeGame("xbox-oldfolder");
        _sut.SimulateRefreshResult([oldEntry]);
        _sut.ToggleFavoriteCommand.Execute(oldEntry);

        // "Reload": a brand-new LibraryViewModel over the SAME settings directory - what a real app
        // restart looks like - loads that persisted override fresh from disk, not from the first
        // instance's in-memory state.
        var restarted = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));

        // "Second scan": this run's detection-method change computes the new, family-anchored id for the
        // very same install.
        var newEntry = MakeGame("xbox-family", legacyId: "xbox-oldfolder");
        await restarted.ApplyScanResultAsync(new ScanResult(
            Games: [newEntry],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string>(),
            ArtworkResultsByGameId: [],
            LegacyIdRemap: new Dictionary<string, string> { ["xbox-oldfolder"] = "xbox-family" }));

        Assert.True(newEntry.Favorite);
        Assert.True(restarted.GetOverride("xbox-family")!.Favorite);
        Assert.Null(restarted.GetOverride("xbox-oldfolder"));

        // The migration itself is persisted too (RefreshAsync's real sequence: ApplyScanResultAsync, then
        // save) - a THIRD instance loading fresh from disk must see the already-migrated state, not fall
        // back through LegacyId all over again.
        Assert.True(restarted.SaveNowForTest());
        var thirdInstance = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
        Assert.True(thirdInstance.GetOverride("xbox-family")!.Favorite);
        Assert.Null(thirdInstance.GetOverride("xbox-oldfolder"));
    }

    private static ArtworkSelection MakeAutomaticSelection(string providerGameId) => new()
    {
        Provider = ArtworkProvider.SteamGridDb,
        ProviderGameId = providerGameId,
        MatchMethod = "ExactTitle",
        IsUserSelected = false,
    };

    [Fact]
    public async Task TransitionAliasing_NeverLetsALoserWithItsOwnPriorDisplay_OverwriteAWinnerThatAlreadyHasOne()
    {
        // The real, confirmed bug this covers: the previousGamesById "fill in a brand-new id's prior
        // display" step the LegacyId tests above need is shared, through idTransitions, with an ORDINARY
        // dedup merge (MergedGameIds) too - and an ordinary merge's winner is very often NOT brand new: it
        // can already be an existing, previously-displayed game with its own correct cover, merging a
        // loser that ALSO happens to already have its own (different) previously-displayed cover from an
        // earlier scan, before the merge relationship was recognized. Filling previousGamesById[winnerId]
        // from the loser UNCONDITIONALLY would clobber the winner's own, more authoritative entry - and
        // since MigrateMergedArtwork correctly keeps the WINNER's own automatic selection as the active
        // metadata when both sides only have automatic (non-user) picks, publication would end up
        // authorizing the winner's metadata while displaying the loser's (wrong) pixels.
        var winnerIcon = new BitmapImage();
        var loserIcon = new BitmapImage();
        var winnerSelection = MakeAutomaticSelection("winner-provider-id");
        var loserSelection = MakeAutomaticSelection("loser-provider-id");

        var winner = MakeGame("ea-awayout", source: GameSource.Ea);
        winner.Icon = winnerIcon;
        winner.IsCoverArt = true;
        var loser = MakeGame("manual-haze1", source: GameSource.Manual);
        loser.Icon = loserIcon;
        loser.IsCoverArt = true;
        _sut.SimulateRefreshResult([winner, loser]);
        _sut.SetArtworkForTest("ea-awayout", winnerSelection, revision: 0);
        _sut.SetArtworkForTest("manual-haze1", loserSelection, revision: 0);

        // A later scan recognizes the relationship and merges them - a brand-new GameEntry instance for
        // the winner's id, exactly as a real scan always publishes, with no fresh unit for it this time
        // (ReconcileArtwork's own "carried-forward pixels" path - the simplest reproduction of the shared
        // bug, needing no identity machinery at all).
        var freshWinner = MakeGame("ea-awayout", source: GameSource.Ea);
        await _sut.ApplyScanResultAsync(new ScanResult(
            Games: [freshWinner],
            NewDateAddedByGameId: new Dictionary<string, DateTime>(),
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-haze1"] = "ea-awayout" },
            ArtworkResultsByGameId: []));

        // The metadata stayed the winner's own (MigrateMergedArtwork's existing, correct precedence)...
        Assert.Same(winnerSelection, _sut.GetOverride("ea-awayout")!.Artwork);
        // ...and the DISPLAYED pixels must agree with that metadata - the winner's own icon, never the
        // loser's.
        Assert.Same(winnerIcon, freshWinner.Icon);
        Assert.NotSame(loserIcon, freshWinner.Icon);
    }
}
