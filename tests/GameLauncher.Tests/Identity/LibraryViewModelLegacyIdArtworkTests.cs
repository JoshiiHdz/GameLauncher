using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>
/// Regression coverage for the second LegacyId integration gap (see GameEntry.LegacyId and
/// ScanResult.LegacyIdRemap): MigrateMergedOverrides correctly bumps a migrated override's artwork/
/// identity revisions as part of the transition (the same bump an ordinary dedup merge already applies -
/// a unit computed against the pre-transition state must never validate against the post-transition id
/// by numeric coincidence), which correctly invalidates the scan worker's PRE-migration unit. But
/// ApplyScanResultAsync's own "carried-forward pixels" recovery (ReconcileArtwork / CommitAutomaticUnit)
/// looked up the game's last-known-displayed GameEntry in a previousGamesById dictionary that, for a game
/// whose id just changed, was still only keyed by its OLD id - so the recovery found nothing, and a
/// perfectly valid automatic cover dropped to the exe icon for one refresh even though nothing about the
/// actual artwork changed.
///
/// Exercised through the real production path, like LibraryViewModelIdentityCommitTests: units are
/// produced by GameScannerService.ResolveGameUnit (the same method a scan's worker uses) and published
/// through ApplyScanResultAsync - not a hand-built ArtworkApplyResult standing in for one.
/// </summary>
public class LibraryViewModelLegacyIdArtworkTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private const int HeightA = 480, HeightB = 640;

    public void Dispose() => _h.Dispose();

    private static CatalogCoverResult Cover(int sourceHeight, bool fromCache = false) =>
        new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(sourceHeight), fromCache);

    // Decoded width is always 320 - sourceHeight 90 is what decodes to exactly HeightA (480), matching
    // LibraryViewModelIdentityCommitTests' own IgdbFinds convention.
    private void IgdbFinds(string id, string title = "Foo", int sourceHeight = 90)
    {
        _h.Igdb.Title = _ => CatalogSearchResult.Found(id, title);
        _h.Igdb.Cover = _ => Cover(sourceHeight);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyDuplicates_FirstScanAfterRestart_RestoresCoverFromMergedIdentity(bool loserConfirmed, bool cancelRecovery)
    {
        IgdbFinds("A");
        var original = Games.Manual("xbox-family", "Foo");
        await _h.Add(original);
        await _h.Scan(original);
        var loser = _h.Vm.EnsureOverrideForTest("xbox-folder");
        loser.Favorite = true;
        if (loserConfirmed)
            loser.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "B", "Foo") };
        Assert.True(_h.Vm.SaveNowForTest());

        using var recoveryCancellation = new CancellationTokenSource();
        var coverCalls = 0;
        var catalog = new FakeCatalog(Cat.Igdb)
        {
            Title = _ => throw new InvalidOperationException("Saved identities must not need title search"),
            Cover = id =>
            {
                if (++coverCalls == 2 && cancelRecovery)
                    recoveryCancellation.Cancel();
                return Cover(id == "B" ? 120 : 90, fromCache: true);
            },
        };
        var context = Cat.Ctx([catalog], legacyRoot: _h.CacheDir);
        var vm = new LibraryViewModel(new SettingsService(_h.DataDir), new PendingUpdateNotesService(_h.DataDir))
        {
            ResolutionContextForTest = () => context,
            IconFallbackForTest = _ => null,
        };
        var game = new GameEntry
        {
            Id = original.Id, LegacyId = "xbox-folder", Name = "Foo", Source = original.Source,
            InstallDir = original.InstallDir, ExecutablePath = original.ExecutablePath,
        };
        var over = vm.GetOverride(game.Id)!;
        var generations = vm.SnapshotIdentityGenerations();
        var unit = GameScannerService.ResolveGameUnit(game, over, over.ArtworkRevision,
            generations, context, CancellationToken.None);
        Assert.True(game.IsCoverArt); // The worker found art; publication used to erase it.

        using var owner = vm.SetRefreshOwnershipForTest();
        using var registration = recoveryCancellation.Token.Register(owner.Cancel);
        var publication = vm.ApplyScanResultAsync(new ScanResult([game], [], [], [],
            new Dictionary<string, ArtworkApplyResult> { [game.Id] = unit },
            LegacyIdRemap: GameScannerService.ComputeLegacyIdRemap([game])),
            ownershipToken: owner, identityGenerationsAtScanStart: generations);

        if (cancelRecovery)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
            Assert.Equal(2, coverCalls); // Actually reached post-merge recovery, not pre-cancelled.
            Assert.False(game.IsCoverArt);
            Assert.Equal("B", vm.GetOverride(game.Id)!.Identity!.Confirmed!.Id);
            return;
        }
        Assert.True(await publication);

        Assert.True(game.Favorite);
        Assert.Null(vm.GetOverride("xbox-folder"));
        Assert.True(game.IsCoverArt);
        Assert.Equal(loserConfirmed ? HeightB : HeightA, IdentityHarness.Height(game));
        Assert.Equal(loserConfirmed ? "B" : "A", vm.GetOverride(game.Id)!.Artwork!.ProviderGameId);
        Assert.Equal(0, catalog.TitleSearches);
        if (loserConfirmed)
            Assert.Equal("B", vm.GetOverride(game.Id)!.Identity!.Confirmed!.Id);
    }

    [Fact]
    public async Task LegacyIdTransition_CarriesForwardTheDisplayedAutomaticCover_ThroughTheRevisionBumpMigrationCauses()
    {
        IgdbFinds("A");

        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame); // resolves identity "A" and shows a real automatic cover

        Assert.Equal(HeightA, IdentityHarness.Height(oldGame));
        var oldOver = _h.Over(oldGame.Id)!;
        Assert.NotNull(oldOver.Artwork);

        // "New-ID entry with LegacyId" - the SAME game, after the next scan's detection-method change
        // gives it a new id. Built the way a real scan's worker builds its own GameEntry (see
        // IdentityHarness.Unit/Fresh): a brand-new instance, same detected title, never the one already
        // on screen.
        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = oldGame.Id, Name = oldGame.DetectedTitle,
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };

        // The unit a real scan would compute: GameScannerService finds the OLD override via LegacyId
        // (ComputeLegacyIdRemap already confirms the transition is unambiguous) and resolves against it -
        // BEFORE migration ever runs. This is exactly the "pre-migration worker result" the real bug is
        // about: by the time it's committed below, migration will have bumped the very revisions this
        // unit was computed against.
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            _h.Vm.SnapshotIdentityGenerations(), _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(oldGame.Id));

        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);

        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { [newGame.Id] = unit },
            LegacyIdRemap: legacyIdRemap));

        // The real, confirmed bug: migration bumps IdentityRevision/ArtworkRevision as part of this exact
        // transition, correctly invalidating the pre-migration unit - CommitAutomaticUnit replaces its
        // rejected provisional pixels with the exe icon, then must recover the real cover from the last
        // known-displayed GameEntry. Without the fix, that recovery finds nothing (previousGamesById was
        // only keyed by the OLD id) and the game is left on the exe icon.
        Assert.Equal(HeightA, IdentityHarness.Height(newGame));
        Assert.True(newGame.IsCoverArt);
    }

    [Fact]
    public async Task LegacyIdTransition_TheVeryFirstScanAfterARestart_ShowsTheCoverImmediately_NoSubsequentScanNeeded()
    {
        // The real, confirmed gap this covers: an earlier version of this test published the migration
        // FIRST (no unit), then separately computed and published a unit afterward - proving recovery on a
        // SECOND scan, not first-scan continuity. Production never does that: GameScannerService computes
        // the unit (via the OLD override, found through LegacyId) and the migration both land in ONE
        // ScanResult, published together in a SINGLE ApplyScanResultAsync call. On a genuinely fresh
        // launch, _allGames is completely empty beforehand - there is no previously-displayed GameEntry at
        // all to recover pixels from - so the cover can only appear if the unit computed for the brand-new
        // id is itself still validated as current, despite the migration that runs in the very same publish.
        IgdbFinds("A");

        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame);
        Assert.True(_h.Vm.SaveNowForTest());

        // "Restart": a brand-new process - a fresh view model over the same settings directory, with a
        // catalog that CANNOT search by title, so a correctly-restored cover can only have come from the
        // persisted override + local cache, never a fresh lookup.
        var igdb2 = new FakeCatalog(Cat.Igdb) { Title = _ => throw new InvalidOperationException("a restart must not need a title search") };
        igdb2.Cover = _ => Cover(90, fromCache: true);
        var context2 = Cat.Ctx([igdb2], legacyRoot: _h.CacheDir);
        var vm2 = new LibraryViewModel(new SettingsService(_h.DataDir), new PendingUpdateNotesService(_h.DataDir))
        {
            IconFallbackForTest = _ => null,
            ResolutionContextForTest = () => context2,
        };

        // "Load old-ID settings": vm2's constructor already loaded settings.json, so the OLD override is
        // live right now, under its OLD id - exactly what GameScannerService's own overridesSnapshot would
        // see at the start of this process's first scan.
        var oldOver = vm2.GetOverride("xbox-oldfolder")!;
        Assert.NotNull(oldOver.Artwork);

        // "Resolve the new entry against that old override": this process's own detection-method change
        // computes the new, family-anchored id for the very same install - and GameScannerService finds
        // the OLD override via LegacyId and resolves THIS unit against it, before anything is published.
        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = "xbox-oldfolder", Name = "Foo",
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };
        // Captured once, exactly as RefreshAsync does - the SAME scan-start snapshot both the worker's own
        // unit AND ApplyScanResultAsync's own re-stamping eligibility check are resolved against.
        var identityGenerationsAtScanStart = vm2.SnapshotIdentityGenerations();
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            identityGenerationsAtScanStart, context2, CancellationToken.None,
            coverDisplayed: vm2.SnapshotDisplayedCovers().Contains("xbox-oldfolder"));
        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);

        // "Publish migration and artwork together" - the one, real ApplyScanResultAsync call a real first
        // scan after this restart would actually make.
        await vm2.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { ["xbox-family"] = unit },
            LegacyIdRemap: legacyIdRemap),
            identityGenerationsAtScanStart: identityGenerationsAtScanStart);

        var migratedOver = vm2.GetOverride("xbox-family")!;
        Assert.Equal("A", Assert.Single(migratedOver.Identity!.Resolved).Id); // the identity itself survived the restart + migration
        Assert.Null(vm2.GetOverride("xbox-oldfolder"));

        // The actual assertion this gap is about: the cover is already showing on THIS first scan - no
        // second scan required to "warm it up".
        Assert.Equal(HeightA, IdentityHarness.Height(newGame));
        Assert.Equal(0, igdb2.TitleSearches); // restored without ever needing a title search
        Assert.Equal(new[] { "A" }, igdb2.FetchedIds); // fetched by the id the migrated identity already carried
        Assert.Equal(ArtworkRetrievalMethod.LocalCache, migratedOver.Artwork!.RetrievedFrom);
    }

    [Fact]
    public async Task AMergeThatCombinesTwoSides_NeverRubberStampsAStaleSamePublishUnit_OnlyAPureAdoptionDoes()
    {
        // The fix above (re-stamping a same-publish unit's own captured markers to the live, post-
        // migration values) is scoped to a PURE ADOPTION - a winner with NO prior override, where the
        // loser's data moves across UNCHANGED. It must never apply to an ORDINARY MERGE, where the winner
        // ALREADY had its own override and the two sides' identity data is genuinely COMBINED (see
        // MigrateMergedIdentity) - there, a unit computed against only the winner's OWN pre-merge identity
        // can legitimately become stale, and rubber-stamping it would let it silently overwrite the merge's
        // own, correct result. Concretely: a confirmed loser identity must always beat an automatic winner
        // (S10) - if a same-publish unit reaffirming the winner's OLD automatic identity were wrongly
        // validated as current, it would stomp the loser's confirmation right back out.
        IgdbFinds("W", "Foo"); // the winner's own, independently-resolved AUTOMATIC identity
        var winner = Games.Ea("ea-winner", "Foo");
        await _h.Add(winner);
        await _h.Scan(winner);
        Assert.Equal("W", Assert.Single(_h.Over(winner.Id)!.Identity!.Resolved).Id);

        // The loser already carries its OWN confirmed identity - set directly, exactly like the existing
        // Merge_ALosersConfirmation_BeatsAnAutomaticWinner_S10 test does, no scan needed for it.
        var loserOver = _h.Vm.EnsureOverrideForTest("manual-loser");
        loserOver.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "L", "Foo") };

        // The SAME scan that recognizes the merge relationship also, independently, rescans the winner's
        // own (still pre-merge) state - exactly what a real scan does: every surviving entry gets its own
        // ResolveGameUnit call, merge-awareness is a completely separate step. This unit reaffirms "W",
        // unchanged - computed BEFORE the merge, with no knowledge the loser is about to beat it.
        var winnerOver = _h.Over(winner.Id)!;
        var freshWinner = IdentityHarness.Fresh(winner);
        var unit = GameScannerService.ResolveGameUnit(freshWinner, winnerOver, winnerOver.ArtworkRevision,
            _h.Vm.SnapshotIdentityGenerations(), _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(winner.Id));

        // Published together, exactly like the pure-adoption tests above - the one difference is the
        // winner already had an override, so MigrateMergedOverrides takes the MERGE branch, not adoption.
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [freshWinner],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-loser"] = "ea-winner" },
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { ["ea-winner"] = unit }));

        // The merge's own correct result (S10: a confirmation beats an automatic match) survives - the
        // stale, same-publish unit for the winner's OWN pre-merge identity was correctly treated as
        // superseded, not rubber-stamped into overwriting it.
        Assert.Equal("L", _h.Over("ea-winner")!.Identity!.Confirmed!.Id);
    }

    [Fact]
    public async Task OrdinaryDedupMerge_WinnerHasNoOverride_NeverRubberStampsItsOwnUnrelatedAutomaticUnit_OverTheLosersConfirmedIdentity()
    {
        // The real, confirmed gap this covers: "the winner has no override" alone was being treated as
        // proof a same-publish unit could be trusted after migration - but it proves nothing by itself. An
        // ORDINARY dedup winner's own unit (when it happens to have one) is resolved from the WINNER's own
        // prior state - here, nothing at all, since it's brand new - which has NO connection whatsoever to
        // whatever a merging loser happens to carry. A loser's own confirmed identity must still beat the
        // winner's unrelated automatic match, exactly as it already must for a winner that DOES have an
        // override (AMergeThatCombinesTwoSides... above) - this is the same guarantee, for the adoption
        // branch instead of the merge branch. Re-stamping must be restricted to a VERIFIED LegacyId
        // transition (see oldByNewId) - never fire just because MergedGameIds also happens to leave the
        // winner override-less.
        var loserOver = _h.Vm.EnsureOverrideForTest("manual-loser");
        loserOver.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "L", "Foo") };

        // The winner is brand new this scan - no override, no prior identity at all - and its own worker
        // independently resolves "W" automatically, with nothing tying it to the loser's data.
        IgdbFinds("W", "Foo");
        var winner = Games.Ea("ea-winner", "Foo");
        var unit = GameScannerService.ResolveGameUnit(winner, over: null, asOfRevision: 0,
            _h.Vm.SnapshotIdentityGenerations(), _h.Context, CancellationToken.None);

        // No LegacyIdRemap at all here - a plain dedup merge, exactly like a real scan discovering the
        // relationship between an EA entry and a Manual one for the first time.
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [winner],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: new Dictionary<string, string> { ["manual-loser"] = "ea-winner" },
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { ["ea-winner"] = unit }));

        // The loser's confirmation survives - the winner's own, unrelated automatic unit is never adopted
        // over it just because the winner happened to have no override of its own.
        Assert.Equal("L", _h.Vm.GetOverride("ea-winner")!.Identity!.Confirmed!.Id);
    }

    [Fact]
    public async Task LegacyIdTransition_AConcurrentConfirmOnTheOldEntry_IsNeverOverwrittenByTheStaleWorkerResult()
    {
        // The real, confirmed gap this covers: a scan can take a while, and its worker resolves the OLD
        // entry's identity (automatically, to "A") well before the scan's own result is ever published. If
        // the user opens "Identify Game" and confirms a DIFFERENT identity ("B") on that SAME, still-
        // displayed old entry before this scan's publish finally runs, the live old override now reflects
        // "B" - with its own, genuinely bumped IdentityRevision. The override adopted under the NEW id must
        // carry "B" forward, never the worker's now-stale "A" - even though the new id, like any LegacyId
        // target, still has no override of its own to protect it the ordinary way. Re-stamping must verify
        // the OLD override's revision is UNCHANGED since the worker read it, not just that a LegacyId
        // transition exists.
        IgdbFinds("A");
        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame);
        var oldOver = _h.Over(oldGame.Id)!;
        Assert.Equal("A", Assert.Single(oldOver.Identity!.Resolved).Id);

        // The scan's worker computes its unit for the new id, from the old override's CURRENT ("A") state -
        // exactly like a real scan does.
        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = oldGame.Id, Name = oldGame.DetectedTitle,
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };
        var identityGenerationsAtScanStart = _h.Vm.SnapshotIdentityGenerations();
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            identityGenerationsAtScanStart, _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(oldGame.Id));

        // "The user confirms B on the old entry before publication" - a real commit landing on the SAME
        // live override the worker already read from, strictly AFTER the worker captured its own snapshot.
        // Generation is deliberately left untouched here (unlike a real ConfirmIdentityAsync, which also
        // bumps it) so this test isolates the IdentityRevision check specifically - the OTHER regression,
        // LegacyIdTransition_ARejectedCandidateOnTheOldEntry..., isolates the generation check the same way.
        oldOver.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Foo") };
        oldOver.IdentityRevision++;

        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { [newGame.Id] = unit },
            LegacyIdRemap: legacyIdRemap),
            identityGenerationsAtScanStart: identityGenerationsAtScanStart);

        // "B" (the user's real, later decision) survives - the worker's stale "A" is never adopted over it.
        Assert.Equal("B", _h.Vm.GetOverride("xbox-family")!.Identity!.Confirmed!.Id);
        Assert.Null(_h.Vm.GetOverride("xbox-oldfolder"));
    }

    [Fact]
    public async Task LegacyIdTransition_ARejectedCandidateOnTheOldEntry_IsNeverOverwrittenByTheStaleWorkerResult()
    {
        // The real, confirmed gap this covers: rejecting a candidate that is NOT the active identity never
        // moves IdentityRevision (SelectActive's own key is unchanged) or ArtworkRevision at all - only
        // DecisionRevision and the old entry's own identity GENERATION advance (CommitIdentityHalf/
        // CommitIdentityChange bump the generation on ANY recorded change, keyChanged or not). The earlier
        // revision-only check (see AConcurrentConfirmOnTheOldEntry... above) would wrongly treat this unit
        // as still current and let its older, pre-rejection record erase the rejection.
        IgdbFinds("A");
        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame);
        var oldOver = _h.Over(oldGame.Id)!;
        Assert.Equal("A", Assert.Single(oldOver.Identity!.Resolved).Id);

        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = oldGame.Id, Name = oldGame.DetectedTitle,
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };
        var identityGenerationsAtScanStart = _h.Vm.SnapshotIdentityGenerations();
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            identityGenerationsAtScanStart, _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(oldGame.Id));

        var preRejectIdentityRevision = oldOver.IdentityRevision;
        var preRejectArtworkRevision = oldOver.ArtworkRevision;

        // "The user rejects a different, non-active candidate on the old entry before publication" - a real
        // decision, through the same production path RejectIdentityCandidate uses everywhere else.
        var dialogState = _h.Vm.GetIdentityDialogState(oldGame.Id)!;
        var rejectOutcome = _h.Vm.RejectIdentityCandidate(oldGame.Id, new IdentityKey(Cat.Sgdb, "Z"), "z",
            dialogState.DecisionRevision, dialogState.IdentityRevision);
        Assert.Equal(IdentityChangeOutcome.Success, rejectOutcome);

        // The rejection is recorded, but it did NOT move either revision this unit's own check compares -
        // only the generation did. If this test didn't hold, it wouldn't actually exercise the gap.
        Assert.Contains(oldOver.Identity!.Rejected, r => r.Key == new IdentityKey(Cat.Sgdb, "Z"));
        Assert.Equal(preRejectIdentityRevision, oldOver.IdentityRevision);
        Assert.Equal(preRejectArtworkRevision, oldOver.ArtworkRevision);

        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { [newGame.Id] = unit },
            LegacyIdRemap: legacyIdRemap),
            identityGenerationsAtScanStart: identityGenerationsAtScanStart);

        // The rejection survives - the worker's older, pre-rejection record is never adopted over it.
        Assert.Contains(_h.Vm.GetOverride("xbox-family")!.Identity!.Rejected, r => r.Key == new IdentityKey(Cat.Sgdb, "Z"));
        Assert.Null(_h.Vm.GetOverride("xbox-oldfolder"));
    }

    [Fact]
    public async Task LegacyIdTransition_WithNoScanStartSnapshotSupplied_NeverRestamps_FailsClosedNotOpen()
    {
        // Found while mutation-testing the generation check above: a caller that can't PROVE the old id's
        // generation is unchanged (no identityGenerationsAtScanStart at all - every caller except
        // RefreshAsync's own real scan path) must never be treated as "no evidence of a problem, so it's
        // fine" - that would silently re-open exactly the gap the generation check exists to close. Reuses
        // the rejected-candidate setup above (revision-only would wrongly pass) but omits the snapshot
        // entirely, rather than supplying one that happens to mismatch.
        IgdbFinds("A");
        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame);
        var oldOver = _h.Over(oldGame.Id)!;

        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = oldGame.Id, Name = oldGame.DetectedTitle,
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            _h.Vm.SnapshotIdentityGenerations(), _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(oldGame.Id));

        var dialogState = _h.Vm.GetIdentityDialogState(oldGame.Id)!;
        Assert.Equal(IdentityChangeOutcome.Success, _h.Vm.RejectIdentityCandidate(oldGame.Id,
            new IdentityKey(Cat.Sgdb, "Z"), "z", dialogState.DecisionRevision, dialogState.IdentityRevision));

        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);

        // No identityGenerationsAtScanStart argument at all - the default every caller but RefreshAsync uses.
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { [newGame.Id] = unit },
            LegacyIdRemap: legacyIdRemap));

        Assert.Contains(_h.Vm.GetOverride("xbox-family")!.Identity!.Rejected, r => r.Key == new IdentityKey(Cat.Sgdb, "Z"));
    }

    [Fact]
    public async Task LegacyIdTransition_AResetDuringTheInFlightScan_IsNeverOverwrittenByTheStaleWorkerResult()
    {
        // Closes the disclosed test gap for the ArtworkRevision half of this eligibility check (the
        // IdentityRevision half is AConcurrentConfirmOnTheOldEntry... above). Reset is an existing, ordinary
        // NON-PINNED artwork change - CommitArtworkChange bumps ArtworkRevision immediately, then a separate
        // automatic unit re-resolves the cover (see ResetCoverToAutomaticAsync) - not a new feature invented
        // for this test.
        IgdbFinds("A", sourceHeight: 90); // decodes to HeightA

        var oldGame = Games.Manual("xbox-oldfolder", "Foo");
        await _h.Add(oldGame);
        await _h.Scan(oldGame);
        Assert.Equal(HeightA, IdentityHarness.Height(oldGame));
        var oldOver = _h.Over(oldGame.Id)!;
        var preResetIdentityRevision = oldOver.IdentityRevision;
        var preResetArtworkRevision = oldOver.ArtworkRevision;

        // The scan's worker computes its unit for the new id, from the old override's CURRENT (pre-reset) state.
        var newGame = new GameEntry
        {
            Id = "xbox-family", LegacyId = oldGame.Id, Name = oldGame.DetectedTitle,
            ExecutablePath = oldGame.ExecutablePath, InstallDir = oldGame.InstallDir, Source = oldGame.Source,
        };
        var identityGenerationsAtScanStart = _h.Vm.SnapshotIdentityGenerations();
        var unit = GameScannerService.ResolveGameUnit(newGame, oldOver, oldOver.ArtworkRevision,
            identityGenerationsAtScanStart, _h.Context, CancellationToken.None,
            coverDisplayed: _h.Vm.SnapshotDisplayedCovers().Contains(oldGame.Id));

        // "Reset during the in-flight scan" - a real, ordinary artwork change landing on the SAME live
        // override the worker already read from, strictly AFTER the worker captured its own snapshot. A
        // DIFFERENT cover height so Reset's own, fresh result is distinguishable from the worker's stale one.
        IgdbFinds("A", sourceHeight: 120); // decodes to HeightB
        Assert.Equal(ArtworkChangeOutcome.Success, await _h.Vm.ResetCoverToAutomaticAsync(oldGame.Id));

        // Exactly the gap this test targets: ArtworkRevision moved, IdentityRevision did not - proving this
        // case needs its own, separate guard from the identity-side regressions above.
        Assert.Equal(preResetIdentityRevision, oldOver.IdentityRevision);
        Assert.NotEqual(preResetArtworkRevision, oldOver.ArtworkRevision);
        Assert.Equal(HeightB, IdentityHarness.Height(oldGame)); // Reset's own, fresh result is what's showing now

        var legacyIdRemap = GameScannerService.ComputeLegacyIdRemap([newGame]);
        await _h.Vm.ApplyScanResultAsync(new ScanResult(
            Games: [newGame],
            NewDateAddedByGameId: [],
            HealedWatchedFolders: [],
            MergedGameIds: [],
            ArtworkResultsByGameId: new Dictionary<string, ArtworkApplyResult> { [newGame.Id] = unit },
            LegacyIdRemap: legacyIdRemap),
            identityGenerationsAtScanStart: identityGenerationsAtScanStart);

        // Reset's own, LATER result survives - the worker's stale (pre-reset) cover is never adopted over it.
        Assert.Equal(HeightB, IdentityHarness.Height(newGame));
    }
}
