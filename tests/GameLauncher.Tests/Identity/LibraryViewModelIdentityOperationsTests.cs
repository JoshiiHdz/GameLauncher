using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>The USER's identity operations (Confirm / Reject / Clear), Reset, the two-revision dialog guard, save-failure
/// rollback, quarantine, and dedup merges - the operation matrix of design 6.3 and 7, through the production view model.
/// Covers S4, S5, S6, S8, S8b, S9-S11, S16, S28, S30, S36-S40, S44 and S49.
///
/// Covers are told apart by their decoded height: A = 480, B = 640, C = 800.</summary>
public class LibraryViewModelIdentityOperationsTests : IDisposable
{
    private readonly List<IdentityHarness> _harnesses = new();
    private const int HeightA = 480, HeightB = 640, HeightC = 800;

    public void Dispose()
    {
        foreach (var h in _harnesses)
            h.Dispose();
    }

    private IdentityHarness NewHarness(bool broken = false)
    {
        var h = new IdentityHarness(brokenSettings: broken);
        _harnesses.Add(h);
        return h;
    }

    private static CatalogCoverResult Cover(int sourceHeight) => new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(sourceHeight), false);
    private static CatalogCoverResult NoArt() => new(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);
    private static CatalogCandidate Cand(IdentifierNamespace ns, string id, string title = "Foo") => new(ns, id, title, null, null);
    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");

    private static LibraryViewModel.IdentityDialogState State(IdentityHarness h, GameEntry g) => h.Vm.GetIdentityDialogState(g.Id)!;

    private static Task<IdentityChangeOutcome> Confirm(IdentityHarness h, GameEntry g, CatalogCandidate c, LibraryViewModel.IdentityDialogState? s = null)
    {
        s ??= State(h, g);
        return h.Vm.ConfirmIdentityAsync(g.Id, c, s.DecisionRevision, s.IdentityRevision);
    }

    private static IdentityChangeOutcome Reject(IdentityHarness h, GameEntry g, IdentityKey key, LibraryViewModel.IdentityDialogState? s = null)
    {
        s ??= State(h, g);
        return h.Vm.RejectIdentityCandidate(g.Id, key, "x", s.DecisionRevision, s.IdentityRevision);
    }

    private static Task<IdentityChangeOutcome> Clear(IdentityHarness h, GameEntry g, LibraryViewModel.IdentityDialogState? s = null)
    {
        s ??= State(h, g);
        return h.Vm.ClearIdentityAsync(g.Id, s.DecisionRevision, s.IdentityRevision);
    }

    private static string Canon(GameIdentityRecord? r) => JsonSerializer.Serialize(r ?? new GameIdentityRecord(), GameLauncher.Serialization.IdentityJson.Inner);

    private async Task<(IdentityHarness H, GameEntry Game)> Started()
    {
        var h = NewHarness();
        return (h, await h.Add(Foo()));
    }

    // ---- Confirm ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_RecordsTheDecision_AndAdvancesBothRevisions_AndTheGeneration()
    {
        var (h, game) = await Started();
        h.Sgdb.Cover = _ => Cover(90);
        var generation = h.Vm.IdentityGenerationForTest(game.Id);

        var outcome = await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"));

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        var over = h.Over(game.Id)!;
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), over.Identity!.Confirmed!.Key);
        Assert.Null(over.Identity.Confirmed.VerifiedLauncherIds); // "not proven" - never assumed
        Assert.Equal((1, 1), (over.DecisionRevision, over.IdentityRevision));
        Assert.True(h.Vm.IdentityGenerationForTest(game.Id) > generation);
    }

    [Fact]
    public async Task Confirm_WhileACustomCoverIsPinned_NeverTouchesThePinnedAsset_S4()
    {
        var (h, game) = await Started();
        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { Assert.Equal(ArtworkChangeOutcome.Success, await h.Vm.ApplyLocalCoverImageAsync(game.Id, png)); }
        finally { File.Delete(png); }
        var pinned = h.Over(game.Id)!.Artwork!;
        var (asset, extension, revision) = (pinned.AssetId, pinned.AssetExtension, h.Over(game.Id)!.ArtworkRevision);
        var bytesBefore = ArtworkAssetStore.TryRead(asset, extension, h.AssetDir);
        h.Sgdb.Cover = _ => Cover(90);

        var outcome = await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"));

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        var after = h.Over(game.Id)!;
        Assert.Equal((asset, extension, revision), (after.Artwork!.AssetId, after.Artwork.AssetExtension, after.ArtworkRevision));
        Assert.True(after.Artwork.IsUserSelected);
        Assert.Equal(bytesBefore, ArtworkAssetStore.TryRead(asset, extension, h.AssetDir));
        Assert.Equal(HeightC, IdentityHarness.Height(game));         // pixels unchanged
        Assert.Equal(0, h.Sgdb.CoverFetches + h.Igdb.CoverFetches);  // and no cover was even fetched over it
    }

    [Fact]
    public async Task Confirm_RejectsALauncherNamespace_OrABlankId()
    {
        var (h, game) = await Started();

        Assert.Equal(IdentityChangeOutcome.InvalidOperation, await Confirm(h, game, Cand(IdentifierNamespace.SteamApp, "100")));
        Assert.Equal(IdentityChangeOutcome.InvalidOperation, await Confirm(h, game, Cand(Cat.Igdb, " ")));
        Assert.Null(h.Over(game.Id)?.Identity?.Confirmed);
    }

    [Fact]
    public async Task Confirm_OfAGameThatNoLongerExists_ReportsIt()
    {
        var (h, _) = await Started();

        var outcome = await h.Vm.ConfirmIdentityAsync("no-such-game", Cand(Cat.Igdb, "1"), 0, 0);

        Assert.Equal(IdentityChangeOutcome.GameNoLongerExists, outcome);
    }

    [Fact]
    public async Task Confirm_ReplacingAnEarlierConfirmation_DropsTheOldArtworkSources_S7()
    {
        var (h, game) = await Started();
        h.Igdb.Cover = _ => NoArt();
        h.Sgdb.Title = t => t == "Foo Canonical" ? CatalogSearchResult.Found("5", "Foo Canonical") : CatalogSearchResult.NoMatch();
        h.Sgdb.Cover = _ => Cover(90);
        await Confirm(h, game, Cand(Cat.Igdb, "7", "Foo Canonical"));
        Assert.Single(h.Record(game.Id)!.ArtworkSources); // SteamGridDB supplied art by the confirmed title

        h.Igdb.Cover = _ => Cover(120);
        var outcome = await Confirm(h, game, Cand(Cat.Igdb, "8", "Another Game"));

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.DoesNotContain(h.Record(game.Id)!.ArtworkSources, s => s.Basis!.Id == "7");  // their basis is gone
        Assert.Equal("8", h.Record(game.Id)!.Confirmed!.Id);
    }

    // ---- The no-op rule (S39) and the revision rules (S40) -------------------------------------------------------------

    [Fact]
    public async Task NoOpUserOperations_ReturnNoChange_AndAdvanceNothing_S39()
    {
        var (h, game) = await Started();
        h.Sgdb.Cover = _ => Cover(90);
        Assert.Equal(IdentityChangeOutcome.NoChange, await Clear(h, game)); // clearing when nothing is set
        await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"));
        Assert.Equal(IdentityChangeOutcome.Success, Reject(h, game, new IdentityKey(Cat.Igdb, "X")));
        var over = h.Over(game.Id)!;
        var revisions = (over.DecisionRevision, over.IdentityRevision);
        var generation = h.Vm.IdentityGenerationForTest(game.Id);
        revisions = (over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.NoChange, await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"))); // re-confirm the same
        Assert.Equal(IdentityChangeOutcome.NoChange, Reject(h, game, new IdentityKey(Cat.Igdb, "X")));    // reject an already-rejected one

        Assert.Equal(revisions, (over.DecisionRevision, over.IdentityRevision));
        Assert.Equal(generation, h.Vm.IdentityGenerationForTest(game.Id));
    }

    [Fact]
    public async Task DecisionRevisionAdvancesOnEveryMeaningfulChange_IdentityRevisionOnlyWhenTheActiveSetChanges_S40()
    {
        var (h, game) = await Started();
        h.Sgdb.Cover = _ => Cover(90);
        var over = h.Vm.EnsureOverrideForTest(game.Id);

        // A candidate that is NOT active: the user's decisions changed, the active identity did not.
        Assert.Equal(IdentityChangeOutcome.Success, Reject(h, game, new IdentityKey(Cat.Igdb, "X")));
        Assert.Equal((1, 0), (over.DecisionRevision, over.IdentityRevision));

        // A confirmation changes both.
        Assert.Equal(IdentityChangeOutcome.Success, await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar")));
        Assert.Equal((2, 1), (over.DecisionRevision, over.IdentityRevision));

        // Clearing removes the confirmation (the active set changes) and the rejection.
        Assert.Equal(IdentityChangeOutcome.Success, await Clear(h, game));
        Assert.Equal((3, 2), (over.DecisionRevision, over.IdentityRevision));
    }

    [Fact]
    public async Task AnExhaustedDecisionRevision_RejectsTheUserOperation_BeforeAnythingChanges_S40()
    {
        var (h, game) = await Started();
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.DecisionRevision = long.MaxValue;
        var before = Canon(over.Identity);

        var outcome = await h.Vm.ConfirmIdentityAsync(game.Id, Cand(Cat.Igdb, "1"), long.MaxValue, 0);

        Assert.Equal(IdentityChangeOutcome.RevisionExhausted, outcome);
        Assert.Equal(before, Canon(over.Identity));
        Assert.Equal(long.MaxValue, over.DecisionRevision);
    }

    [Fact]
    public async Task AnExhaustedIdentityRevision_RejectsAKeyChangingOperation_BeforeAnythingChanges_S19()
    {
        var (h, game) = await Started();
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.IdentityRevision = long.MaxValue;

        var outcome = await h.Vm.ConfirmIdentityAsync(game.Id, Cand(Cat.Igdb, "1"), 0, long.MaxValue);

        Assert.Equal(IdentityChangeOutcome.RevisionExhausted, outcome);
        Assert.Null(over.Identity?.Confirmed);
        Assert.Equal(0, over.DecisionRevision);
    }

    // ---- The two-revision dialog guard (S8b, S37) ------------------------------------------------------------------------

    [Fact]
    public async Task AClearDialog_IsStale_WhenAnotherOperationRejectedANonActiveCandidate_AndThatRejectionSurvives_S37()
    {
        var (h, game) = await Started();
        h.Sgdb.Cover = _ => Cover(90);
        await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"));
        var dialogOpenedWith = State(h, game);

        Assert.Equal(IdentityChangeOutcome.Success, Reject(h, game, new IdentityKey(Cat.Igdb, "NOT-ACTIVE")));
        var after = State(h, game);
        Assert.Equal(dialogOpenedWith.IdentityRevision, after.IdentityRevision);      // the active set did not change...
        Assert.NotEqual(dialogOpenedWith.DecisionRevision, after.DecisionRevision);   // ...but the user's decisions did

        var outcome = await Clear(h, game, dialogOpenedWith);

        Assert.Equal(IdentityChangeOutcome.StaleSelection, outcome);
        Assert.Contains(h.Record(game.Id)!.Rejected, r => r.Id == "NOT-ACTIVE");      // the newer rejection was NOT silently removed
        Assert.NotNull(h.Record(game.Id)!.Confirmed);
    }

    [Fact]
    public async Task AnIdentityDialog_IsStale_WhenAnAutomaticUnitChangedTheActiveIdentityMeanwhile_S8b()
    {
        var (h, game) = await Started();
        var dialogOpenedWith = State(h, game);
        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);
        await h.Scan(game); // "this game was just identified as A" while the dialog was open

        var outcome = await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"), dialogOpenedWith);

        Assert.Equal(IdentityChangeOutcome.StaleSelection, outcome);
        Assert.Null(h.Record(game.Id)!.Confirmed);
        Assert.Equal("A", State(h, game).Active.Primary!.Id); // what the UI tells the user the game was just identified as
    }

    [Fact]
    public async Task HarmlessAutomaticWrites_DoNotInvalidateAnOpenDialog_S38()
    {
        var (h, game) = await Started();
        h.Igdb.Cover = _ => NoArt();
        h.Sgdb.Title = t => t == "Foo Canonical" ? CatalogSearchResult.Found("5", "Foo Canonical") : CatalogSearchResult.NoMatch();
        h.Sgdb.Cover = _ => Cover(90);
        await Confirm(h, game, Cand(Cat.Igdb, "7", "Foo Canonical"));
        var dialogOpenedWith = State(h, game);

        // A rescan restamps LastAttempt and (re)writes an ArtworkSource: neither is a decision nor a change of identity.
        var later = Cat.Ctx([h.Igdb, h.Sgdb], now: () => new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = IdentityHarness.Fresh(game);
        await h.Publish([fresh], (fresh.Id, h.Unit(fresh, later)));

        var after = State(h, game);
        Assert.Equal((dialogOpenedWith.DecisionRevision, dialogOpenedWith.IdentityRevision), (after.DecisionRevision, after.IdentityRevision));
        Assert.Equal(IdentityChangeOutcome.Success, Reject(h, fresh, new IdentityKey(Cat.Igdb, "X"), dialogOpenedWith)); // the old dialog still applies
    }

    // ---- Reject -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RejectingAResolvedCandidate_RemovesItInTheSameTransaction_AndItIsNeverPickedAgain_S28_S16()
    {
        var (h, game) = await Started();
        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);
        await h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));

        var outcome = Reject(h, game, new IdentityKey(Cat.Igdb, "A"));

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        var record = h.Record(game.Id)!;
        Assert.Empty(record.Resolved);                                             // removed in the same transaction
        Assert.Contains(record.Rejected, r => r.Key == new IdentityKey(Cat.Igdb, "A"));
        Assert.True(IdentitySelection.SelectActive(record, State(h, game).Query).IsEmpty);
        Assert.Null(h.Over(game.Id)!.Artwork);                                     // its cover went with it
        Assert.Null(IdentityHarness.Height(game));

        var fresh = IdentityHarness.Fresh(game);
        await h.Publish([fresh], (fresh.Id, h.Unit(fresh)));                       // even though the provider still says A
        Assert.Empty(h.Record(game.Id)!.Resolved);
        Assert.Null(IdentityHarness.Height(fresh));
    }

    [Fact]
    public async Task ARejection_SurvivesARestart_S16()
    {
        var (h, game) = await Started();
        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);
        await h.Scan(game);
        Reject(h, game, new IdentityKey(Cat.Igdb, "A"));

        var igdb2 = new FakeCatalog(Cat.Igdb) { Title = _ => CatalogSearchResult.Found("A", "Foo") };
        var vm2 = new LibraryViewModel(new SettingsService(h.DataDir), new PendingUpdateNotesService(h.DataDir))
        {
            IconFallbackForTest = _ => null,
            ResolutionContextForTest = () => Cat.Ctx([igdb2]),
        };
        var game2 = Foo();
        await vm2.ApplyScanResultAsync(new ScanResult([game2], [], [], [], new Dictionary<string, ArtworkApplyResult>()));
        var unit = GameScannerService.ResolveGameUnit(Foo(), vm2.GetOverride(game2.Id), 0, vm2.SnapshotIdentityGenerations(), Cat.Ctx([igdb2]), CancellationToken.None);
        await vm2.ApplyScanResultAsync(new ScanResult([game2], [], [], [], new Dictionary<string, ArtworkApplyResult> { [game2.Id] = unit }));

        Assert.Empty(vm2.GetOverride(game2.Id)!.Identity!.Resolved);
        Assert.Contains(vm2.GetOverride(game2.Id)!.Identity!.Rejected, r => r.Id == "A");
        Assert.Equal(0, igdb2.CoverFetches);
    }

    [Fact]
    public async Task RejectingTheConfirmedIdentity_IsNotAReject_ItIsClearOrChange_S28()
    {
        var (h, game) = await Started();
        h.Sgdb.Cover = _ => Cover(90);
        await Confirm(h, game, Cand(Cat.Sgdb, "B", "Bar"));

        Assert.Equal(IdentityChangeOutcome.InvalidOperation, Reject(h, game, new IdentityKey(Cat.Sgdb, "B")));
        Assert.NotNull(h.Record(game.Id)!.Confirmed);
    }

    [Fact]
    public async Task RejectingAnArtworkSource_RemovesItsCover_AndItIsNeverAcceptedOrShownAgain_S44()
    {
        var (h, game) = await Started();
        h.Igdb.Cover = _ => NoArt();
        h.Sgdb.Title = t => t == "Foo Canonical" ? CatalogSearchResult.Found("C", "Foo Canonical") : CatalogSearchResult.NoMatch();
        h.Sgdb.Cover = _ => Cover(150);
        await Confirm(h, game, Cand(Cat.Igdb, "7", "Foo Canonical"));
        Assert.Equal(HeightC, IdentityHarness.Height(game));                        // C's cover is showing (an artwork source)
        Assert.Equal(new IdentityKey(Cat.Sgdb, "C"), h.Over(game.Id)!.Artwork!.DerivedFrom);

        var outcome = Reject(h, game, new IdentityKey(Cat.Sgdb, "C"));

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        var record = h.Record(game.Id)!;
        Assert.Contains(record.Rejected, r => r.Key == new IdentityKey(Cat.Sgdb, "C"));
        Assert.Empty(record.ArtworkSources);                                        // removed in the same transaction
        Assert.Null(h.Over(game.Id)!.Artwork);                                      // (a) C's cover is gone...
        Assert.Null(IdentityHarness.Height(game));

        var coverFetches = h.Sgdb.CoverFetches;
        var fresh = IdentityHarness.Fresh(game);
        await h.Publish([fresh], (fresh.Id, h.Unit(fresh)));                        // (e) the fallback still finds C by title...
        Assert.Empty(h.Record(game.Id)!.ArtworkSources);                            // ...but refuses it
        Assert.Equal(coverFetches, h.Sgdb.CoverFetches);                            // never even fetched again
        Assert.Null(IdentityHarness.Height(fresh));
    }

    // ---- Clear ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Clear_RemovesTheConfirmationRejectionsAndArtworkSources_KeepsFreshAutomaticIdentities_AndPinnedCovers_S6()
    {
        var (h, game) = await Started();
        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { await h.Vm.ApplyLocalCoverImageAsync(game.Id, png); }
        finally { File.Delete(png); }
        var pinnedAsset = h.Over(game.Id)!.Artwork!.AssetId;
        var query = State(h, game).Query;
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        over.Identity.Resolved.Add(Cat.Resolved(query, Cat.Igdb, "A", "Foo"));       // a fresh automatic identity...
        over.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "X", "x"));
        over.Identity.ArtworkSources.Add(new ArtworkSourceAssociation { Namespace = Cat.Igdb, Id = "S", Title = "Bar", ResolverVersion = 1, Basis = over.Identity.Confirmed!.Key });
        var decisionBefore = over.DecisionRevision;

        var outcome = await Clear(h, game);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Null(over.Identity!.Confirmed);
        Assert.Empty(over.Identity.Rejected);
        Assert.Empty(over.Identity.ArtworkSources);
        Assert.Equal("A", Assert.Single(over.Identity.Resolved).Id);                 // ...survives: it never depended on the user's decisions
        Assert.Equal(pinnedAsset, over.Artwork!.AssetId);                             // the pinned cover is untouched
        Assert.Equal(decisionBefore + 1, over.DecisionRevision);
    }

    // ---- Reset ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_WithAConfirmedIdentity_LeavesIdentityAlone_AndFetchesByIdWithZeroTitleSearches_S5()
    {
        var (h, game) = await Started();
        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { await h.Vm.ApplyLocalCoverImageAsync(game.Id, png); }
        finally { File.Delete(png); }
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo Confirmed") };
        var identityBefore = Canon(over.Identity);
        h.Igdb.Cover = _ => Cover(90);
        var decision = over.DecisionRevision;
        var identityRevision = over.IdentityRevision;

        var outcome = await h.Vm.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.Equal(0, h.Igdb.TitleSearches + h.Sgdb.TitleSearches);               // zero title-search calls to any provider
        Assert.Equal(new[] { "7" }, h.Igdb.FetchedIds);
        Assert.Equal(HeightA, IdentityHarness.Height(game));                        // the confirmed game's cover, by id
        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), over.Artwork!.DerivedFrom);
        Assert.False(over.Artwork.IsUserSelected);
        Assert.Equal((decision, identityRevision), (over.DecisionRevision, over.IdentityRevision)); // Reset never touches identity (I2)
        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), over.Identity!.Confirmed!.Key);                 // the decision is exactly as it was
        Assert.Empty(over.Identity.Rejected);
        Assert.Empty(over.Identity.Resolved);                                                        // and nothing was re-derived beside it
        Assert.NotNull(identityBefore);
    }

    [Fact]
    public async Task Reset_WithNoIdentity_IsAnOrdinaryFullResolution_ThatBypassesTheCooldown_S30()
    {
        var (h, game) = await Started();
        h.Igdb.Title = _ => CatalogSearchResult.NoMatch();
        await h.Scan(game);                         // records NoMatch
        var searchesAfterScan = h.Igdb.TitleSearches;
        var again = IdentityHarness.Fresh(game);
        await h.Scan(again);                        // within the cooldown: nothing is asked again
        Assert.Equal(searchesAfterScan, h.Igdb.TitleSearches);

        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);
        var outcome = await h.Vm.ResetCoverToAutomaticAsync(again.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.Equal(searchesAfterScan + 1, h.Igdb.TitleSearches);                  // Reset bypassed the cooldown
        var record = h.Record(game.Id)!;
        Assert.Equal("A", Assert.Single(record.Resolved).Id);                        // its own, separately validated identity write
        Assert.StartsWith("Reset", record.LastAttempt!.Trigger);
        Assert.Equal(HeightA, IdentityHarness.Height(again));
    }

    private static GameIdentityRecord Quarantined(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };
    }

    [Fact]
    public async Task Reset_OnAQuarantinedGame_ChangesOnlyArtwork_PublishesNoCatalogCover_AndKeepsTheRawRecordVerbatim_S36()
    {
        var (h, game) = await Started();
        const string raw = """{ "Confirmed": { "Namespace": 5 }, "Extra": [1, 2] }""";
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = Quarantined(raw);
        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);

        var outcome = await h.Vm.ResetCoverToAutomaticAsync(game.Id);

        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.Equal(0, h.Igdb.TitleSearches + h.Igdb.CoverFetches + h.Sgdb.TitleSearches + h.Sgdb.CoverFetches); // no catalog was asked
        Assert.Null(IdentityHarness.Height(game));
        Assert.True(h.Vm.SaveNowForTest());
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(h.DataDir, "settings.json")));
        using var original = JsonDocument.Parse(raw);
        var written = saved.RootElement.GetProperty("Overrides").GetProperty(game.Id).GetProperty("Identity");
        Assert.Equal(JsonSerializer.Serialize(original.RootElement), JsonSerializer.Serialize(written)); // byte-identical after Load->Save
    }

    [Fact]
    public async Task ClearIdentity_OnAQuarantinedGame_ArchivesTheRawSubtreeVerbatim_AndStartsAFreshRecord_S36()
    {
        var (h, game) = await Started();
        const string raw = """{ "Confirmed": { "Namespace": 5 }, "Extra": [1, 2] }""";
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = Quarantined(raw);
        var before = State(h, game);
        Assert.Equal((IdentityState.Unresolved, "Quarantined"), (before.State, before.UnresolvedReason));

        Assert.Equal(IdentityChangeOutcome.InvalidOperation, await Confirm(h, game, Cand(Cat.Igdb, "1"), before)); // nothing else replaces it
        Assert.Equal(IdentityChangeOutcome.InvalidOperation, Reject(h, game, new IdentityKey(Cat.Igdb, "1"), before));

        var outcome = await Clear(h, game, before);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.False(over.Identity!.IsQuarantined);
        var archived = Assert.Single(h.Vm.QuarantineArchiveForTest);
        using var original = JsonDocument.Parse(raw);
        Assert.Equal(JsonSerializer.Serialize(original.RootElement), JsonSerializer.Serialize(archived));
        Assert.Equal(before.DecisionRevision + 1, over.DecisionRevision);
    }

    // ---- Save failures (S11) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AUserOperationThatCannotBeSaved_RestoresEverythingExactly_AndChangesNothingVisible_S11()
    {
        var h = NewHarness(broken: true);
        var game = await h.Add(Foo());
        var over = h.Vm.EnsureOverrideForTest(game.Id);
        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "1", "One") };
        over.Artwork = Cat.Auto(Cat.Sgdb, "9", ArtworkProvider.SteamGridDb);
        over.DecisionRevision = 4;
        over.IdentityRevision = 6;
        var before = Canon(over.Identity);
        game.Icon = TestBitmaps.Distinct(90);
        game.IsCoverArt = true;

        var outcome = await h.Vm.ConfirmIdentityAsync(game.Id, Cand(Cat.Igdb, "2", "Two"), 4, 6);

        Assert.Equal(IdentityChangeOutcome.SaveFailed, outcome);
        Assert.Equal(before, Canon(over.Identity));
        Assert.Equal((4, 6), (over.DecisionRevision, over.IdentityRevision));
        Assert.Equal(new IdentityKey(Cat.Sgdb, "9"), over.Artwork!.DerivedFrom);      // the artwork record is back too
        Assert.Equal(HeightA, IdentityHarness.Height(game));                           // and the display was never touched
        Assert.Equal(0, h.Igdb.CoverFetches);                                          // no follow-up on a failed operation
    }

    [Fact]
    public async Task AnAutomaticCommit_ThatCannotBeSaved_IsStillShown_AndMemoryIsAhead_S11()
    {
        var h = NewHarness(broken: true);
        var game = await h.Add(Foo());
        h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        h.Igdb.Cover = _ => Cover(90);

        await h.Scan(game);

        Assert.Equal(HeightA, IdentityHarness.Height(game));               // automatic results are re-derivable: shown first
        Assert.Equal("A", Assert.Single(h.Record(game.Id)!.Resolved).Id);
        Assert.False(h.Vm.SaveNowForTest());                                // (and they persist with the next SUCCESSFUL save)
    }

    // ---- Dedup merges (design 8.2) ---------------------------------------------------------------------------------------

    private (IdentityHarness H, GameOverride Winner, GameOverride Loser) Merge(Action<GameOverride> winner, Action<GameOverride> loser, bool winnerExists = true)
    {
        var h = NewHarness();
        var l = h.Vm.EnsureOverrideForTest("loser");
        loser(l);
        GameOverride w;
        if (winnerExists)
        {
            w = h.Vm.EnsureOverrideForTest("winner");
            winner(w);
        }
        else
        {
            w = null!;
        }

        h.Vm.MigrateMergedOverrides(new Dictionary<string, string> { ["loser"] = "winner" });
        return (h, h.Vm.GetOverride("winner")!, l);
    }

    private static GameIdentityRecord Confirmed(IdentifierNamespace ns, string id, string title = "T") =>
        new() { Confirmed = Cat.Confirmed(ns, id, title) };

    [Fact]
    public void Merge_ALosersConfirmation_BeatsAnAutomaticWinner_S10()
    {
        var (h, winner, _) = Merge(w => w.Identity = new GameIdentityRecord(), l => l.Identity = Confirmed(Cat.Igdb, "9"));

        Assert.Equal(new IdentityKey(Cat.Igdb, "9"), winner.Identity!.Confirmed!.Key);
        Assert.Empty(h.Vm.IdentityConflictsForTest);
        Assert.True(winner.DecisionRevision > 0);       // a merge that adopts a decision makes an open dialog stale
        Assert.Null(h.Vm.GetOverride("loser"));
    }

    [Fact]
    public void Merge_TwoDifferentConfirmationsInOneNamespace_KeepTheWinner_AndRecordTheLoser_S9()
    {
        var (h, winner, _) = Merge(w => w.Identity = Confirmed(Cat.Igdb, "1"), l => l.Identity = Confirmed(Cat.Igdb, "2"));

        Assert.Equal("1", winner.Identity!.Confirmed!.Id);
        var conflict = Assert.Single(h.Vm.IdentityConflictsForTest);
        Assert.Equal(("SameNamespaceDifferentId", "2", "winner", "loser"), (conflict.Kind, conflict.LoserConfirmed!.Id, conflict.WinnerGameId, conflict.LoserGameId));
    }

    [Fact]
    public void Merge_ConfirmationsInDifferentNamespaces_AreNeverMerged_NeverDropped_S9b()
    {
        var (h, winner, _) = Merge(w => w.Identity = Confirmed(Cat.Igdb, "A"), l => l.Identity = Confirmed(Cat.Sgdb, "B"));

        Assert.Equal(new IdentityKey(Cat.Igdb, "A"), winner.Identity!.Confirmed!.Key);   // never merged into Confirmed
        var conflict = Assert.Single(h.Vm.IdentityConflictsForTest);
        Assert.Equal("CrossNamespaceUnproven", conflict.Kind);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), conflict.LoserConfirmed!.Key);       // never dropped
    }

    [Fact]
    public void Merge_TheSameConfirmationOnBothSides_IsNotAConflict()
    {
        var (h, winner, _) = Merge(w => w.Identity = Confirmed(Cat.Igdb, "1"), l => l.Identity = Confirmed(Cat.Igdb, "1"));

        Assert.Equal("1", winner.Identity!.Confirmed!.Id);
        Assert.Empty(h.Vm.IdentityConflictsForTest);
    }

    [Fact]
    public void Merge_ALosersConfirmationTheWinnerRejected_IsAConflict_AndConfirmedNeverIntersectsRejected_S9d()
    {
        var (h, winner, _) = Merge(
            w => { w.Identity = new GameIdentityRecord(); w.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "X", "x")); },
            l => l.Identity = Confirmed(Cat.Igdb, "X"));

        Assert.Null(winner.Identity!.Confirmed);                                         // the winner's rejection stands
        Assert.Equal("ConfirmedVsRejected", Assert.Single(h.Vm.IdentityConflictsForTest).Kind);
    }

    [Fact]
    public void Merge_ALosersRejectionOfTheWinnersConfirmation_IsKeptInTheConflict_NotInTheLiveRejectedList_S9d()
    {
        var (h, winner, _) = Merge(
            w => w.Identity = Confirmed(Cat.Igdb, "X"),
            l => { l.Identity = new GameIdentityRecord(); l.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "X", "x")); });

        Assert.Equal("X", winner.Identity!.Confirmed!.Id);
        Assert.DoesNotContain(winner.Identity.Rejected, r => r.Id == "X");                // Confirmed is never in Rejected
        var conflict = Assert.Single(h.Vm.IdentityConflictsForTest);
        Assert.Equal("ConfirmedVsRejected", conflict.Kind);
        Assert.Contains(conflict.LoserRejected, r => r.Id == "X");
    }

    [Fact]
    public void Merge_RejectionsAreUnioned()
    {
        var (_, winner, _) = Merge(
            w => { w.Identity = new GameIdentityRecord(); w.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "1", "a")); },
            l => { l.Identity = new GameIdentityRecord(); l.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "2", "b")); });

        Assert.Equal(new[] { "1", "2" }, winner.Identity!.Rejected.Select(r => r.Id).OrderBy(x => x));
    }

    [Fact]
    public void Merge_WithAnExhaustedCounter_SkipsTheMigration_ButStillRecordsTheLosersDecisions_S9c()
    {
        var (h, winner, _) = Merge(
            w => { w.Identity = Confirmed(Cat.Igdb, "1"); w.IdentityRevision = long.MaxValue; },
            l => { l.Identity = Confirmed(Cat.Sgdb, "B"); l.Identity.Rejected.Add(Cat.Confirmed(Cat.Igdb, "Z", "z")); });

        Assert.Equal("1", winner.Identity!.Confirmed!.Id);
        Assert.Equal(long.MaxValue, winner.IdentityRevision);                               // untouched
        var conflict = Assert.Single(h.Vm.IdentityConflictsForTest);
        Assert.Equal("MergeRevisionExhausted", conflict.Kind);
        Assert.Equal("B", conflict.LoserConfirmed!.Id);
        Assert.Contains(conflict.LoserRejected, r => r.Id == "Z");
    }

    [Fact]
    public void Merge_ALosersQuarantinedRecord_IsArchivedVerbatim_NeverInterpreted()
    {
        var (h, winner, _) = Merge(w => w.Identity = Confirmed(Cat.Igdb, "1"), l => l.Identity = Quarantined("""{ "Junk": [1] }"""));

        Assert.Equal("1", winner.Identity!.Confirmed!.Id);
        Assert.Equal("""{"Junk":[1]}""", JsonSerializer.Serialize(Assert.Single(h.Vm.QuarantineArchiveForTest)));
    }

    [Fact]
    public void Merge_WhenTheWinnerHadNoOverride_AdoptsTheLosersIdentity_WithRevisionsBumpedPastTheirOwn()
    {
        var h = NewHarness();
        var loser = h.Vm.EnsureOverrideForTest("loser");
        loser.Identity = Confirmed(Cat.Igdb, "9");
        loser.IdentityRevision = 5;
        loser.DecisionRevision = 3;

        h.Vm.MigrateMergedOverrides(new Dictionary<string, string> { ["loser"] = "winner" });

        var winner = h.Vm.GetOverride("winner")!;
        Assert.Equal("9", winner.Identity!.Confirmed!.Id);
        Assert.Equal((6, 4), (winner.IdentityRevision, winner.DecisionRevision)); // a unit computed pre-merge can never validate by coincidence
        Assert.True(h.Vm.IdentityGenerationForTest("winner") > 0);
    }

    [Fact]
    public async Task ADialogHoldingAMergedAwayId_IsRetargetedToTheWinner_ButIsStaleAfterTheMerge_S8()
    {
        var h = NewHarness();
        var winnerGame = Games.Manual("winner", "Foo");
        await h.Add(winnerGame);
        h.Vm.EnsureOverrideForTest("winner").Identity = Confirmed(Cat.Igdb, "1");
        var loser = h.Vm.EnsureOverrideForTest("loser");
        loser.Identity = Confirmed(Cat.Sgdb, "B");
        var dialogOpenedWith = (Decision: 0L, Identity: 0L); // captured before the merge

        await h.Vm.ApplyScanResultAsync(new ScanResult([winnerGame], [], [], new Dictionary<string, string> { ["loser"] = "winner" },
            new Dictionary<string, ArtworkApplyResult>()));

        var outcome = await h.Vm.ConfirmIdentityAsync("loser", Cand(Cat.Sgdb, "C", "C"), dialogOpenedWith.Decision, dialogOpenedWith.Identity);

        Assert.Equal(IdentityChangeOutcome.StaleSelection, outcome);   // retargeted to the winner - and the merge advanced its revisions
        Assert.NotNull(h.Vm.GetIdentityDialogState("loser"));          // the alias still finds the game
        Assert.Equal("winner", h.Vm.GetIdentityDialogState("loser")!.GameId);
    }
}
