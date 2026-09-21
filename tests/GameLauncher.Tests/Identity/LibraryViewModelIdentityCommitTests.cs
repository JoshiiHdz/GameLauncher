using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;
using static GameLauncher.ViewModels.LibraryViewModel;

namespace GameLauncher.Tests.Identity;

/// <summary>The automatic unit's COMMIT on the UI thread (design 6.5) and everything that races it, through the production
/// path: units are produced by the same worker method a scan uses and published through ApplyScanResultAsync. Covers the
/// two-halves algorithm (identity half first, then gates G1-G4 for the artwork half), stale results (S2, S3, S7, S21-S24),
/// the audit's acceptance cases S33 (a-d) and S34, the removal pass, and restart.
///
/// Covers are told apart by their decoded HEIGHT (a decoded cover is always 320 wide): A = 480, B = 640, C = 800.</summary>
public class LibraryViewModelIdentityCommitTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private const int HeightA = 480, HeightB = 640, HeightC = 800;

    public void Dispose() => _h.Dispose();

    private static CatalogCoverResult Cover(int sourceHeight, bool fromCache = false) =>
        new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(sourceHeight), fromCache);

    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");

    private void IgdbFinds(string id, string title = "Foo", int sourceHeight = 90)
    {
        _h.Igdb.Title = _ => CatalogSearchResult.Found(id, title);
        _h.Igdb.Cover = _ => Cover(sourceHeight);
    }

    // ---- The happy path ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AScan_ResolvesTheIdentity_PersistsIt_AndShowsTheCoverFetchedById()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());

        await _h.Scan(game);

        var over = _h.Over(game.Id)!;
        Assert.Equal("A", Assert.Single(over.Identity!.Resolved).Id);
        Assert.Equal(new IdentityKey(Cat.Igdb, "A"), over.Artwork!.DerivedFrom);
        Assert.False(over.Artwork.IsUserSelected);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        Assert.Equal(new AutomaticCommitReport(IdentityHalf.Committed, true), _h.Vm.UnitReportsForTest[game.Id]);
        Assert.Equal(1, over.IdentityRevision);   // the active identity changed: advanced
        Assert.Equal(0, over.DecisionRevision);   // an automatic write never touches the user's decisions
        Assert.Equal(0, over.ArtworkRevision);    // ...nor the artwork revision (unchanged rule)
    }

    [Fact]
    public async Task AnUnchangedResult_IsValidatedNoChange_AndAdvancesNothingAtAll()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        var generation = _h.Vm.IdentityGenerationForTest(game.Id);

        await _h.Scan(IdentityHarness.Fresh(game)); // the same inputs, the same answer, the same clock

        Assert.Equal(IdentityHalf.ValidatedNoChange, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Equal(1, _h.Over(game.Id)!.IdentityRevision);
        Assert.Equal(generation, _h.Vm.IdentityGenerationForTest(game.Id));
    }

    [Fact]
    public async Task AMetadataOnlyRefresh_AdvancesTheGeneration_ButNeitherRevision()
    {
        // A later scan restamps LastAttempt: a real write of the record (so it moves the generation and a late older unit is
        // detected), but the ACTIVE identity did not change, so IdentityRevision - and the user's DecisionRevision - stay put.
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        var generation = _h.Vm.IdentityGenerationForTest(game.Id);
        var later = Cat.Ctx([_h.Igdb, _h.Sgdb], now: () => new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = IdentityHarness.Fresh(game);

        await _h.Publish([fresh], (fresh.Id, _h.Unit(fresh, later)));

        Assert.Equal(IdentityHalf.Committed, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Equal(generation + 1, _h.Vm.IdentityGenerationForTest(game.Id));
        Assert.Equal(1, _h.Over(game.Id)!.IdentityRevision);
        Assert.Equal(0, _h.Over(game.Id)!.DecisionRevision);
    }

    [Fact]
    public async Task ARename_DoesNotChangeWhatIsSearched_OrInvalidateAnything_S1()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        var unit = _h.Unit(game);                     // the unit starts...
        game.Name = "Renamed By The User";            // ...a rename lands mid-lookup (display only)

        await _h.Publish([game], (game.Id, unit));

        Assert.Equal(IdentityHalf.Committed, _h.Vm.UnitReportsForTest[game.Id].Identity); // the result stays valid: the name was never an input
        Assert.Equal(new[] { "Foo" }, _h.Igdb.SearchedTitles);                               // the provider only ever saw the detected title
    }

    // ---- Stale results ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADetectedTitleThatChangedMidLookup_RejectsTheIdentityHalf_AndBlocksEveryArtworkPublish_S2()
    {
        IgdbFinds("A");
        var original = await _h.Add(Foo());
        var unit = _h.Unit(original);

        var rescanned = Games.Manual("manual-foo", "Foo Renamed On Disk"); // the same game, a different DETECTED title
        await _h.Publish([rescanned], (rescanned.Id, unit));

        Assert.Equal(IdentityHalf.RejectedFingerprint, _h.Vm.UnitReportsForTest[rescanned.Id].Identity);
        Assert.Null(_h.Over(rescanned.Id)?.Identity);           // nothing was written
        Assert.Null(IdentityHarness.Height(rescanned));         // and no cover was shown
    }

    [Fact]
    public async Task S34_AnIdentityHalfThatFailedValidation_BlocksBothSetAndClear_EvenWhenTheArtworkIdIsStillActive()
    {
        // The audit's acceptance test. The user confirmed IGDB 7 and its cover is showing. A rescan changes the detected
        // title, so the identity half is Rejected(fingerprint) - yet the artwork's DerivedFrom id (7) is STILL active.
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo") };
        over.Artwork = Cat.Auto(Cat.Igdb, "7");
        _h.Igdb.Cover = _ => Cover(90);
        await _h.Scan(game); // shows cover 7
        Assert.Equal(HeightA, IdentityHarness.Height(game));

        var newQuery = IdentityQuery.From(Games.Manual("manual-foo", "A Different Title"));
        Assert.True(ArtworkAuthorization.IsAuthorized(Cat.Auto(Cat.Igdb, "7"), IdentitySelection.SelectActive(over.Identity, newQuery), newQuery, over.Identity),
            "precondition: the predicate ALONE would authorize this artwork - so the test is not vacuous");

        // A SET half (a different cover for the same id) and then a CLEAR half, both computed against the old title.
        _h.Igdb.Cover = _ => Cover(120);
        var setUnit = _h.Unit(game);
        _h.Igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);
        var clearUnit = _h.Unit(game);
        Assert.Equal(ArtworkHalfKind.Set, setUnit.Unit!.Output.Artwork.Kind);

        var rescanned = Games.Manual("manual-foo", "A Different Title");
        await _h.Publish([rescanned], (rescanned.Id, setUnit));

        Assert.Equal(IdentityHalf.RejectedFingerprint, _h.Vm.UnitReportsForTest[rescanned.Id].Identity);
        Assert.False(_h.Vm.UnitReportsForTest[rescanned.Id].ArtworkPublished);
        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), over.Artwork!.DerivedFrom);   // Artwork unchanged
        Assert.NotEqual(HeightB, IdentityHarness.Height(rescanned));                 // the new pixels were never displayed

        await _h.Publish([rescanned], (rescanned.Id, clearUnit));
        Assert.NotNull(over.Artwork);                                                // ClearAutomatic did not clear either
    }

    [Fact]
    public async Task AnExhaustedIdentityRevision_RejectsTheUnit_BeforeAnythingIsMutated_AndTheCoverIsNeverShown_S19_S23()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        _h.Vm.EnsureOverrideForTest(game.Id).IdentityRevision = long.MaxValue;

        await _h.Scan(game);

        Assert.Equal(IdentityHalf.RejectedExhausted, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Null(_h.Over(game.Id)!.Identity);            // nothing written
        Assert.Null(_h.Over(game.Id)!.Artwork);
        Assert.Null(IdentityHarness.Height(game));          // bitmap dropped, never displayed
    }

    [Fact]
    public async Task ACatalogCover_OnAQuarantinedRecord_NeverPublishes_S34()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        using (var doc = JsonDocument.Parse("""{ "Junk": 1 }""")) over.Identity = new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };

        await _h.Scan(game);

        Assert.Equal(IdentityHalf.NotApplicable, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Null(over.Artwork);
        Assert.Null(IdentityHarness.Height(game));
        Assert.True(over.Identity!.IsQuarantined); // and the record itself was never touched
    }

    [Fact]
    public async Task LauncherArt_OnAStillCurrentQuarantinedRecord_MayPublish_ButNeverOnARejectedOne_S35_S47()
    {
        _h.LauncherArt = (_, _) => (TestBitmaps.Distinct(90), false);
        var game = await _h.Add(Games.Steam("100", "Foo"));
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        using (var doc = JsonDocument.Parse("""{ "Junk": 1 }""")) over.Identity = new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };

        await _h.Scan(game);

        Assert.Equal(IdentityHalf.NotApplicable, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Equal(HeightA, IdentityHarness.Height(game));           // the one defined exception
        Assert.Equal(ArtworkProvider.SteamCdn, over.Artwork!.Provider);

        // A quarantined record whose fingerprint MOVED is Rejected(fingerprint), not NotApplicable: no exception, so a NEW
        // launcher cover is not published (the last known-good frame may still be carried forward - that is a different path).
        _h.LauncherArt = (_, _) => (TestBitmaps.Distinct(120), false);
        var unit = _h.Unit(game);
        var moved = Games.Steam("100", "A Different Detected Title");
        await _h.Publish([moved], (moved.Id, unit));
        Assert.Equal(IdentityHalf.RejectedFingerprint, _h.Vm.UnitReportsForTest[moved.Id].Identity);
        Assert.False(_h.Vm.UnitReportsForTest[moved.Id].ArtworkPublished);
        Assert.NotEqual(HeightB, IdentityHarness.Height(moved));
    }

    // ---- Races ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConfirmationThatLandsWhileAUnitIsInFlight_SupersedesTheWholeUnit_S3()
    {
        IgdbFinds("A", sourceHeight: 90);
        var game = await _h.Add(Foo());
        var staleUnit = _h.Unit(game); // in flight for the identity A...

        _h.Sgdb.Cover = _ => Cover(120);
        var confirmed = await _h.Vm.ConfirmIdentityAsync(game.Id,
            new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null),
            _h.Over(game.Id)?.DecisionRevision ?? 0, _h.Over(game.Id)?.IdentityRevision ?? 0);
        Assert.Equal(IdentityChangeOutcome.Success, confirmed);

        await _h.Publish([game], (game.Id, staleUnit));       // ...and lands late

        Assert.Equal(IdentityHalf.Superseded, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.DoesNotContain(_h.Record(game.Id)!.Resolved, r => r.Id == "A");        // the old unit committed nothing
        Assert.NotEqual(HeightA, IdentityHarness.Height(game));                         // and A's pixels were never displayed
    }

    [Fact]
    public async Task AResetLookupForA_LosesToAScanThatUpgradedTheActiveIdentityToB_S21()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        _h.Igdb.Cover = _ => Cover(90);
        var resetUnit = _h.Unit(game);                               // the Reset lookup, for A

        _h.Igdb.Title = _ => CatalogSearchResult.Found("B", "Foo");
        _h.Igdb.Cover = _ => Cover(120);
        var scanUnit = _h.Unit(game);                                // a scan that resolved B
        await _h.Publish([game], (game.Id, scanUnit));
        Assert.Equal(HeightB, IdentityHarness.Height(game));

        var next = IdentityHarness.Fresh(game);                                     // (a scan publishes a NEW entry, never the one on screen)
        await _h.Publish([next], (next.Id, resetUnit));               // the older Reset result finally lands

        Assert.Equal(IdentityHalf.Superseded, _h.Vm.UnitReportsForTest[game.Id].Identity);
        Assert.Equal("B", Assert.Single(_h.Record(game.Id)!.Resolved).Id);
        Assert.Equal(HeightB, IdentityHarness.Height(next));          // A's art never displayed; B's carried forward (still authorized)
    }

    [Fact]
    public async Task TwoUnitsRacing_TheOlderLosesOnTheGeneration_AndCannotDowngradeTheTier_S24()
    {
        var steam = await _h.Add(Games.Steam("100", "Foo"));

        // Both units start from the SAME snapshot (same generation, same revisions).
        _h.Igdb.Map = _ => CatalogSearchResult.NoMatch();
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        var olderUnit = _h.Unit(steam);                               // would record A as TitleExact
        _h.Igdb.Map = _ => CatalogSearchResult.Found("A", "Foo");
        var newerUnit = _h.Unit(steam);                               // records A as IdMapped

        await _h.Publish([steam], (steam.Id, newerUnit));
        Assert.Equal(IdentityTier.IdMapped, Assert.Single(_h.Record(steam.Id)!.Resolved).Tier);

        await _h.Publish([steam], (steam.Id, olderUnit));             // the older one finally lands

        Assert.Equal(IdentityHalf.Superseded, _h.Vm.UnitReportsForTest[steam.Id].Identity);      // it loses on the generation, with no persisted counter
        Assert.Equal(IdentityTier.IdMapped, Assert.Single(_h.Record(steam.Id)!.Resolved).Tier); // never restamped downward
    }

    [Fact]
    public async Task ANewerPinnedCover_DropsTheArtworkHalf_ButAValidIdentityHalfStillApplies_S7_S22()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        var unit = _h.Unit(game);                                     // in flight, with a Set half

        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try
        {
            Assert.Equal(ArtworkChangeOutcome.Success, await _h.Vm.ApplyLocalCoverImageAsync(game.Id, png));
        }
        finally { File.Delete(png); }
        var pinnedAsset = _h.Over(game.Id)!.Artwork!.AssetId;

        await _h.Publish([game], (game.Id, unit));

        Assert.Equal(IdentityHalf.Committed, _h.Vm.UnitReportsForTest[game.Id].Identity);    // identity recorded
        Assert.Equal("A", Assert.Single(_h.Record(game.Id)!.Resolved).Id);
        Assert.Equal(pinnedAsset, _h.Over(game.Id)!.Artwork!.AssetId);                       // the pinned cover untouched
        Assert.True(_h.Over(game.Id)!.Artwork!.IsUserSelected);
        Assert.Equal(HeightC, IdentityHarness.Height(game));                                  // and still what is shown
    }

    [Fact]
    public async Task ACancelledScan_WritesNothing_S17()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _h.Unit(game, ct: cts.Token));

        Assert.Null(_h.Over(game.Id)?.Identity);
        Assert.Null(_h.Over(game.Id)?.Artwork);
    }

    [Fact]
    public async Task AnUnexpectedProviderFailure_ChangesNothing_AndNeverClearsAnExistingCover()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        _h.Igdb.Cover = _ => throw new InvalidOperationException("boom");

        await _h.Scan(game);

        Assert.NotNull(_h.Over(game.Id)!.Artwork);                                   // the persisted cover is kept
        Assert.Equal("A", Assert.Single(_h.Record(game.Id)!.Resolved).Id);           // and so is the identity
    }

    // ---- The removal pass and the audit's S33 -----------------------------------------------------------------------

    private async Task<(GameEntry Game, GameOverride Over)> AutoResolvedToA()
    {
        IgdbFinds("A", "Foo", 90);
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        return (game, _h.Over(game.Id)!);
    }

    [Fact]
    public async Task S33a_ConfirmingAConflictingSteamGridDbGame_RemovesAAndShowsB_AndAIsNeverDisplayedAgain()
    {
        var (game, over) = await AutoResolvedToA();
        _h.Sgdb.Cover = _ => Cover(120);
        var fetchedFromIgdbBefore = _h.Igdb.CoverFetches;

        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null),
            over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Equal(HeightB, IdentityHarness.Height(game));                        // B's art, not A's
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), over.Artwork!.DerivedFrom);
        Assert.Equal(fetchedFromIgdbBefore, _h.Igdb.CoverFetches);                  // A's cover was never even requested again
        Assert.False(IdentitySelection.SelectActive(over.Identity, IdentityQuery.From(game)).Entries.Single(e => e.Id == "A").ArtworkEligible);
    }

    [Fact]
    public async Task S33b_ARescan_NeverServesTheCompetitorsCachedCover()
    {
        var (game, over) = await AutoResolvedToA();
        _h.Sgdb.Cover = _ => Cover(120);
        await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null), over.DecisionRevision, over.IdentityRevision);
        _h.Igdb.Cover = _ => Cover(90, fromCache: true); // A's image is valid and sitting in a cache
        var before = _h.Igdb.CoverFetches;

        await _h.Scan(game);

        Assert.Equal(HeightB, IdentityHarness.Height(game));
        Assert.Equal(before, _h.Igdb.CoverFetches); // the confirmed catalog supplied art first, so A's cache was never consulted
    }

    [Fact]
    public async Task S33c_CarriedForwardPixels_AreNotReused_WhenTheirIdentityIsNoLongerAuthorized()
    {
        // Arrange the state directly: the card is showing A's cover (a previous frame), but the record now says the user
        // confirmed B - so A is ineligible. A rescan that produces no fresh art must NOT carry A's pixels forward.
        var game = await _h.Add(Foo());
        var previous = new GameEntry { Id = game.Id, Name = "Foo", ExecutablePath = "x", InstallDir = "x", Source = GameSource.Manual };
        previous.Icon = TestBitmaps.Distinct(90); previous.IsCoverArt = true;
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        over.Identity.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Igdb, "A", "Foo"));
        over.Artwork = Cat.Auto(Cat.Igdb, "A");
        _h.Sgdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Unavailable, null, false); // B has nothing right now
        await _h.Vm.ApplyScanResultAsync(new ScanResult([previous], [], [], [], new Dictionary<string, ArtworkApplyResult>()));

        var rescanned = Games.Manual(game.Id, "Foo");
        await _h.Publish([rescanned], (rescanned.Id, _h.Unit(rescanned)));

        Assert.Null(IdentityHarness.Height(rescanned));   // A's carried-forward frame was refused
        Assert.Null(over.Artwork);                          // and its record removed by the removal pass
    }

    [Fact]
    public async Task S33d_AfterARestart_AnUnauthorizedCoverIsNotShown_AndIsRemoved()
    {
        // A file left by an earlier session: the user confirmed B, but A's automatic cover is still recorded.
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        over.Artwork = Cat.Auto(Cat.Igdb, "A");
        _h.Sgdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Unavailable, null, false);
        _h.Igdb.Title = t => t == "Foo" ? CatalogSearchResult.Found("A", "Foo") : CatalogSearchResult.NoMatch();
        _h.Igdb.Cover = _ => Cover(90);

        var fresh = Games.Manual(game.Id, "Foo");
        await _h.Publish([fresh], (fresh.Id, _h.Unit(fresh)));

        Assert.NotEqual(HeightA, IdentityHarness.Height(fresh));
        Assert.Null(over.Artwork);
    }

    [Fact]
    public async Task S45_SameTitleDifferentId_InOneNamespace_TheCompetitorsCoverIsRemoved_AndNeverShownAgain()
    {
        var (game, over) = await AutoResolvedToA();
        _h.Igdb.Cover = id => id == "B" ? Cover(120) : Cover(90);

        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Igdb, "B", "Foo", null, null),
            over.DecisionRevision, over.IdentityRevision); // identical TITLE, different id

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Equal(HeightB, IdentityHarness.Height(game));
        var a = IdentitySelection.SelectActive(over.Identity, IdentityQuery.From(game)).Entries.Single(e => e.Id == "A");
        Assert.False(a.ArtworkEligible);
        Assert.Equal("SameNamespaceDifferentId", a.IneligibleReason);

        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo"); // even if the provider keeps saying A
        await _h.Scan(game);
        Assert.Equal(HeightB, IdentityHarness.Height(game));
    }

    [Fact]
    public async Task S49_ConfirmingACatalogIdentity_RemovesUnprovenLauncherArt_WithoutRequiringAPin()
    {
        _h.LauncherArt = (_, _) => (TestBitmaps.Distinct(90), false);
        _h.Igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);
        var game = await _h.Add(Games.Steam("100", "Foo"));
        await _h.Scan(game);
        var over = _h.Over(game.Id)!;
        Assert.Equal(ArtworkProvider.SteamCdn, over.Artwork!.Provider);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        var launcherCalls = 0;
        _h.LauncherArt = (_, _) => { launcherCalls++; return (TestBitmaps.Distinct(90), false); };

        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Igdb, "7", "Foo", null, null),
            over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Null(over.Artwork);                          // removed in the Confirm transaction
        Assert.Null(IdentityHarness.Height(game));          // the icon (or the confirmed identity's own art) - never the unproven launcher cover
        Assert.Equal(0, launcherCalls);                     // and the follow-up never fetched launcher art either
    }

    [Fact]
    public async Task LauncherArt_StaysAuthorized_WithNoConfirmedIdentity_S49c()
    {
        _h.LauncherArt = (_, _) => (TestBitmaps.Distinct(90), false);
        _h.Igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);
        var game = await _h.Add(Games.Steam("100", "Foo"));

        await _h.Scan(game);
        await _h.Scan(game);

        Assert.Equal(HeightA, IdentityHarness.Height(game));
    }

    // ---- Legacy continuity through the commit -----------------------------------------------------------------------

    private void WriteLegacyCache(string gameId, string searchedName, int id = 777)
    {
        Directory.CreateDirectory(_h.CacheDir);
        var bytes = TestImages.Png(60, 90);
        var path = Path.Combine(_h.CacheDir, $"{gameId}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(new
        {
            Id = id, Title = "Old Match", SearchedName = searchedName, ImageSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        }));
    }

    private async Task<(GameEntry Game, GameOverride Over)> MigratedLegacyGame(string searchedName = "Foo")
    {
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Artwork = new ArtworkSelection { Provider = ArtworkProvider.SteamGridDb, ProviderGameId = "777", ProviderTitle = "Old Match", IsUserSelected = false };
        over.Identity = new GameIdentityRecord();
        over.Identity.LegacyEvidence.Add(new LegacyAssociation
        {
            Namespace = Cat.Sgdb, Id = "777", Title = "Old Match", SourceProvider = ArtworkProvider.SteamGridDb, Status = LegacyStatus.Pending,
        });
        WriteLegacyCache(game.Id, searchedName);
        _h.Sgdb.Title = _ => CatalogSearchResult.Unavailable("down"); // an outage: continuity is all there is
        _h.Igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        return (game, over);
    }

    [Fact]
    public async Task LegacyContinuity_ShowsTheValidatedCachedImage_ButKeepsItAsUnverified_S41a_S27()
    {
        var (game, over) = await MigratedLegacyGame();

        await _h.Scan(game);

        Assert.Equal(HeightA, IdentityHarness.Height(game));
        Assert.Null(over.Artwork!.DerivedFrom);                                   // still the continuity marker
        Assert.Empty(over.Identity!.Resolved);                                     // never identity, never sticky
        Assert.Equal(LegacyStatus.Pending, Assert.Single(over.Identity.LegacyEvidence).Status);
        Assert.Empty(_h.Sgdb.FetchedIds);                                          // never used for an id-based fetch
    }

    [Fact]
    public async Task LegacyContinuity_IsWithheld_WhenTheLookupUsedOtherInputs_S41d_S50()
    {
        var (game, over) = await MigratedLegacyGame(searchedName: "Some Other Name");

        await _h.Scan(game);

        Assert.Null(IdentityHarness.Height(game));                                 // withheld, with no network call
        Assert.Equal(LegacyStatus.StaleLookup, Assert.Single(over.Identity!.LegacyEvidence).Status);
        Assert.Empty(over.Identity.Rejected);                                      // a name mismatch is not a verdict
        Assert.Null(over.Artwork);                                                 // not authorized while StaleLookup: removed from display
    }

    [Fact]
    public async Task ConfirmingADifferentIdentity_EndsLegacyContinuityInTheConfirmTransaction_S46()
    {
        var (game, over) = await MigratedLegacyGame();
        await _h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        _h.Igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false); // nothing replaces it

        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Igdb, "77", "Other", null, null),
            over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Empty(over.Identity!.LegacyEvidence);
        Assert.Null(over.Artwork);                                                  // the continuity artwork is gone; nothing pinned it
        Assert.Null(IdentityHarness.Height(game));
    }

    [Fact]
    public async Task ConfirmingTheSameIdAsTheLegacyMatch_AdoptsItsArtwork_S46()
    {
        var (game, over) = await MigratedLegacyGame();
        await _h.Scan(game);
        _h.Sgdb.Cover = _ => Cover(90);

        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Sgdb, "777", "Old Match", null, null),
            over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "777"), over.Artwork!.DerivedFrom);  // the user's decision is its corroboration
        Assert.Empty(over.Identity!.LegacyEvidence);
    }

    [Fact]
    public async Task ALegacyMatchThatNoLongerMatches_IsRemovedAndItsCoverCleared_S25()
    {
        var (game, over) = await MigratedLegacyGame();
        _h.Sgdb.Title = _ => CatalogSearchResult.NoMatch();
        _h.Igdb.Title = _ => CatalogSearchResult.NoMatch();

        await _h.Scan(game);

        Assert.Empty(over.Identity!.LegacyEvidence);
        Assert.Null(over.Artwork);
        Assert.Null(IdentityHarness.Height(game));
    }

    [Fact]
    public async Task ALegacyMatchTheCurrentInputsCorroborate_BecomesAResolvedIdentity_S26()
    {
        var (game, over) = await MigratedLegacyGame();
        _h.Sgdb.Title = _ => CatalogSearchResult.Found("777", "Foo");
        _h.Igdb.Title = _ => CatalogSearchResult.NoMatch();
        _h.Sgdb.Cover = _ => Cover(90);

        await _h.Scan(game);

        Assert.Empty(over.Identity!.LegacyEvidence);
        Assert.Equal("777", Assert.Single(over.Identity.Resolved).Id);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "777"), over.Artwork!.DerivedFrom);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
    }

    // ---- Restart ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterARestart_AnActiveIdentityShowsItsCachedCoverWithNoTitleSearch_S12b()
    {
        IgdbFinds("A");
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        Assert.True(_h.Vm.SaveNowForTest());

        // A second process: the same settings directory, a brand-new view model, and a catalog that CANNOT search.
        var igdb2 = new FakeCatalog(Cat.Igdb) { Title = _ => throw new InvalidOperationException("a restart must not need a title search") };
        igdb2.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Distinct(90), FromCache: true);
        var context2 = Cat.Ctx([igdb2]);
        var vm2 = new LibraryViewModel(new SettingsService(_h.DataDir), new PendingUpdateNotesService(_h.DataDir))
        {
            IconFallbackForTest = _ => null,
            ResolutionContextForTest = () => context2,
        };
        var game2 = Games.Manual("manual-foo", "Foo");
        await vm2.ApplyScanResultAsync(new ScanResult([game2], [], [], [], new Dictionary<string, ArtworkApplyResult>()));

        var over2 = vm2.GetOverride(game2.Id)!;
        Assert.Equal("A", Assert.Single(over2.Identity!.Resolved).Id);                 // the identity persisted
        Assert.Equal(new IdentityKey(Cat.Igdb, "A"), over2.Artwork!.DerivedFrom);

        var scratch = Games.Manual("manual-foo", "Foo");
        var unit = GameScannerService.ResolveGameUnit(scratch, over2, over2.ArtworkRevision, vm2.SnapshotIdentityGenerations(), context2, CancellationToken.None);
        await vm2.ApplyScanResultAsync(new ScanResult([game2], [], [], [], new Dictionary<string, ArtworkApplyResult> { [game2.Id] = unit }));

        Assert.Equal(HeightA, IdentityHarness.Height(game2));
        Assert.Equal(0, igdb2.TitleSearches);
        Assert.Equal(new[] { "A" }, igdb2.FetchedIds);                                  // by id, from the cache
        Assert.Equal(ArtworkRetrievalMethod.LocalCache, vm2.GetOverride(game2.Id)!.Artwork!.RetrievedFrom);
    }
}
