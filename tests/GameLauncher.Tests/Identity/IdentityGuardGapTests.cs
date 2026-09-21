using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.ViewModels;
using GameLauncher.Services;
using static GameLauncher.ViewModels.LibraryViewModel;

namespace GameLauncher.Tests.Identity;

/// <summary>Guards that the mutation pass showed nothing was pinning: each test here was written because removing the guard
/// left the whole suite green. Covers are told apart by decoded height (A = 480, B = 640).</summary>
public class IdentityGuardGapTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private const int HeightA = 480, HeightB = 640;

    public void Dispose() => _h.Dispose();

    private static CatalogCoverResult Cover(int sourceHeight) => new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(sourceHeight), false);
    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");

    // ---- G3: the artwork revision --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaleAutomaticUnit_CannotReplaceTheCoverThatAResetJustProduced_G3()
    {
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        _h.Igdb.Cover = _ => Cover(90);
        var game = await _h.Add(Foo());
        await _h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));
        var staleUnit = _h.Unit(game);                       // a scan's worker, started while the artwork revision was N

        _h.Igdb.Cover = _ => Cover(120);                     // ... then the user resets, and the catalog now serves a different image
        Assert.Equal(ArtworkChangeOutcome.Success, await _h.Vm.ResetCoverToAutomaticAsync(game.Id));
        Assert.Equal(HeightB, IdentityHarness.Height(game));

        var next = IdentityHarness.Fresh(game);              // the older unit finally lands, in a scan's publication
        await _h.Publish([next], (next.Id, staleUnit));

        Assert.Equal(HeightB, IdentityHarness.Height(next)); // the newer cover stands: the stale unit's artwork half was dropped
        Assert.False(_h.Vm.UnitReportsForTest[next.Id].ArtworkPublished);
    }

    [Fact]
    public async Task AnArtworkRevisionThatMovedWithoutAnyIdentityWrite_StillDropsAnOlderUnitsArtwork_G3()
    {
        // Every real artwork change also restamps identity metadata (so the generation moves first); this moves ONLY the artwork
        // revision, so the guard is what has to hold.
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        _h.Igdb.Cover = _ => Cover(90);
        var game = await _h.Add(Foo());
        var unit = _h.Unit(game);
        _h.Vm.EnsureOverrideForTest(game.Id).ArtworkRevision += 1;

        var next = IdentityHarness.Fresh(game);
        await _h.Publish([next], (next.Id, unit));

        Assert.Equal(IdentityHalf.Committed, _h.Vm.UnitReportsForTest[next.Id].Identity);   // the identity half still applied...
        Assert.False(_h.Vm.UnitReportsForTest[next.Id].ArtworkPublished);                    // ...its artwork half did not
        Assert.Null(IdentityHarness.Height(next));
    }

    // ---- G2: the predicate is checked AT publication, not only cleaned up afterwards ---------------------------------------------

    [Fact]
    public async Task AUnitOfferingArtForAnIdentityTheLiveRecordDoesNotAuthorize_IsRefusedAtPublication_G2()
    {
        // Real units never offer such art (a race bumps the generation first), so this one is built by hand: the live record says
        // the user confirmed B and A is ineligible, yet the unit - whose identity half is a clean no-change - carries A's cover.
        // The removal pass would clean a published A up afterwards, which is exactly why this asserts the REPORT and that A's
        // record never existed even briefly.
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        over.Identity.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Igdb, "A", "Foo"));
        var offered = new UnitOutput
        {
            NewRecord = over.Identity.Clone(),
            Artwork = new ArtworkHalf(ArtworkHalfKind.Set, Cat.Auto(Cat.Igdb, "A"), TestBitmaps.Distinct(90)),
        };
        var unit = new AutomaticUnitResult(offered, IdentityQuery.From(game), over.IdentityRevision, _h.Vm.IdentityGenerationForTest(game.Id), false);

        var fresh = Games.Manual(game.Id, "Foo");
        await _h.Publish([fresh], (fresh.Id, new ArtworkApplyResult(over.ArtworkRevision, null, unit)));

        Assert.Equal(IdentityHalf.ValidatedNoChange, _h.Vm.UnitReportsForTest[fresh.Id].Identity);
        Assert.False(_h.Vm.UnitReportsForTest[fresh.Id].ArtworkPublished);
        Assert.Null(IdentityHarness.Height(fresh));
        Assert.Null(over.Artwork);
    }

    // ---- The generation advances on a user change, even one that leaves the active identity alone -----------------------------

    [Fact]
    public async Task RejectingTheCandidateAnInFlightUnitResolved_SupersedesThatUnit_AndTheRejectionSurvives()
    {
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        _h.Igdb.Cover = _ => Cover(90);
        var game = await _h.Add(Foo());
        var inFlight = _h.Unit(game);                        // resolved A; snapshotted before the user acts

        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        Assert.Equal(IdentityChangeOutcome.Success,
            _h.Vm.RejectIdentityCandidate(game.Id, new IdentityKey(Cat.Igdb, "A"), "Foo", state.DecisionRevision, state.IdentityRevision));
        // A was never ACTIVE, so the active set (and IdentityRevision) did not change - only the generation can tell the unit it lost.
        Assert.Equal(0, _h.Over(game.Id)!.IdentityRevision);

        var next = IdentityHarness.Fresh(game);
        await _h.Publish([next], (next.Id, inFlight));

        Assert.Equal(IdentityHalf.Superseded, _h.Vm.UnitReportsForTest[next.Id].Identity);
        Assert.Contains(_h.Record(game.Id)!.Rejected, r => r.Key == new IdentityKey(Cat.Igdb, "A")); // the user's rejection was not overwritten
        Assert.Empty(_h.Record(game.Id)!.Resolved);
        Assert.Null(IdentityHarness.Height(next));
    }

    // ---- Confirm and Rejected never overlap -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmingACandidateTheUserHadRejected_RemovesTheRejection_SoConfirmedIsNeverInRejected()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Cover = _ => Cover(120);
        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        Assert.Equal(IdentityChangeOutcome.Success,
            _h.Vm.RejectIdentityCandidate(game.Id, new IdentityKey(Cat.Sgdb, "B"), "Bar", state.DecisionRevision, state.IdentityRevision));
        Assert.Single(_h.Record(game.Id)!.Rejected);

        state = _h.Vm.GetIdentityDialogState(game.Id)!;
        var outcome = await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Sgdb, "B", "Bar", null, null),
            state.DecisionRevision, state.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        var record = _h.Record(game.Id)!;
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), record.Confirmed!.Key);
        Assert.Empty(record.Rejected);                                           // the invariant: Confirmed is never in Rejected
        Assert.Equal(HeightB, IdentityHarness.Height(game));                     // and its art is not refused by its own old rejection
    }

    // ---- An ambiguous store-id mapping is not guessed around ---------------------------------------------------------------------

    [Fact]
    public void AStoreIdThatMapsToSeveralGames_IsAmbiguous_AndTheTitlePathDoesNotGuessOneOfThem()
    {
        var igdb = new FakeCatalog(Cat.Igdb, "IGDB")
        {
            Map = _ => CatalogSearchResult.Ambiguous("the store id maps to more than one IGDB game"),
            Title = _ => CatalogSearchResult.Found("7", "Cyberpunk 2077"), // a unique exact title exists - it must NOT be used to pick one
        };

        var output = AutomaticResolver.Run(Cat.Input(Games.Steam(), null, null, false, "Scan", false), Cat.Ctx([igdb]), CancellationToken.None);

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Ambiguous, output.NewRecord.LastAttempt!.Outcome);
        Assert.Equal(0, igdb.TitleSearches);
    }

    // ---- Defence in depth: each guard stands on its own, not only because another one usually fires first -------------------------

    [Fact]
    public async Task ARevisionMismatch_RejectsAUnitEvenWhenTheGenerationStillMatches()
    {
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        _h.Igdb.Cover = _ => Cover(90);
        var game = await _h.Add(Foo());
        var unit = _h.Unit(game);
        _h.Vm.EnsureOverrideForTest(game.Id).IdentityRevision = 5; // a revision moved by a path that did not touch the generation

        var next = IdentityHarness.Fresh(game);
        await _h.Publish([next], (next.Id, unit));

        Assert.Equal(IdentityHalf.RejectedRevision, _h.Vm.UnitReportsForTest[next.Id].Identity);
        Assert.Null(IdentityHarness.Height(next));
    }

    private static ActiveIdentity FabricatedActive(params ActiveEntry[] entries) => new()
    {
        Entries = entries, Primary = entries.FirstOrDefault(), Key = "k", AuthKey = "ak",
    };

    [Fact]
    public void ThePredicate_RefusesARejectedIdentity_EvenIfHandedAnActiveSetThatStillListsItAsEligible()
    {
        var query = IdentityQuery.From(Foo());
        var record = new GameIdentityRecord();
        record.Rejected.Add(new ProviderIdentity { Namespace = Cat.Igdb, Id = "A", Title = "Foo", At = DateTime.UtcNow });
        var active = FabricatedActive(new ActiveEntry(Cat.Igdb, "A", "Foo", EntryRole.Identity, EntryAuthority.Automatic, true, null));

        Assert.False(ArtworkAuthorization.IsAuthorized(Cat.Auto(Cat.Igdb, "A"), active, query, record));
    }

    [Fact]
    public void ThePredicate_RefusesACompetingIdInTheConfirmedNamespace_EvenIfHandedAnActiveSetThatListsItAsEligible()
    {
        var query = IdentityQuery.From(Foo());
        var record = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "B", "Foo") };
        var active = FabricatedActive(
            new ActiveEntry(Cat.Igdb, "B", "Foo", EntryRole.Identity, EntryAuthority.User, true, null),
            new ActiveEntry(Cat.Igdb, "A", "Foo", EntryRole.Identity, EntryAuthority.Automatic, true, null));

        Assert.False(ArtworkAuthorization.IsAuthorized(Cat.Auto(Cat.Igdb, "A"), active, query, record));
        Assert.True(ArtworkAuthorization.IsAuthorized(Cat.Auto(Cat.Igdb, "B"), active, query, record));
    }
}
