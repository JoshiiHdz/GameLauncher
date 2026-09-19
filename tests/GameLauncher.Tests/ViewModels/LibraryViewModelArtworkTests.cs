using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>
/// Covers two things: ApplyScanResultAsync's per-game artwork reconciliation (a live user selection
/// always wins over whatever the scan worker baked into the fresh GameEntry; a current automatic result
/// gets its metadata persisted alongside the already-correct displayed image; a STALE automatic result -
/// computed against a revision the live override has since moved past - is neither displayed nor
/// persisted, UNLESS the live override already has something newer, in which case THAT is what's
/// restored instead of blanking to the exe icon), and Change Cover/Reset's own commit discipline
/// (synchronous, revalidated, rollback-safe, revision-guarded against a scan racing it).
///
/// Every test here uses an ISOLATED asset-store directory (via AssetStoreDirOverrideForTest) instead of
/// the real %AppData%\GameLauncher\CustomCovers - a real, confirmed gap in an earlier version of this
/// suite wrote real files there, including from the two SaveFails tests (which by design can't know the
/// staged AssetId to clean up on their own, since a rolled-back commit never exposes it) and from the
/// "game no longer exists" test (which stages an asset before discovering the game is gone). Disposing
/// the isolated directory wholesale means every one of those cases - known or not - is actually cleaned
/// up, not just the ones this file happened to remember to track by hand.
///
/// Reset tests inject AutomaticCoverArtLookupForTest instead of letting ResetCoverToAutomaticAsync reach
/// CoverArtService.Apply for real - relying on "no SteamGridDB key is configured" was never actually
/// true in every checkout (a real default-api-key.txt can be embedded at build time), so a test that
/// depended on that could have silently made a real network call.
/// </summary>
public class LibraryViewModelArtworkTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _assetStoreDir;
    private readonly LibraryViewModel _sut;
    private readonly List<string> _tempFiles = new();

    public LibraryViewModelArtworkTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
        _assetStoreDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir))
        {
            AssetStoreDirOverrideForTest = _assetStoreDir,
            // Never reach the real automatic matcher (network, or this checkout's possibly-real embedded
            // SteamGridDB key) unless a specific test overrides this itself.
            AutomaticCoverArtLookupForTest = (_, _) => null,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
        if (File.Exists(_dataDir))
            File.Delete(_dataDir);

        if (Directory.Exists(_assetStoreDir))
            Directory.Delete(_assetStoreDir, recursive: true);

        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static GameEntry MakeGame(string id = "manual-test") => new()
    {
        Id = id,
        Name = "Test Game",
        ExecutablePath = @"C:\Games\TestGame\game.exe",
        InstallDir = @"C:\Games\TestGame",
        Source = GameSource.Manual,
    };

    private static byte[] MakePng(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private string MakeTempImageFile(int width = 8, int height = 8)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-CoverInput-{Guid.NewGuid()}.png");
        File.WriteAllBytes(path, MakePng(width, height));
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Writes into THIS test's isolated asset-store directory - never the real AppData.</summary>
    private string WriteIsolatedAsset(byte[] bytes) => ArtworkAssetStore.Write(bytes, "png", _assetStoreDir);

    // ---- ApplyScanResultAsync: artwork reconciliation ------------------------------------------------

    [Fact]
    public async Task LiveUserSelection_OverridesWhateverTheScanWorkerBaked()
    {
        var assetId = WriteIsolatedAsset(MakePng());
        var game = MakeGame();
        var workerIcon = new BitmapImage(); // what the scan worker baked in before this publish
        game.Icon = workerIcon;
        game.IsCoverArt = false;

        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true }, revision: 1);

        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 1, AutomaticResult: null) }));

        Assert.NotSame(workerIcon, game.Icon); // replaced by the stored selection, not left as the worker's guess
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public async Task CurrentAutomaticResult_RevisionMatches_PersistsMetadata_LeavesWorkersIconAlone()
    {
        var game = MakeGame();
        var workerIcon = new BitmapImage();
        game.Icon = workerIcon;
        game.IsCoverArt = true; // the worker already computed this correctly

        var automatic = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle" };

        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 0, AutomaticResult: automatic) }));

        Assert.Same(workerIcon, game.Icon); // untouched - the worker's own result was already current and correct
        Assert.Same(automatic, _sut.GetOverride(game.Id)!.Artwork); // but its evidence is now persisted, not just displayed
    }

    [Fact]
    public async Task CurrentAutomaticResult_ReplacesAnOlderStaleAutomaticRecord_NotJustWhenOverrideArtworkWasNull()
    {
        // A real gap: the metadata sync used to only fire when over.Artwork was null, so an OLDER
        // automatic result (A) stayed recorded forever once ANY automatic result had ever been persisted,
        // even after a later, current, successful scan found a DIFFERENT one (B) or found nothing at all -
        // the settings.json record would silently disagree with what's actually on screen.
        var game = MakeGame();
        var stale = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle", ProviderGameId = "A" };
        _sut.SetArtworkForTest(game.Id, stale, revision: 0); // not user-selected - a previous automatic result

        var fresh = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle", ProviderGameId = "B" };
        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 0, AutomaticResult: fresh) }));

        Assert.Same(fresh, _sut.GetOverride(game.Id)!.Artwork);
    }

    [Fact]
    public async Task CurrentResult_NoAutomaticMatchFound_ClearsAPreviouslyRecordedAutomaticSelection()
    {
        // Same gap, the "found nothing this time" half: the worker fell back to the exe icon (already
        // correctly reflected in game.Icon/IsCoverArt by the time this runs), but a stale automatic
        // record from a PREVIOUS scan must not keep describing artwork that isn't shown anymore.
        var game = MakeGame();
        game.IsCoverArt = false; // the worker's own fallback result, already current and correct
        var stale = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle" };
        _sut.SetArtworkForTest(game.Id, stale, revision: 0);

        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 0, AutomaticResult: null) }));

        Assert.Null(_sut.GetOverride(game.Id)!.Artwork);
    }

    [Fact]
    public async Task StaleAutomaticResult_RevisionMismatch_NoLiveArtwork_FallsBackToIcon_DoesNotPersistMetadata()
    {
        // Simulates: a Change Cover/Reset bumped ArtworkRevision to 1 WHILE a scan (which captured
        // AsOfRevision 0 at its own start) was still running - the scan's result is stale by the time
        // this publish runs, and nothing is currently selected to restore instead.
        var game = MakeGame();
        var workerIcon = new BitmapImage(); // the worker's now-stale guess, computed against revision 0
        game.Icon = workerIcon;
        game.IsCoverArt = true;

        _sut.SetArtworkForTest(game.Id, artwork: null, revision: 1); // something else already moved the revision

        var staleAutomatic = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle" };

        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 0, AutomaticResult: staleAutomatic) }));

        Assert.NotSame(workerIcon, game.Icon); // stale pixels rejected - fell back to the exe icon instead
        Assert.Null(_sut.GetOverride(game.Id)!.Artwork); // stale metadata rejected too - override left exactly as it was
    }

    [Fact]
    public async Task StaleAutomaticResult_ButLiveArtworkAlreadyCurrentFromANewerCommit_CarriesForwardTheNewerDisplayedCover()
    {
        // The real, confirmed sequence this covers: (1) a scan starts, capturing AsOfRevision 1 for this
        // game; (2) WHILE it's still running, Reset commits (revision -> 2) and its own immediate lookup
        // completes, correctly updating the THEN-live GameEntry instance's Icon; (3) the older scan
        // finally publishes, replacing EVERY GameEntry instance wholesale (ReplaceAllGames) with its own,
        // now-stale guesses. Without carrying the newer result forward, reconciliation would blank the
        // brand-new instance back to the exe icon, silently discarding a result that had already landed
        // correctly - with no pending Reset lookup left to repair it afterwards.
        var game = MakeGame();
        var newerIcon = new BitmapImage(); // what Reset's own completed lookup already applied
        game.Icon = newerIcon;
        game.IsCoverArt = true;
        _sut.SimulateRefreshResult([game]); // becomes the "previous" live instance ApplyScanResultAsync must fall back to

        var staleWorkerInstance = MakeGame(); // the OLD, still-in-flight scan's own fresh instance for the same id
        staleWorkerInstance.Icon = new BitmapImage();
        staleWorkerInstance.IsCoverArt = true;

        _sut.SetArtworkForTest(game.Id,
            new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle" }, revision: 2);

        await _sut.ApplyScanResultAsync(new ScanResult([staleWorkerInstance], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 1, AutomaticResult: null) }));

        Assert.Same(newerIcon, staleWorkerInstance.Icon); // carried forward, not blanked to the exe icon
        Assert.True(staleWorkerInstance.IsCoverArt);
    }

    [Fact]
    public async Task NoScanResultForThisGame_TreatedAsStale_NotAsCurrent()
    {
        // Defensive case: a game.Id with no corresponding entry at all in ArtworkResultsByGameId must
        // never be silently treated as "revision 0 is current" - only an explicit, present result can be.
        var game = MakeGame();
        var workerIcon = new BitmapImage();
        game.Icon = workerIcon;

        await _sut.ApplyScanResultAsync(new ScanResult([game], [], [], [], new Dictionary<string, ArtworkApplyResult>()));

        Assert.NotSame(workerIcon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public async Task IconFallbackDuringReconciliation_ThrowingIconExtraction_DoesNotAbortPublishForOtherGames()
    {
        // Point: the fallback in the "nothing currently selected" reconciliation branch must be crash-
        // isolated exactly like every other icon fallback in this codebase - a throwing icon extractor
        // for one game must not abort ApplyScanResultAsync and leave every OTHER game's reconciliation
        // (and Games/FavoriteGames/etc. population) never having run.
        var throwing = MakeGame("throws");
        var fine = MakeGame("fine");
        _sut.IconFallbackForTest = g => g.Id == "throws"
            ? throw new InvalidOperationException("simulated icon extraction failure")
            : new BitmapImage();

        var exception = await Record.ExceptionAsync(() => _sut.ApplyScanResultAsync(
            new ScanResult([throwing, fine], [], [], [], new Dictionary<string, ArtworkApplyResult>())));

        Assert.Null(exception);
        Assert.Null(throwing.Icon); // both attempts failed for this one - left null, not left stale
        Assert.False(throwing.IsCoverArt);
        Assert.NotNull(fine.Icon); // the other game's own reconciliation still ran normally
    }

    // ---- ApplyScanResultAsync: controlled interleaving during its PREPARE/decode window ---------------
    //
    // DuringCoverDecodeForTest fires deterministically right after PREPARE finishes and right before
    // PUBLISH starts - the exact window a real Change Cover/Reset commit, ToggleFavorite click, or
    // superseding refresh could land in production while a scan is mid-decode.

    [Fact]
    public async Task ApplyScanResultAsync_ChangeCoverHappensDuringDecode_NewerSelectionWins_NotTheStalePreparedOne()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        // AssetA and the eventual AssetB are distinguishable by ASPECT RATIO, not just AssetId or width -
        // CoverArtDecoder.Decode forces DecodePixelWidth=320 for every image regardless of its original
        // size, so two square images (A=8x8, a differently-sized square) would both decode to 320x320 and
        // be indistinguishable by dimensions alone. A's aspect ratio (1:1, decodes to 320x320) versus B's
        // (2:1, decodes to 320x160) survives that resize and proves the actually-DISPLAYED pixels are B's,
        // not just that the metadata says so (a stale prepared decode could otherwise still get applied
        // while the metadata correctly says B).
        var assetA = WriteIsolatedAsset(MakePng(width: 8, height: 8));
        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { AssetId = assetA, AssetExtension = "png", IsUserSelected = true }, revision: 1);

        // The scan's own fresh instance for the same id - PREPARE will queue AssetA for it.
        var freshGame = MakeGame();
        var scanResult = new ScanResult([freshGame], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 1, AutomaticResult: null) });

        string? newAssetId = null;
        _sut.DuringCoverDecodeForTest = async () =>
        {
            // Simulates the user picking a DIFFERENT cover while the scan was mid-decode of AssetA.
            var imagePath = MakeTempImageFile(width: 40, height: 20);
            var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);
            Assert.Equal(ArtworkChangeOutcome.Success, outcome);
            newAssetId = _sut.GetOverride(game.Id)!.Artwork!.AssetId;
        };

        await _sut.ApplyScanResultAsync(scanResult);

        Assert.NotNull(newAssetId);
        Assert.NotEqual(assetA, newAssetId);
        Assert.Equal(newAssetId, _sut.GetOverride(game.Id)!.Artwork!.AssetId); // still the newer pick - not reverted to AssetA
        Assert.NotNull(freshGame.Icon);
        Assert.True(freshGame.IsCoverArt);
        Assert.Equal(160, freshGame.Icon!.PixelHeight); // B's 2:1 aspect (320x160) - not A's 1:1 aspect (320x320)
    }

    [Fact]
    public async Task ApplyScanResultAsync_ResetHappensDuringDecode_ClearedSelectionWins_NotTheStalePreparedOne()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var assetA = WriteIsolatedAsset(MakePng());
        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { AssetId = assetA, AssetExtension = "png", IsUserSelected = true }, revision: 1);

        var freshGame = MakeGame();
        var scanResult = new ScanResult([freshGame], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 1, AutomaticResult: null) });

        _sut.DuringCoverDecodeForTest = async () =>
        {
            var outcome = await _sut.ResetCoverToAutomaticAsync(game.Id);
            Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        };

        await _sut.ApplyScanResultAsync(scanResult);

        Assert.Null(_sut.GetOverride(game.Id)!.Artwork); // Reset's cleared state - not overwritten by the stale AssetA decode
        Assert.False(freshGame.IsCoverArt);
    }

    [Fact]
    public async Task ApplyScanResultAsync_SupersededDuringDecode_DoesNotPublish()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var assetA = WriteIsolatedAsset(MakePng());
        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { AssetId = assetA, AssetExtension = "png", IsUserSelected = true }, revision: 1);

        var freshGame = MakeGame();
        var scanResult = new ScanResult([freshGame], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = new(AsOfRevision: 1, AutomaticResult: null) });

        var ownershipToken = _sut.SetRefreshOwnershipForTest();
        _sut.DuringCoverDecodeForTest = () =>
        {
            _sut.SetRefreshOwnershipForTest(); // a newer refresh takes ownership before this one can publish
            return Task.CompletedTask;
        };

        var published = await _sut.ApplyScanResultAsync(scanResult, ownershipToken);

        Assert.False(published);
        Assert.Empty(_sut.Games); // ApplyFilter (part of PUBLISH) never ran - proves publication was skipped
        Assert.Empty(_sut.FavoriteGames);
    }

    [Fact]
    public async Task ApplyScanResultAsync_ToggleFavoriteDuringDecode_IsReflectedInPublishedCollections_NotReverted()
    {
        // The real, confirmed bug this covers: an earlier version split _allGames/override reconciliation
        // (before the decode await) from Games/FavoriteGames population (ApplyFilter, after it, in the
        // caller). A card still bound to the OLD instance during that gap could click Favorite, mutating
        // the old instance and settings - then see the newly-published Games/FavoriteGames repopulated
        // from the NEW instances, already reconciled before the click and so untouched by it, making the
        // click appear to silently revert.
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]); // "game" is the currently-bound instance, Favorite still false

        var freshGame = MakeGame(); // the scan's own new instance for the same id
        var scanResult = new ScanResult([freshGame], [], [], [], new Dictionary<string, ArtworkApplyResult>());

        _sut.DuringCoverDecodeForTest = () =>
        {
            _sut.ToggleFavoriteCommand.Execute(game); // the click, against the still-bound OLD instance
            return Task.CompletedTask;
        };

        await _sut.ApplyScanResultAsync(scanResult);

        Assert.True(game.Favorite); // the click itself took effect on the instance it was bound to
        Assert.True(_sut.GetOverride(game.Id)!.Favorite); // persisted correctly

        // Specifically the NEW, post-scan instance - not merely "something with a matching Id", which
        // ToggleFavorite's OWN internal ApplyFilter call could already have added for the OLD instance
        // regardless of whether ApplyScanResultAsync ever re-ran ApplyFilter itself afterward. Proving
        // freshGame itself (by reference) is what's actually published is what makes this assertion
        // meaningful, not just "some entry with this id is somewhere in the list".
        Assert.True(freshGame.Favorite);
        Assert.Contains(freshGame, (IEnumerable<GameEntry>)_sut.FavoriteGames);
        Assert.DoesNotContain(freshGame, (IEnumerable<GameEntry>)_sut.Games);
        Assert.DoesNotContain(game, (IEnumerable<GameEntry>)_sut.FavoriteGames); // the stale OLD instance isn't left behind either
    }

    // ---- ApplyLocalCoverImageAsync ---------------------------------------------------------------------

    [Fact]
    public async Task ApplyLocalCoverImageAsync_ValidImage_Succeeds_PersistsSelection_UpdatesCard()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        var artwork = _sut.GetOverride(game.Id)?.Artwork;
        Assert.NotNull(artwork);
        Assert.True(artwork!.IsUserSelected);
        Assert.Equal(ArtworkProvider.UserLocalFile, artwork.Provider);
        Assert.True(game.IsCoverArt);
        Assert.NotNull(game.Icon);
        Assert.Equal(1, _sut.GetOverride(game.Id)!.ArtworkRevision);
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_CommitAppliesThePreDecodedBitmap_WithoutRereadingTheJustWrittenAsset()
    {
        // Proves CommitArtworkChange's preparedIcon path is real, not just present in source: the staged
        // asset file is deleted the instant it's written (before CommitArtworkChange ever runs) - if
        // commit tried to re-read it, it would find nothing and fall back to the exe icon (IsCoverArt
        // false) instead of succeeding with the real cover, since ApplyStoredSafely's own crash isolation
        // treats a missing asset as "retain the selection, show the icon", never as a failure to report.
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();
        _sut.AfterAssetWrittenForTest = assetId =>
        {
            Assert.True(ArtworkAssetStore.TryResolvePath(assetId, "png", out var path, _assetStoreDir));
            File.Delete(path);
        };

        var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.True(game.IsCoverArt); // the pre-decoded bitmap was applied directly - no re-read was attempted
        Assert.NotNull(game.Icon);
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_RevisionOneBelowExhaustion_CommitSucceeds_ReachesTheLastValidValue()
    {
        // CommitArtworkChange must use the same TryGetNextRevision the merge path uses - a separate,
        // real, confirmed inconsistency (an earlier version still used a bare "previousRevision + 1"
        // here). One below the ceiling is still a perfectly ordinary, successful commit.
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        _sut.SetArtworkForTest(game.Id, artwork: null, revision: long.MaxValue - 1);
        var imagePath = MakeTempImageFile();

        var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.Equal(long.MaxValue, _sut.GetOverride(game.Id)!.ArtworkRevision);
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_RevisionExhausted_RejectsTheCommit_LeavesOverrideCompletelyUnchanged()
    {
        // The real property this closes: an earlier version SATURATED at long.MaxValue instead of
        // rejecting - repeatedly returning the same long.MaxValue meant a NEW, real change made after
        // saturation was indistinguishable, by revision alone, from the state that existed before it. An
        // outstanding scan/lookup that had already captured long.MaxValue as "current" would keep
        // matching it forever, even across a change it never actually saw. Rejecting instead guarantees
        // that if a mutation is accepted, its resulting revision is provably distinct from anything
        // captured before it - and if it can't be, NOTHING about the live state changes at all, so
        // whatever was already correctly "current" simply stays correctly current. Verified here by
        // asserting the override is untouched - same reference, same revision - not merely that the
        // returned outcome says "rejected".
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var previousArtwork = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle", ProviderGameId = "pre-exhaustion" };
        _sut.SetArtworkForTest(game.Id, previousArtwork, revision: long.MaxValue);
        var imagePath = MakeTempImageFile();

        var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.RevisionExhausted, outcome);
        Assert.Same(previousArtwork, _sut.GetOverride(game.Id)!.Artwork); // not replaced by the attempted new selection
        Assert.Equal(long.MaxValue, _sut.GetOverride(game.Id)!.ArtworkRevision); // not bumped/changed at all
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_RevisionExhausted_RejectsTheCommit_NeverStartsTheAutomaticLookup()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var previousArtwork = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        _sut.SetArtworkForTest(game.Id, previousArtwork, revision: long.MaxValue);
        var lookupCalls = 0;
        _sut.AutomaticCoverArtLookupForTest = (_, _) => { lookupCalls++; return null; };

        var outcome = await _sut.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.RevisionExhausted, outcome);
        Assert.Same(previousArtwork, _sut.GetOverride(game.Id)!.Artwork); // the selection Reset was trying to clear is untouched
        Assert.Equal(long.MaxValue, _sut.GetOverride(game.Id)!.ArtworkRevision);
        Assert.Equal(0, lookupCalls); // rejected before the commit succeeded - the immediate lookup never starts
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_InvalidImage_ReturnsInvalidImage_NoOverrideCreated()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var badPath = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Bad-{Guid.NewGuid()}.png");
        File.WriteAllBytes(badPath, [1, 2, 3]);
        _tempFiles.Add(badPath);

        var outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, badPath);

        Assert.Equal(ArtworkChangeOutcome.InvalidImage, outcome);
        Assert.Null(_sut.GetOverride(game.Id));
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_GameNoLongerExists_ReturnsGameNoLongerExists_NoOverrideCreated_NoAssetLeftBehind()
    {
        // No SimulateRefreshResult at all - the game id is not in _allGames, simulating a refresh that
        // replaced/removed it while an async prepare phase (validation/staging) was in flight. The image
        // itself is valid, so validation/staging succeed BEFORE CommitArtworkChange discovers the game is
        // gone - a real, confirmed leak in an earlier version of this test suite only tracked assets from
        // the two SaveFails cases, missing that this path stages one too. The isolated per-test asset
        // directory (deleted wholesale in Dispose) makes this assertion meaningful either way.
        var imagePath = MakeTempImageFile();

        var outcome = await _sut.ApplyLocalCoverImageAsync("never-existed", imagePath);

        Assert.Equal(ArtworkChangeOutcome.GameNoLongerExists, outcome);
        Assert.Null(_sut.GetOverride("never-existed")); // must not resurrect/create an override for a dead id
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_AssetStoreWriteFails_ReturnsStorageFailed_NoOverrideCreated()
    {
        // Forces ArtworkAssetStore.Write to fail deterministically: pointing its store directory at an
        // existing FILE (not a directory) makes Directory.CreateDirectory throw inside Write. Nothing was
        // ever staged, so - unlike SaveFailed - there is no override mutation to roll back at all; the
        // point is simply that this is reported as a distinct, controlled failure instead of an unhandled
        // exception escaping ApplyLocalCoverImageAsync.
        var blockedAssetDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-BlockedAssets-" + Guid.NewGuid());
        File.WriteAllText(blockedAssetDir, "blocking file");
        _tempFiles.Add(blockedAssetDir);
        _sut.AssetStoreDirOverrideForTest = blockedAssetDir;

        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        ArtworkChangeOutcome outcome = default;
        var exception = await Record.ExceptionAsync(async () => outcome = await _sut.ApplyLocalCoverImageAsync(game.Id, imagePath));

        Assert.Null(exception);
        Assert.Equal(ArtworkChangeOutcome.StorageFailed, outcome);
        Assert.Null(_sut.GetOverride(game.Id));
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_SaveFails_RollsBackToNoOverride()
    {
        // Forces SettingsService.Save to fail deterministically: _dataDir is a FILE, not a directory, so
        // Directory.CreateDirectory(_dataDir) inside Save() throws.
        var blockedDataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Blocked-" + Guid.NewGuid());
        File.WriteAllText(blockedDataDir, "blocking file");
        _tempFiles.Add(blockedDataDir);
        var sut = new LibraryViewModel(new SettingsService(blockedDataDir), new PendingUpdateNotesService(blockedDataDir))
        {
            AssetStoreDirOverrideForTest = _assetStoreDir,
        };

        var game = MakeGame();
        sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        var outcome = await sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.SaveFailed, outcome);
        Assert.Null(sut.GetOverride(game.Id)); // rolled back - no override left dangling
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_SaveFails_RollsBackExistingOverride_ToPreviousArtworkAndRevision()
    {
        var blockedDataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Blocked-" + Guid.NewGuid());
        File.WriteAllText(blockedDataDir, "blocking file");
        _tempFiles.Add(blockedDataDir);
        var sut = new LibraryViewModel(new SettingsService(blockedDataDir), new PendingUpdateNotesService(blockedDataDir))
        {
            AssetStoreDirOverrideForTest = _assetStoreDir,
        };

        var game = MakeGame();
        sut.SimulateRefreshResult([game]);
        var previousArtwork = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        sut.SetArtworkForTest(game.Id, previousArtwork, revision: 3);

        var imagePath = MakeTempImageFile();
        var outcome = await sut.ApplyLocalCoverImageAsync(game.Id, imagePath);

        Assert.Equal(ArtworkChangeOutcome.SaveFailed, outcome);
        Assert.Same(previousArtwork, sut.GetOverride(game.Id)!.Artwork);
        Assert.Equal(3, sut.GetOverride(game.Id)!.ArtworkRevision);
    }

    [Fact]
    public async Task ApplyLocalCoverImageAsync_ConcurrentCallsForSameGame_SecondIsRejectedAsAlreadyInProgress()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var imagePath = MakeTempImageFile();

        var task1 = _sut.ApplyLocalCoverImageAsync(game.Id, imagePath);
        var task2 = _sut.ApplyLocalCoverImageAsync(game.Id, imagePath); // started before task1's guard is released

        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(ArtworkChangeOutcome.AlreadyInProgress, results);
        Assert.Contains(ArtworkChangeOutcome.Success, results);
    }

    // ---- ResetCoverToAutomaticAsync -----------------------------------------------------------------

    [Fact]
    public async Task ResetCoverToAutomaticAsync_ClearsSelection_NoAutomaticMatchFound_Succeeds()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var existing = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        _sut.SetArtworkForTest(game.Id, existing, revision: 1);

        var outcome = await _sut.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        // AutomaticCoverArtLookupForTest (set in the constructor) deterministically finds nothing, never
        // touching the network or depending on whether this checkout has a real embedded SteamGridDB key.
        Assert.Null(_sut.GetOverride(game.Id)!.Artwork);
        Assert.True(_sut.GetOverride(game.Id)!.ArtworkRevision > 1);
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_ImmediateLookupFindsAMatch_PersistsItWithoutFurtherRevisionBump()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var existing = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        _sut.SetArtworkForTest(game.Id, existing, revision: 1);

        var found = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle" };
        _sut.AutomaticCoverArtLookupForTest = (g, _) =>
        {
            g.IsCoverArt = true; // mirrors what a real CoverArtService.Apply implementation does to its target
            return found;
        };

        var outcome = await _sut.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        var afterReset = _sut.GetOverride(game.Id)!;
        Assert.Same(found, afterReset.Artwork);
        var revisionRightAfterReset = 2; // previous (1) + 1 for the reset commit itself
        Assert.Equal(revisionRightAfterReset, afterReset.ArtworkRevision); // the immediate lookup must NOT bump it again
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_ImmediateLookupFindsNoMatch_ButConcurrentScanAlreadyRecordedAutomaticMetadata_ClearsIt()
    {
        // The real, confirmed ordering this covers: Reset's own commit lands at revision R. A CONCURRENT
        // scan, running independently, also reconciles this exact game at revision R (unchanged, since
        // ReconcileArtwork's "current" branch never bumps revision) and publishes its own automatic
        // metadata. THEN Reset's own immediate lookup (started right after its commit) finishes and finds
        // no match. Without syncing metadata on the no-match path too, the card would correctly fall back
        // to the exe icon here while over.Artwork kept describing the scan's now-not-displayed match - a
        // metadata/display mismatch.
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        var existing = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        _sut.SetArtworkForTest(game.Id, existing, revision: 1);

        var concurrentScanResult = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, MatchMethod = "ExactTitle", ProviderGameId = "concurrent-scan-match" };
        _sut.AutomaticCoverArtLookupForTest = (_, _) =>
        {
            // Simulates the concurrent scan landing while Reset's own lookup is still in flight, at
            // whatever revision Reset's own commit just established (never guessed/hardcoded here).
            var currentRevision = _sut.GetOverride(game.Id)!.ArtworkRevision;
            _sut.SetArtworkForTest(game.Id, concurrentScanResult, currentRevision);
            return null; // Reset's own lookup: no match found
        };

        var outcome = await _sut.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.Null(_sut.GetOverride(game.Id)!.Artwork); // Reset's current, no-match result wins - not left stale
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_SaveFails_PreservesSelectionRevisionAndDisplayedImage_NeverStartsTheLookup()
    {
        // The Reset counterpart to ApplyLocalCoverImageAsync_SaveFails_RollsBackExistingOverride - not
        // previously covered on its own. CommitArtworkChange's SaveFailed path never reaches the block
        // that touches game.Icon/IsCoverArt at all, so the displayed image must be untouched, not merely
        // "rolled back to something equivalent" - and ResetCoverToAutomaticAsync's own early-return on a
        // non-Success outcome means the immediate automatic lookup must never even start.
        var blockedDataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Blocked-" + Guid.NewGuid());
        File.WriteAllText(blockedDataDir, "blocking file");
        _tempFiles.Add(blockedDataDir);
        var sut = new LibraryViewModel(new SettingsService(blockedDataDir), new PendingUpdateNotesService(blockedDataDir))
        {
            AssetStoreDirOverrideForTest = _assetStoreDir,
        };

        var game = MakeGame();
        sut.SimulateRefreshResult([game]);
        var assetId = WriteIsolatedAsset(MakePng());
        var existingSelection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };
        sut.SetArtworkForTest(game.Id, existingSelection, revision: 3);
        // A concrete, known "before" state to prove survives untouched - not just "some non-null icon".
        Assert.True(CoverArtService.ApplyStored(game, existingSelection, _assetStoreDir));
        var displayedIconBeforeReset = game.Icon;
        Assert.NotNull(displayedIconBeforeReset);

        var lookupCalls = 0;
        sut.AutomaticCoverArtLookupForTest = (_, _) => { lookupCalls++; return null; };

        var outcome = await sut.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.SaveFailed, outcome);
        Assert.Same(existingSelection, sut.GetOverride(game.Id)!.Artwork); // selection preserved
        Assert.Equal(3, sut.GetOverride(game.Id)!.ArtworkRevision); // revision preserved
        Assert.Same(displayedIconBeforeReset, game.Icon); // displayed image untouched - not re-decoded, not cleared
        Assert.Equal(0, lookupCalls); // rejected before the commit succeeded - the immediate lookup never starts
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_GameNoLongerExists_ReturnsGameNoLongerExists()
    {
        var outcome = await _sut.ResetCoverToAutomaticAsync("never-existed");
        Assert.Equal(ArtworkChangeOutcome.GameNoLongerExists, outcome);
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_ConcurrentWithItself_SecondIsRejectedAsAlreadyInProgress()
    {
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { IsUserSelected = true, AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png" }, revision: 1);

        var task1 = _sut.ResetCoverToAutomaticAsync(game.Id);
        var task2 = _sut.ResetCoverToAutomaticAsync(game.Id);

        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(ArtworkChangeOutcome.AlreadyInProgress, results);
        Assert.Contains(ArtworkChangeOutcome.Success, results);
    }

    [Fact]
    public async Task ResetCoverToAutomaticAsync_IconFallbackThrows_StillCommitsAndRunsTheImmediateLookup()
    {
        // Point 2's second consequence: Reset's own commit (which clears Artwork -> falls to the plain
        // icon-fallback branch inside CommitArtworkChange) must not let a throwing icon extractor abort
        // the reset outright, or skip the immediate automatic lookup that follows it.
        var game = MakeGame();
        _sut.SimulateRefreshResult([game]);
        _sut.SetArtworkForTest(game.Id, new ArtworkSelection { IsUserSelected = true, AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png" }, revision: 1);
        _sut.IconFallbackForTest = _ => throw new InvalidOperationException("simulated icon extraction failure");

        var lookupRan = false;
        _sut.AutomaticCoverArtLookupForTest = (_, _) => { lookupRan = true; return null; };

        ArtworkChangeOutcome outcome = default;
        var exception = await Record.ExceptionAsync(async () => outcome = await _sut.ResetCoverToAutomaticAsync(game.Id));

        Assert.Null(exception); // never throws, despite the icon fallback throwing internally
        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.True(lookupRan); // the immediate lookup still ran despite the icon fallback throwing
        Assert.Null(_sut.GetOverride(game.Id)!.Artwork);
    }

    // ---- End-to-end persistence: a real settings.json + a real (isolated) asset store, across a ------
    // ---- fresh LibraryViewModel instance - not just in-memory continuity within one _sut. -------------

    [Fact]
    public async Task SuccessfulSelection_SurvivesSaveThenFreshInstanceThenScanPublication_UsingOnlyIsolatedStorage()
    {
        // Every other test in this class reuses the SAME _sut/_settings object end to end, which never
        // actually proves anything survived a real save+reload round trip - only that the in-memory
        // object graph was mutated correctly. This test constructs a completely SEPARATE LibraryViewModel
        // (simulating an app restart) pointed at the exact same isolated settings/asset directories the
        // first one wrote to, then publishes a scan through it, and never wires up anything resembling a
        // network path anywhere in the chain: AutomaticCoverArtLookupForTest is set to throw on both
        // instances, and ApplyScanResultAsync itself never calls the automatic matcher directly (that's
        // GameScannerService's job, entirely bypassed here by constructing the ScanResult by hand).
        var dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Persist-" + Guid.NewGuid());
        var assetDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-PersistAssets-" + Guid.NewGuid());
        try
        {
            Func<GameEntry, string?, ArtworkSelection?> neverReachNetwork =
                (_, _) => throw new InvalidOperationException("must never reach the automatic matcher/network in this test");

            var firstInstance = new LibraryViewModel(new SettingsService(dataDir), new PendingUpdateNotesService(dataDir))
            {
                AssetStoreDirOverrideForTest = assetDir,
                AutomaticCoverArtLookupForTest = neverReachNetwork,
            };
            var game = MakeGame();
            firstInstance.SimulateRefreshResult([game]);
            var imagePath = MakeTempImageFile(width: 40, height: 20);

            var outcome = await firstInstance.ApplyLocalCoverImageAsync(game.Id, imagePath);
            Assert.Equal(ArtworkChangeOutcome.Success, outcome);
            var assetId = firstInstance.GetOverride(game.Id)!.Artwork!.AssetId;

            // A brand new instance, loading only what the first one persisted to disk - nothing from the
            // first instance's own memory (not even a shared SettingsService) is reused past this point.
            var secondInstance = new LibraryViewModel(new SettingsService(dataDir), new PendingUpdateNotesService(dataDir))
            {
                AssetStoreDirOverrideForTest = assetDir,
                AutomaticCoverArtLookupForTest = neverReachNetwork,
            };

            var loadedOverride = secondInstance.GetOverride(game.Id);
            Assert.NotNull(loadedOverride);
            Assert.NotNull(loadedOverride!.Artwork);
            Assert.True(loadedOverride.Artwork!.IsUserSelected);
            Assert.Equal(assetId, loadedOverride.Artwork.AssetId);

            // A scan publish through the fresh instance - the persisted selection's actual asset bytes
            // must be correctly re-decoded from the isolated store, not merely have surviving metadata.
            var freshGame = MakeGame();
            var scanResult = new ScanResult([freshGame], [], [], [], new Dictionary<string, ArtworkApplyResult>());
            await secondInstance.ApplyScanResultAsync(scanResult);

            Assert.NotNull(freshGame.Icon);
            Assert.True(freshGame.IsCoverArt);
            Assert.Equal(160, freshGame.Icon!.PixelHeight); // the real, persisted 40x20 image (320x160 after decode) - not a placeholder
        }
        finally
        {
            if (Directory.Exists(dataDir))
                Directory.Delete(dataDir, recursive: true);
            if (Directory.Exists(assetDir))
                Directory.Delete(assetDir, recursive: true);
        }
    }
}
