using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;
using static GameLauncher.ViewModels.LibraryViewModel;

namespace GameLauncher.Tests.Identity;

/// <summary>The audit's four corrections, each as the regression that would have caught it:
///  1. an outage of the primary catalog must not replace a working cover with a fallback's (design 4.8, I5);
///  3. the negative-result cooldown must keep its own evidence - THREE consecutive scans, each fed the previous scan's record;
///  4. the alternative-name path (4.5) and verified launcher ids (D7).
/// (2, the dialog's lifetime, is in IdentifyGameViewModelTests.)  Covers are told apart by decoded height (A = 480, B = 640).</summary>
public class IdentityOutageCooldownAndAlternativeNameTests : IDisposable
{
    private readonly FakeCatalog _igdb = new(Cat.Igdb, "IGDB");
    private readonly FakeCatalog _sgdb = new(Cat.Sgdb, "SteamGridDB");
    private readonly IdentityHarness _h = new();
    private const int HeightA = 480, HeightB = 640;

    public void Dispose() => _h.Dispose();

    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");
    private static CatalogSearchResult Found(string id, string title) => CatalogSearchResult.Found(id, title);
    private static CatalogCoverResult Outage() => new(CoverLookupStatus.Unavailable, null, false);
    private static CatalogCoverResult Cover(int sourceHeight) => new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(sourceHeight), false);

    private UnitOutput Run(GameEntry game, ResolutionContext ctx, GameIdentityRecord? prior = null, ArtworkSelection? current = null,
        bool displayed = false) =>
        AutomaticResolver.Run(Cat.Input(game, prior, current, displayed: displayed), ctx, CancellationToken.None);

    private static GameIdentityRecord IgdbIdentified(GameEntry game, string id = "7")
    {
        var prior = new GameIdentityRecord();
        prior.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Igdb, id, game.DetectedTitle));
        return prior;
    }

    // ================================================================================================================
    // 1. An outage never replaces a working cover
    // ================================================================================================================

    [Fact]
    public void ExistingIgdbCover_IgdbCoverUnavailable_FallbackWouldSucceed_TheCoverIsKept_AndTheFallbackIsNotEvenFetched()
    {
        var game = Foo();
        _igdb.Cover = _ => Outage();
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);

        var output = Run(game, Cat.Ctx([_igdb, _sgdb]), IgdbIdentified(game), Cat.Auto(Cat.Igdb, "7"), displayed: true);

        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);      // not a Set from SteamGridDB, not a Clear
        Assert.Equal(0, _sgdb.CoverFetches);                           // a working cover is not fetched over
        Assert.Contains(output.NewRecord!.Resolved, r => r.Namespace == Cat.Igdb && r.Id == "7"); // identity retained
    }

    [Fact]
    public void ExistingIgdbCover_IgdbCoverUnavailable_SteamCdnWouldSucceed_TheCoverIsKept()
    {
        var game = Games.Steam("1091500", "Cyberpunk 2077");
        var launcherCalls = 0;
        _igdb.Cover = _ => Outage();
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => { launcherCalls++; return (TestBitmaps.Cover(), false); });

        var output = Run(game, ctx, IgdbIdentified(game), Cat.Auto(Cat.Igdb, "7"), displayed: true);

        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);
        Assert.Equal(0, launcherCalls);
    }

    // ---- 1b. ...but only a cover that HAS pixels: a recorded selection with no image behind it is not worth protecting -------------------

    [Fact]
    public void RecordedIgdbCover_WithNoDisplayedPixelsAndNoUsableCache_IgdbDown_TheFallbackSuppliesTheCover()
    {
        var game = Foo();
        _igdb.Cover = _ => Outage();                                   // and its cache is empty (the fake's default)
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);

        var output = Run(game, Cat.Ctx([_igdb, _sgdb]), IgdbIdentified(game), Cat.Auto(Cat.Igdb, "7"), displayed: false);

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
        Assert.Equal(1, _sgdb.CoverFetches);
        Assert.Contains("7", _igdb.CacheReads);                        // the resolver LOOKED for usable pixels before giving up on them
        Assert.Contains(output.NewRecord!.Resolved, r => r.Namespace == Cat.Igdb && r.Id == "7"); // the identity is untouched
    }

    [Fact]
    public void RecordedIgdbCover_NotDisplayed_ButValidInItsCache_IgdbCircuitOpen_TheCachedPixelsAreRestored_NotTheFallback()
    {
        var game = Foo();
        var cached = TestBitmaps.Distinct(90);
        _igdb.CachedCover = id => id == "7" ? cached : null;
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);
        var ctx = Cat.Ctx([_igdb, _sgdb]);
        for (var i = 0; i < 3; i++)
            ctx.Breaker.Note(Cat.Igdb, unavailable: true);              // the per-scan breaker has already given up on IGDB's network

        var output = Run(game, ctx, IgdbIdentified(game), Cat.Auto(Cat.Igdb, "7"), displayed: false);

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Same(cached, output.Artwork.Image);
        Assert.Equal((ArtworkProvider.Igdb, "7", ArtworkRetrievalMethod.LocalCache),
            (output.Artwork.Selection!.Provider, output.Artwork.Selection.ProviderGameId, output.Artwork.Selection.RetrievedFrom));
        Assert.Equal(0, _sgdb.CoverFetches);
    }

    [Fact]
    public void RecordedIgdbCover_NotDisplayed_WithAnUnreadableCache_IgdbCircuitOpen_TheFallbackIsUsed()
    {
        var game = Foo();
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);
        var ctx = Cat.Ctx([_igdb, _sgdb]);
        for (var i = 0; i < 3; i++)
            ctx.Breaker.Note(Cat.Igdb, unavailable: true);

        var output = Run(game, ctx, IgdbIdentified(game), Cat.Auto(Cat.Igdb, "7"), displayed: false);

        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
    }

    [Fact]
    public void RecordedSteamCdnCover_NotDisplayed_WhenSteamCdnCanStillSupplyIt_ItIsRestored_NotReplacedByTheFallback()
    {
        var game = Games.Steam("1091500", "Cyberpunk 2077");
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Title = _ => Found("9", "Cyberpunk 2077");
        _sgdb.Cover = _ => Cover(120);
        var launcher = new ArtworkSelection
        {
            Provider = ArtworkProvider.SteamCdn, ProviderGameId = "steam-1091500", IsUserSelected = false,
            DerivedFrom = new IdentityKey(IdentifierNamespace.SteamApp, "1091500"),
        };
        var launcherCalls = 0;

        var output = Run(game, Cat.Ctx([_igdb, _sgdb], launcherArt: (_, _) => { launcherCalls++; return (TestBitmaps.Cover(), true); }),
            null, launcher, displayed: false);

        Assert.Equal(ArtworkProvider.SteamCdn, output.Artwork.Selection!.Provider);
        Assert.Equal(0, _sgdb.CoverFetches);
        Assert.Equal(1, launcherCalls);                                // asked once, not once for the rule and again for its own step
    }

    [Fact]
    public void RecordedSteamCdnCover_NotDisplayed_WhenSteamCdnCannotSupplyIt_TheFallbackIsUsed()
    {
        var game = Games.Steam("1091500", "Cyberpunk 2077");
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Title = _ => Found("9", "Cyberpunk 2077");
        _sgdb.Cover = _ => Cover(120);
        var launcher = new ArtworkSelection
        {
            Provider = ArtworkProvider.SteamCdn, ProviderGameId = "steam-1091500", IsUserSelected = false,
            DerivedFrom = new IdentityKey(IdentifierNamespace.SteamApp, "1091500"),
        };

        var output = Run(game, Cat.Ctx([_igdb, _sgdb], launcherArt: (_, _) => (null, false)), null, launcher, displayed: false);

        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
    }

    /// <summary>The audit's scenario, through the real commit: the app restarts (a brand-new view model over the persisted settings)
    /// with IGDB metadata recorded but no image behind it, IGDB is unavailable and SteamGridDB works - the card must get a cover.</summary>
    [Fact]
    public async Task Restart_WithPersistedIgdbMetadataButNoUsableIgdbCache_IgdbDown_TheRealCardReceivesTheFallbackArtwork()
    {
        _igdb.Title = _ => Found("A", "Foo");
        _igdb.Cover = _ => Cover(90);
        _sgdb.Title = _ => Found("S", "Foo");
        _sgdb.Cover = _ => Cover(120);
        var game = await _h.Add(Foo());
        _h.Context = Cat.Ctx([_igdb, _sgdb]);
        var first = IdentityHarness.Fresh(game);
        await _h.Publish([first], (first.Id, _h.Unit(first)));
        Assert.Equal(HeightA, IdentityHarness.Height(first));
        Assert.True(_h.Vm.SaveNowForTest());                                  // ...and that is what is on disk when the app closes

        // ---- restart: nothing is displayed, IGDB is down, and its cache holds nothing usable ----
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _igdb.Cover = _ => Outage();
        var restarted = new LibraryViewModel(new SettingsService(_h.DataDir), new PendingUpdateNotesService(_h.DataDir))
        {
            AssetStoreDirOverrideForTest = _h.AssetDir, IconFallbackForTest = _ => null,
        };
        var startup = IdentityHarness.Fresh(game);
        await restarted.ApplyScanResultAsync(new ScanResult([startup], [], [], [], new Dictionary<string, ArtworkApplyResult>()));
        Assert.Null(IdentityHarness.Height(startup));                          // a recorded cover, and an icon: the state the audit described
        Assert.Equal(ArtworkProvider.Igdb, restarted.GetOverride(game.Id)!.Artwork!.Provider);
        Assert.False(restarted.SnapshotDisplayedCovers().Contains(game.Id));
        var sgdbFetchesBefore = _sgdb.CoverFetches;

        var scanned = IdentityHarness.Fresh(game);
        var unit = _h.Unit(scanned, restarted, Cat.Ctx([_igdb, _sgdb]));
        await restarted.ApplyScanResultAsync(new ScanResult([scanned], [], [], [], new Dictionary<string, ArtworkApplyResult> { [scanned.Id] = unit }));

        Assert.Equal(HeightB, IdentityHarness.Height(scanned));                // SteamGridDB's cover is on the published card
        var over = restarted.GetOverride(game.Id)!;
        Assert.Equal((ArtworkProvider.SteamGridDb, "S"), (over.Artwork!.Provider, over.Artwork.ProviderGameId));
        Assert.Contains(over.Identity!.Resolved, r => r.Namespace == Cat.Igdb && r.Id == "A"); // IGDB is still the identity
        Assert.Equal(sgdbFetchesBefore + 1, _sgdb.CoverFetches);
    }

    /// <summary>The in-place path (Reset / Confirm / Clear follow up with an ordinary unit for the card that is on screen) takes "is the
    /// cover displayed" from the live card itself, not from a scan snapshot.</summary>
    [Fact]
    public async Task InPlaceUnit_WithACoverOnTheCard_IgdbDown_KeepsThatCoverAndAsksNoFallback()
    {
        _igdb.Title = _ => Found("A", "Foo");
        _igdb.Cover = _ => Cover(90);
        _sgdb.Title = _ => Found("S", "Foo");
        _sgdb.Cover = _ => Cover(120);
        var game = await _h.Add(Foo());
        _h.Context = Cat.Ctx([_igdb, _sgdb]);
        _h.Vm.ResolutionContextForTest = () => _h.Context;
        var shown = IdentityHarness.Fresh(game);
        await _h.Publish([shown], (shown.Id, _h.Unit(shown)));
        Assert.Equal(HeightA, IdentityHarness.Height(shown));
        _igdb.Cover = _ => Outage();
        var sgdbFetchesBefore = _sgdb.CoverFetches;

        await _h.Vm.RunAutomaticUnitAsync(game.Id, "Reset", force: true);

        Assert.Equal(HeightA, IdentityHarness.Height(shown));
        Assert.Equal(ArtworkProvider.Igdb, _h.Over(game.Id)!.Artwork!.Provider);
        Assert.Equal(sgdbFetchesBefore, _sgdb.CoverFetches);
    }

    [Fact]
    public void NoPriorCover_IgdbCoverUnavailable_TheFallbackIsUsed()
    {
        var game = Foo();
        _igdb.Cover = _ => Outage();
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);

        var output = Run(game, Cat.Ctx([_igdb, _sgdb]), IgdbIdentified(game), current: null);

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
    }

    [Fact]
    public void ExistingFallbackCover_IgdbDown_TheSameFallbackMayRefreshIt()
    {
        var game = Foo();
        var prior = new GameIdentityRecord();
        prior.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Sgdb, "9", "Foo"));
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Cover = _ => Cover(120);

        var output = Run(game, Cat.Ctx([_igdb, _sgdb]), prior, Cat.Auto(Cat.Sgdb, "9", ArtworkProvider.SteamGridDb));

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);   // same source as what is shown: not a replacement by another
        Assert.Equal("9", output.Artwork.Selection!.ProviderGameId);
    }

    [Fact]
    public void ExistingSteamCdnCover_IgdbIdentityLookupDown_TheFallbackCatalogDoesNotReplaceIt()
    {
        var game = Games.Steam("1091500", "Cyberpunk 2077");
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Title = _ => Found("9", "Cyberpunk 2077");
        _sgdb.Cover = _ => Cover(120);
        var launcher = new ArtworkSelection
        {
            Provider = ArtworkProvider.SteamCdn, ProviderGameId = "steam-1091500", IsUserSelected = false,
            DerivedFrom = new IdentityKey(IdentifierNamespace.SteamApp, "1091500"),
        };

        var output = Run(game, Cat.Ctx([_igdb, _sgdb], launcherArt: (_, _) => (TestBitmaps.Cover(), true)), null, launcher);

        // Steam CDN refreshing ITS OWN cover is the same source and fine; what must not happen is SteamGridDB replacing it.
        Assert.True(output.Artwork.Kind == ArtworkHalfKind.None || output.Artwork.Selection!.Provider == ArtworkProvider.SteamCdn);
        Assert.Equal(0, _sgdb.CoverFetches);
    }

    [Fact]
    public void AnUnauthorizedExistingCover_IsNotPreserved_TheFallbackMayReplaceIt()
    {
        var game = Foo();
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Title = _ => Found("9", "Foo");
        _sgdb.Cover = _ => Cover(120);

        // the current cover claims IGDB game 7, but nothing in the (empty) record makes IGDB 7 the active identity
        var output = Run(game, Cat.Ctx([_igdb, _sgdb]), null, Cat.Auto(Cat.Igdb, "7"));

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
    }

    [Fact]
    public async Task ThroughTheRealCommit_TheDisplayedImageAndItsAttributionAreUnchanged_ThenIgdbRecovers()
    {
        _igdb.Title = _ => Found("A", "Foo");
        _igdb.Cover = _ => Cover(90);
        _sgdb.Title = _ => Found("S", "Foo");
        _sgdb.Cover = _ => Cover(120);
        var game = await _h.Add(Foo());
        _h.Context = Cat.Ctx([_igdb, _sgdb]);
        _h.Vm.ResolutionContextForTest = () => _h.Context;
        var first = IdentityHarness.Fresh(game);
        await _h.Publish([first], (first.Id, _h.Unit(first)));
        Assert.Equal(HeightA, IdentityHarness.Height(first));
        var before = _h.Over(game.Id)!.Artwork!;
        Assert.Equal((ArtworkProvider.Igdb, "A"), (before.Provider, before.ProviderGameId));

        _igdb.Cover = _ => Outage();                                        // IGDB's cover host goes down; SteamGridDB is fine
        var second = IdentityHarness.Fresh(game);
        await _h.Publish([second], (second.Id, _h.Unit(second)));

        var after = _h.Over(game.Id)!.Artwork!;
        Assert.Equal(HeightA, IdentityHarness.Height(second));               // the same cover, not SteamGridDB's 640
        Assert.Equal((ArtworkProvider.Igdb, "A", new IdentityKey(Cat.Igdb, "A")), (after.Provider, after.ProviderGameId, after.DerivedFrom));
        Assert.Equal(0, _sgdb.CoverFetches);

        _igdb.Cover = _ => Cover(150);                                      // recovery: IGDB's own cover comes back
        var third = IdentityHarness.Fresh(game);
        await _h.Publish([third], (third.Id, _h.Unit(third)));
        Assert.Equal(800, IdentityHarness.Height(third));                     // the 150 px source decodes to 800: IGDB's own cover, freshly fetched
        Assert.Equal(ArtworkProvider.Igdb, _h.Over(game.Id)!.Artwork!.Provider);
    }

    // ================================================================================================================
    // 3. The cooldown keeps its own evidence
    // ================================================================================================================

    [Theory]
    [InlineData("NoMatch")]
    [InlineData("Ambiguous")]
    public void ThreeConsecutiveScans_EachFedTheLastRecord_KeepTheOriginalResultAndTimestamp_AndSearchOnlyOnce(string kind)
    {
        var clock = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var game = Foo();
        if (kind == "Ambiguous")
        {
            _igdb.Title = _ => CatalogSearchResult.Ambiguous();
            _sgdb.Title = _ => CatalogSearchResult.Ambiguous();
        }

        ResolutionContext Ctx() => Cat.Ctx([_igdb, _sgdb], now: () => clock);

        var scan1 = Run(game, Ctx());
        var attempted = scan1.NewRecord!.LastAttempt!;
        Assert.Equal(LookupOutcome.Create(kind), attempted.Outcome);
        var searchesAfterScan1 = _igdb.TitleSearches;
        var fallbackSearchesAfterScan1 = _sgdb.TitleSearches;   // (0 when the name was ambiguous: that blocks the fallback's guess)

        clock = clock.AddHours(1);
        var scan2 = Run(game, Ctx(), scan1.NewRecord);           // inside the cooldown: nobody is asked
        clock = clock.AddHours(1);
        var scan3 = Run(game, Ctx(), scan2.NewRecord);           // ...and the record scan 2 returned must still say why

        Assert.Equal(searchesAfterScan1, _igdb.TitleSearches);
        Assert.Equal(fallbackSearchesAfterScan1, _sgdb.TitleSearches);
        foreach (var scan in new[] { scan2, scan3 })
        {
            Assert.Equal(LookupOutcome.Create(kind), scan.NewRecord!.LastAttempt!.Outcome);      // never "NotConfigured"
            Assert.Equal(attempted.At, scan.NewRecord.LastAttempt!.At);                          // the cooldown clock is the ORIGINAL attempt's
            Assert.Equal(attempted.Trigger, scan.NewRecord.LastAttempt.Trigger);
        }

        clock = clock.AddHours(5);                                // 7h after the original attempt: the cooldown has genuinely ended
        var scan4 = Run(game, Ctx(), scan3.NewRecord);
        Assert.True(_igdb.TitleSearches > searchesAfterScan1);
        Assert.True(scan4.NewRecord!.LastAttempt!.At > attempted.At);
    }

    [Fact]
    public async Task ThroughTheRealCommit_TheSecondAndThirdScanWriteNothing_AndTheStateStillExplainsItself()
    {
        var game = await _h.Add(Foo());
        var first = IdentityHarness.Fresh(game);
        await _h.Publish([first], (first.Id, _h.Unit(first)));
        var searches = _h.Igdb.TitleSearches;
        var attempt = _h.Record(game.Id)!.LastAttempt!;
        var generation = _h.Vm.IdentityGenerationForTest(game.Id);

        for (var i = 0; i < 2; i++)
        {
            var next = IdentityHarness.Fresh(game);
            await _h.Publish([next], (next.Id, _h.Unit(next)));
            Assert.Equal(IdentityHalf.ValidatedNoChange, _h.Vm.UnitReportsForTest[next.Id].Identity);
            Assert.True(next.NeedsIdentity);                                   // still badged, still explained
            Assert.Contains("No confident match", next.IdentityBadgeText);
        }

        Assert.Equal(searches, _h.Igdb.TitleSearches);
        Assert.Equal(attempt.At, _h.Record(game.Id)!.LastAttempt!.At);
        Assert.Equal(generation, _h.Vm.IdentityGenerationForTest(game.Id));    // nothing was written
    }

    [Fact]
    public void EveryProviderSkippedByTheBreaker_IsAnOutage_NotNotConfigured()
    {
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        var ctx = Cat.Ctx([_igdb]);
        for (var i = 0; i < 3; i++)
            Run(Games.Manual($"manual-{i}", $"Game {i}"), ctx);

        var skipped = Run(Games.Manual("manual-9", "Game 9"), ctx);

        Assert.Equal(LookupOutcome.Unavailable, skipped.NewRecord!.LastAttempt!.Outcome);
    }

    // ================================================================================================================
    // 4a. The alternative-name path (design 4.5)
    // ================================================================================================================

    [Fact]
    public void WhenThePrimaryTitleFindsNothing_ExactlyOneAlternativeNameOwner_IsResolved_WithItsOwnTier()
    {
        _igdb.Title = _ => CatalogSearchResult.NoMatch();
        _igdb.AlternativeName = _ => Found("114795", "Apex Legends");

        var output = Run(Games.Manual("manual-apex", "Apex"), Cat.Ctx([_igdb]));

        var resolved = Assert.Single(output.NewRecord!.Resolved);
        Assert.Equal((Cat.Igdb, "114795", IdentityTier.AlternativeName), (resolved.Namespace, resolved.Id, resolved.Tier));
        Assert.Equal(new[] { "Apex" }, _igdb.AlternativeSearches);
        Assert.Equal(new[] { "114795" }, _igdb.FetchedIds);                    // and its art is fetched by that id
        Assert.Equal(new IdentityKey(Cat.Igdb, "114795"), output.Artwork.Selection!.DerivedFrom);
    }

    [Fact]
    public void ThePrimaryTitleWins_TheAlternativeNamesAreNeverAsked()
    {
        _igdb.Title = _ => Found("7", "Foo");

        Run(Foo(), Cat.Ctx([_igdb]));

        Assert.Empty(_igdb.AlternativeSearches);
    }

    [Theory]
    [InlineData("Ambiguous")]
    [InlineData("Unavailable")]
    public void AnAmbiguousOrUnavailablePrimaryTitle_IsNotSecondGuessedByTheAlternativeNames(string primary)
    {
        _igdb.Title = _ => primary == "Ambiguous" ? CatalogSearchResult.Ambiguous() : CatalogSearchResult.Unavailable("down");
        _igdb.AlternativeName = _ => Found("1", "Somebody");

        var output = Run(Foo(), Cat.Ctx([_igdb]));

        Assert.Empty(_igdb.AlternativeSearches);
        Assert.Empty(output.NewRecord!.Resolved);
    }

    [Fact]
    public void TwoGamesSharingTheAlternativeName_AreAmbiguous_AndBlockEveryOtherProvidersGuess()
    {
        _igdb.AlternativeName = _ => CatalogSearchResult.Ambiguous("two games");
        _sgdb.Title = _ => Found("5", "Apex");

        var output = Run(Games.Manual("manual-apex", "Apex"), Cat.Ctx([_igdb, _sgdb]));

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Ambiguous, output.NewRecord.LastAttempt!.Outcome);
        Assert.Equal(0, _sgdb.TitleSearches);
    }

    [Fact]
    public void AnUnavailableAlternativeNameLookup_IsAnOutage_NeverANegativeThatStartsACooldown()
    {
        _igdb.AlternativeName = _ => CatalogSearchResult.Unavailable("down");

        var output = Run(Foo(), Cat.Ctx([_igdb]));

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Unavailable, output.NewRecord.LastAttempt!.Outcome);
    }

    [Fact]
    public void AnAlternativeNameMatch_IsHeldToTheSameContradictionsAsAnExactTitle()
    {
        var game = Games.Steam("1091500", "Cyberpunk");
        _igdb.AlternativeName = _ => Found("7", "Cyberpunk 2077");
        _igdb.Consistency = (_, _) => LauncherConsistency.Contradicted;
        var contradicted = Run(game, Cat.Ctx([_igdb]));
        Assert.Empty(contradicted.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Contradicted, contradicted.NewRecord.LastAttempt!.Outcome);

        var rejectedFirst = new GameIdentityRecord();
        rejectedFirst.Rejected.Add(new ProviderIdentity { Namespace = Cat.Igdb, Id = "7", Title = "Cyberpunk 2077", At = DateTime.UtcNow });
        _igdb.Consistency = (_, _) => LauncherConsistency.Unknown;
        var rejected = Run(game, Cat.Ctx([_igdb]), rejectedFirst);
        Assert.Empty(rejected.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Contradicted, rejected.NewRecord.LastAttempt!.Outcome);

        _igdb.Consistency = (_, _) => LauncherConsistency.Consistent;
        var corroborated = Run(game, Cat.Ctx([_igdb]));
        Assert.Equal(IdentityTier.TitleExactCorroborated, Assert.Single(corroborated.NewRecord!.Resolved).Tier);
    }

    [Fact]
    public void ACatalogWithNoAlternativeNameData_SimplyHasNone_SteamGridDbNeverGuessesOne()
    {
        ICatalogProvider sgdb = new SteamGridDbCatalog(new SteamGridDbCoverArtProvider("key"));

        Assert.Equal(CatalogStatus.NoMatch, sgdb.SearchByAlternativeName("Apex", CancellationToken.None).Status);
    }

    [Fact]
    public void AnAlternativeNameIdentity_TiesWithAnExactTitle_SoNamespacePriorityDecidesNotTheLiteralMatch()
    {
        var game = Foo();
        var record = new GameIdentityRecord();
        record.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Sgdb, "9", "Foo", IdentityTier.TitleExact));
        record.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Igdb, "7", "Foo", IdentityTier.AlternativeName));

        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), IdentitySelection.SelectActive(record, IdentityQuery.From(game)).Primary!.Key);
    }

    // ================================================================================================================
    // 4b. Verified launcher ids (D7): the catalog's own cross-reference proves launcher art still belongs
    // ================================================================================================================

    private async Task<(GameEntry Game, GameOverride Over)> SteamGameShowingLauncherArt()
    {
        var game = await _h.Add(Games.Steam("1091500", "Cyberpunk 2077"));
        _h.LauncherArt = (_, _) => (TestBitmaps.Distinct(90), false);
        _h.Igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false); // the catalog has no cover
        await _h.Scan(game);
        Assert.Equal(HeightA, IdentityHarness.Height(game));                       // the Steam CDN art
        Assert.Equal(ArtworkProvider.SteamCdn, _h.Over(game.Id)!.Artwork!.Provider);
        return (game, _h.Over(game.Id)!);
    }

    private Task<IdentityChangeOutcome> ConfirmIgdb(GameEntry game, GameOverride over) =>
        _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Igdb, "7", "Cyberpunk 2077", null, null), over.DecisionRevision, over.IdentityRevision);

    [Fact]
    public async Task WhenIgdbsOwnCrossReferenceProvesTheSteamId_ConfirmingKeepsTheSteamCoverAuthorized()
    {
        var (game, over) = await SteamGameShowingLauncherArt();
        _h.Igdb.Consistency = (_, launcher) => launcher.Namespace == IdentifierNamespace.SteamApp && launcher.Id == "1091500"
            ? LauncherConsistency.Consistent : LauncherConsistency.Unknown;

        Assert.Equal(IdentityChangeOutcome.Success, await ConfirmIgdb(game, over));

        var verified = Assert.Single(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds!);
        Assert.Equal((IdentifierNamespace.SteamApp, "1091500"), (verified.Namespace, verified.Id));
        Assert.Equal(HeightA, IdentityHarness.Height(game));                       // still the Steam CDN cover: not dropped
        Assert.Equal(ArtworkProvider.SteamCdn, _h.Over(game.Id)!.Artwork!.Provider);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Contradicted")]
    public async Task WithoutProof_ConfirmingStillDropsTheSteamCover_ExactlyAsBefore(string consistency)
    {
        var (game, over) = await SteamGameShowingLauncherArt();
        _h.Igdb.Consistency = (_, _) => Enum.Parse<LauncherConsistency>(consistency);

        Assert.Equal(IdentityChangeOutcome.Success, await ConfirmIgdb(game, over));

        Assert.Null(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds);
        Assert.Null(IdentityHarness.Height(game));                                 // dropped; the confirmed identity has no art either
        Assert.Null(_h.Over(game.Id)!.Artwork);
    }

    [Fact]
    public async Task ReConfirmingTheSameGame_WhenProofHasSinceAppeared_RecordsIt_ItIsNotANoChange()
    {
        var (game, over) = await SteamGameShowingLauncherArt();
        Assert.Equal(IdentityChangeOutcome.Success, await ConfirmIgdb(game, over));
        Assert.Null(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds);

        _h.Igdb.Consistency = (_, _) => LauncherConsistency.Consistent;
        over = _h.Over(game.Id)!;
        Assert.Equal(IdentityChangeOutcome.Success, await ConfirmIgdb(game, over));

        Assert.NotNull(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds);
    }

    [Fact]
    public async Task AConfirmationForAGameWithNoLauncherId_AsksNoOneForProof()
    {
        var game = await _h.Add(Foo());
        var asked = 0;
        _h.Igdb.Consistency = (_, _) => { asked++; return LauncherConsistency.Consistent; };
        var over = _h.Vm.EnsureOverrideForTest(game.Id);

        await _h.Vm.ConfirmIdentityAsync(game.Id, new CatalogCandidate(Cat.Igdb, "7", "Foo", null, null), over.DecisionRevision, over.IdentityRevision);

        Assert.Equal(0, asked);
        Assert.Null(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds);
    }

    // ================================================================================================================
    // Found by the physical run-through: a confirmed identity + a pinned cover leaves nothing for any catalog to do
    // ================================================================================================================

    [Fact]
    public void AConfirmedIdentityWithAPinnedCover_AsksNoCatalogAnything_AndLeavesTheRecordAlone()
    {
        var game = Foo();
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "42", "Foo Tactics") };
        prior.LastAttempt = new ResolutionAttempt
        {
            Outcome = LookupOutcome.Resolved, At = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Fingerprint = IdentityQuery.From(game).Fingerprint, ResolverVersion = IdentitySelection.ResolverVersion, Trigger = "Scan;p=IgdbGame,SteamGridDbGame",
        };
        var input = Cat.Input(game, prior, new ArtworkSelection { Provider = ArtworkProvider.UserLocalFile, IsUserSelected = true }, pinned: true);

        var output = AutomaticResolver.Run(input, Cat.Ctx([_igdb, _sgdb]), CancellationToken.None);

        foreach (var catalog in new[] { _igdb, _sgdb })
        {
            Assert.Equal(0, catalog.TitleSearches);         // in particular: no canonical-title search of the FALLBACK catalog
            Assert.Empty(catalog.AlternativeSearches);
            Assert.Empty(catalog.MappedIds);
            Assert.Empty(catalog.FetchedIds);
        }

        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);
        Assert.Empty(output.NewRecord!.ArtworkSources);
        Assert.Equal(prior.LastAttempt, output.NewRecord.LastAttempt);      // nothing was attempted, so nothing was restamped
    }

    [Fact]
    public void APinnedCoverWithNoConfirmedIdentity_StillResolvesIdentity_ButFetchesNoArt_S22()
    {
        _igdb.Title = _ => Found("7", "Foo");

        var output = AutomaticResolver.Run(Cat.Input(Foo(), null, new ArtworkSelection { IsUserSelected = true }, pinned: true), Cat.Ctx([_igdb, _sgdb]), CancellationToken.None);

        Assert.Equal("7", Assert.Single(output.NewRecord!.Resolved).Id);   // knowing the game still has value
        Assert.Empty(_igdb.FetchedIds);                                      // ... but nothing is downloaded for a cover that is not shown
    }
}
