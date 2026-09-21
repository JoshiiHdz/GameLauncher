using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>The automatic resolution unit's POLICY, through fake catalogs whose every call is counted: what it asks for,
/// what it records, and what it refuses to conclude. "Zero title searches" and "one canonical-title search" are claims
/// about CALLS, so they are asserted as calls.</summary>
public class AutomaticResolverTests : IDisposable
{
    private readonly FakeCatalog _igdb = new(Cat.Igdb, "IGDB");
    private readonly FakeCatalog _sgdb = new(Cat.Sgdb, "SteamGridDB");
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private ResolutionContext Ctx(params FakeCatalog[] providers) => Cat.Ctx(providers);

    private UnitOutput Run(GameEntry game, ResolutionContext ctx, GameIdentityRecord? prior = null, ArtworkSelection? current = null,
        bool pinned = false, string trigger = "Scan", bool force = false) =>
        AutomaticResolver.Run(Cat.Input(game, prior, current, pinned, trigger, force), ctx, CancellationToken.None);

    private static CatalogSearchResult Found(string id, string title) => CatalogSearchResult.Found(id, title);

    private static CatalogCoverResult NoArt() => new(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);
    private static CatalogCoverResult Outage() => new(CoverLookupStatus.Unavailable, null, false);

    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");

    // ---- The title path --------------------------------------------------------------------------------------------

    [Fact]
    public void AUniqueExactTitle_IsResolved_AndItsCoverIsFetchedById()
    {
        _igdb.Title = _ => Found("7", "Foo");

        var output = Run(Foo(), Ctx(_igdb, _sgdb));

        var resolved = Assert.Single(output.NewRecord!.Resolved);
        Assert.Equal((Cat.Igdb, "7", IdentityTier.TitleExact), (resolved.Namespace, resolved.Id, resolved.Tier));
        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), output.Artwork.Selection!.DerivedFrom);
        Assert.Equal(new[] { "7" }, _igdb.FetchedIds);
        Assert.Equal(0, _sgdb.TitleSearches); // IGDB had art: the fallback was never consulted
        Assert.Equal(LookupOutcome.Resolved, output.NewRecord.LastAttempt!.Outcome);
    }

    [Fact]
    public void TheProvider_IsSearchedByTheDetectedTitle_NeverByACustomName_S1()
    {
        var game = Foo();
        game.Name = "My Custom Name";
        _igdb.Title = _ => Found("7", "Foo");

        Run(game, Ctx(_igdb));

        Assert.Equal(new[] { "Foo" }, _igdb.SearchedTitles);
    }

    [Fact]
    public void TheCuratedHint_IsWhatIsSearched_ForAnAbbreviatedTitle()
    {
        _igdb.Title = t => t == "Apex Legends" ? Found("114795", "Apex Legends") : CatalogSearchResult.NoMatch();

        var output = Run(Games.Ea(), Ctx(_igdb));

        Assert.Equal(new[] { "Apex Legends" }, _igdb.SearchedTitles);
        Assert.Equal("114795", Assert.Single(output.NewRecord!.Resolved).Id);
    }

    [Fact]
    public void AnIdMapping_ResolvesWithoutAnyTitleSearch_AndIsTheStrongestTier()
    {
        _igdb.Map = l => l.Namespace == IdentifierNamespace.SteamApp ? Found("7", "Cyberpunk 2077") : CatalogSearchResult.NoMatch();

        var output = Run(Games.Steam(), Ctx(_igdb));

        Assert.Equal(0, _igdb.TitleSearches);
        Assert.Equal(IdentityTier.IdMapped, Assert.Single(output.NewRecord!.Resolved).Tier);
    }

    [Fact]
    public void AnIdMappingThatMisses_FallsThroughToTheTitlePath_AndAnIdPathOutageNeverBlocksIt()
    {
        _igdb.Map = _ => CatalogSearchResult.Unavailable("external_games query rejected");
        _igdb.Title = _ => Found("7", "Cyberpunk 2077");

        var output = Run(Games.Steam(), Ctx(_igdb));

        Assert.Equal(1, _igdb.TitleSearches);
        Assert.Equal("7", Assert.Single(output.NewRecord!.Resolved).Id);
    }

    [Fact]
    public void AMatchThatTheLaunchersOwnIdCorroborates_IsRecordedAsCorroborated()
    {
        _igdb.Title = _ => Found("7", "Cyberpunk 2077");
        _igdb.Consistency = (_, _) => LauncherConsistency.Consistent;

        var output = Run(Games.Steam(), Ctx(_igdb));

        Assert.Equal(IdentityTier.TitleExactCorroborated, Assert.Single(output.NewRecord!.Resolved).Tier);
    }

    [Fact]
    public void AUniqueExactTitle_ThatTheLaunchersOwnIdContradicts_IsNotAccepted_AndNothingIsSeededFromIt_S15()
    {
        _igdb.Title = _ => Found("7", "Cyberpunk 2077");
        _igdb.Consistency = (_, _) => LauncherConsistency.Contradicted;

        var output = Run(Games.Steam(), Ctx(_igdb, _sgdb));

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Contradicted, output.NewRecord.LastAttempt!.Outcome);
        Assert.Equal(0, _igdb.CoverFetches);
        Assert.Equal(new[] { "Cyberpunk 2077" }, _sgdb.SearchedTitles); // the fallback searches the DETECTED title, never the rejected candidate
    }

    [Fact]
    public void ARejectedCandidate_IsNeverAutoPicked_S16()
    {
        _igdb.Title = _ => Found("7", "Foo");
        var prior = new GameIdentityRecord();
        prior.Rejected.Add(Cat.Confirmed(Cat.Igdb, "7", "Foo"));

        var output = Run(Foo(), Ctx(_igdb), prior);

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(LookupOutcome.Contradicted, output.NewRecord.LastAttempt!.Outcome);
        Assert.Equal(0, _igdb.CoverFetches);
    }

    // ---- Ambiguity, no match, and outages ---------------------------------------------------------------------------

    [Fact]
    public void AnAmbiguousName_IsUnresolved_NoCoverIsGuessed_AndNoOtherProviderGuessesIt_S13()
    {
        _igdb.Title = _ => CatalogSearchResult.Ambiguous();
        _sgdb.Title = _ => Found("5", "Foo"); // SteamGridDB WOULD accept it - but must not be asked

        var output = Run(Foo(), Ctx(_igdb, _sgdb));

        Assert.Empty(output.NewRecord!.Resolved);
        Assert.Equal(0, _sgdb.TitleSearches);
        Assert.Equal(0, _igdb.CoverFetches + _sgdb.CoverFetches);
        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);
        Assert.Equal(LookupOutcome.Ambiguous, output.NewRecord.LastAttempt!.Outcome);
    }

    [Fact]
    public void AnAmbiguousLauncherHub_StillGetsItsOwnLauncherArt_BecauseThatNeverGuessesByName()
    {
        _igdb.Title = _ => CatalogSearchResult.Ambiguous();
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => (TestBitmaps.Cover(), true));

        var output = Run(Games.Steam("999", "Some Hub"), ctx);

        Assert.Equal(ArtworkProvider.SteamCdn, output.Artwork.Selection!.Provider);
    }

    [Fact]
    public void NoMatchAnywhere_ClearsAnAutomaticCover_ButOnlyBecauseItIsADefiniteNegative()
    {
        var current = Cat.Auto(Cat.Igdb, "7");

        var output = Run(Foo(), Ctx(_igdb, _sgdb), current: current);

        Assert.Equal(ArtworkHalfKind.ClearAutomatic, output.Artwork.Kind);
        Assert.Equal(LookupOutcome.NoMatch, output.NewRecord!.LastAttempt!.Outcome);
    }

    [Fact]
    public void AnOutage_NeverClearsAnything_I5()
    {
        _igdb.Title = _ => CatalogSearchResult.Unavailable("boom");
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("boom");
        var current = Cat.Auto(Cat.Igdb, "7");

        var output = Run(Foo(), Ctx(_igdb, _sgdb), current: current);

        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind); // not ClearAutomatic
        Assert.Equal(LookupOutcome.Unavailable, output.NewRecord!.LastAttempt!.Outcome);
    }

    [Fact]
    public void APriorFreshIdentity_SurvivesAnOutage_AndItsCoverComesFromTheIdKeyedCache_S14a()
    {
        var game = Foo();
        var query = IdentityQuery.From(game);
        var prior = new GameIdentityRecord();
        prior.Resolved.Add(Cat.Resolved(query, Cat.Igdb, "7", "Foo"));
        _igdb.Title = _ => throw new InvalidOperationException("must not search: the identity is fresh and sticky");
        _igdb.Cover = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Cover(), FromCache: true);

        var output = Run(game, Ctx(_igdb, _sgdb), prior);

        Assert.Equal("7", Assert.Single(output.NewRecord!.Resolved).Id);
        Assert.Equal(ArtworkRetrievalMethod.LocalCache, output.Artwork.Selection!.RetrievedFrom);
        Assert.Equal(0, _igdb.TitleSearches);
    }

    [Fact]
    public void WhenTheIdentityIsFreshButTheCoverIsUnavailable_NothingIsClearedOrDowngraded_S14a()
    {
        var game = Foo();
        var query = IdentityQuery.From(game);
        var prior = new GameIdentityRecord();
        prior.Resolved.Add(Cat.Resolved(query, Cat.Igdb, "7", "Foo"));
        _igdb.Cover = _ => Outage();
        var current = Cat.Auto(Cat.Igdb, "7");

        var output = Run(game, Ctx(_igdb), prior, current);

        Assert.Equal("7", Assert.Single(output.NewRecord!.Resolved).Id);
        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);
    }

    [Fact]
    public void PrimaryDownAndFallbackUp_UsesTheFallback_AndTheIdentityIsOnlyTheFallbacks_S14b()
    {
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        _sgdb.Title = _ => Found("5", "Foo");

        var output = Run(Foo(), Ctx(_igdb, _sgdb));

        var resolved = Assert.Single(output.NewRecord!.Resolved);
        Assert.Equal((Cat.Sgdb, "5"), (resolved.Namespace, resolved.Id));
        Assert.Equal(ArtworkProvider.SteamGridDb, output.Artwork.Selection!.Provider);
        Assert.Equal(LookupOutcome.Resolved, output.NewRecord.LastAttempt!.Outcome);
    }

    [Fact]
    public void AResolvedIdentity_IsSticky_ASecondRunSearchesNothing()
    {
        _igdb.Title = _ => Found("7", "Foo");
        var game = Foo();
        var first = Run(game, Ctx(_igdb));

        var second = Run(game, Ctx(_igdb), first.NewRecord);

        Assert.Equal(1, _igdb.TitleSearches);
        Assert.Equal(2, _igdb.CoverFetches); // by id, every time (a cache hit in production)
        Assert.Equal("7", Assert.Single(second.NewRecord!.Resolved).Id);
    }

    [Fact]
    public void AStaleIdentity_IsReResolved_WhenTheDetectedTitleChanges()
    {
        _igdb.Title = t => Found(t == "Foo" ? "7" : "8", t);
        var first = Run(Foo(), Ctx(_igdb));

        var renamedOnDisk = Games.Manual("manual-foo", "Foo 2");
        var second = Run(renamedOnDisk, Ctx(_igdb), first.NewRecord);

        Assert.Equal("8", Assert.Single(second.NewRecord!.Resolved).Id); // the old entry's fingerprint no longer matches
    }

    [Fact]
    public void TheBreaker_SkipsAProvider_AfterConsecutiveOutages_WithinOneScan()
    {
        _igdb.Title = _ => CatalogSearchResult.Unavailable("down");
        var ctx = Ctx(_igdb); // ONE context = one scan = one breaker

        for (var i = 0; i < 6; i++)
            Run(Games.Manual($"manual-{i}", $"Game {i}"), ctx);

        Assert.Equal(3, _igdb.TitleSearches); // three failures trip it; the rest of the scan does not hammer a dead provider
    }

    [Fact]
    public void ASuccess_ResetsTheBreaker()
    {
        var calls = 0;
        _igdb.Title = _ => ++calls % 3 == 0 ? Found("1", "x") : CatalogSearchResult.Unavailable("flaky");
        var ctx = Ctx(_igdb);

        for (var i = 0; i < 9; i++)
            Run(Games.Manual($"manual-{i}", $"Game {i}"), ctx);

        Assert.Equal(9, _igdb.TitleSearches); // never three IN A ROW
    }

    // ---- Negative-result cooldown -----------------------------------------------------------------------------------

    [Fact]
    public void ANegativeResult_IsNotRetriedWithinTheCooldown_ButIsAfterIt_AndReasonToRetryEndsIt()
    {
        var clock = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var game = Foo();
        ResolutionContext Ctx1(params FakeCatalog[] p) => Cat.Ctx(p, now: () => clock);

        var first = Run(game, Ctx1(_igdb));
        Assert.Equal(1, _igdb.TitleSearches);

        Run(game, Ctx1(_igdb), first.NewRecord);
        Assert.Equal(1, _igdb.TitleSearches);                       // within the cooldown: no new search

        clock += TimeSpan.FromHours(7);
        Run(game, Ctx1(_igdb), first.NewRecord);
        Assert.Equal(2, _igdb.TitleSearches);                       // after it: asked again

        clock = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Run(game, Ctx1(_igdb, _sgdb), first.NewRecord);
        Assert.Equal(3, _igdb.TitleSearches);                       // a newly configured provider ends it

        Run(game, Ctx1(_igdb), first.NewRecord, force: true, trigger: "Reset");
        Assert.Equal(4, _igdb.TitleSearches);                       // Reset bypasses it
    }

    // ---- A confirmed identity: never re-derived, provider-scoped ---------------------------------------------------------

    [Fact]
    public void AConfirmedIdentity_SuppliesItsCoverById_WithZeroTitleSearchesToAnyProvider_S5()
    {
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo Confirmed") };

        var output = Run(Foo(), Ctx(_igdb, _sgdb), prior, force: true, trigger: "Reset");

        Assert.Equal(0, _igdb.TitleSearches + _sgdb.TitleSearches);
        Assert.Equal(new[] { "7" }, _igdb.FetchedIds);
        Assert.Equal("UserConfirmed", output.Artwork.Selection!.MatchMethod);
        Assert.Equal(new IdentityKey(Cat.Igdb, "7"), output.Artwork.Selection.DerivedFrom);
        Assert.Empty(output.NewRecord!.Resolved);                 // the confirmed identity is not re-derived...
        Assert.Equal("7", output.NewRecord.Confirmed!.Id);        // ...and is kept exactly
    }

    [Fact]
    public void WhenTheConfirmedProviderHasNoArt_TheFallbackSearchesTheCanonicalTitleOnce_AndRecordsAnArtworkSource_S5b()
    {
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo Canonical") };
        _igdb.Cover = _ => NoArt();
        _sgdb.Title = _ => Found("5", "Foo Canonical");

        var output = Run(Foo(), Ctx(_igdb, _sgdb), prior);

        Assert.Equal(new[] { "Foo Canonical" }, _sgdb.SearchedTitles);                     // exactly one, by the CONFIRMED title
        var source = Assert.Single(output.NewRecord!.ArtworkSources);
        Assert.Equal((Cat.Sgdb, "5", new IdentityKey(Cat.Igdb, "7")), (source.Namespace, source.Id, source.Basis));
        Assert.Empty(output.NewRecord.Resolved);                                            // NOT an identity
        Assert.Equal(prior.Confirmed!.Key, IdentitySelection.SelectActive(output.NewRecord, IdentityQuery.From(Foo())).Primary!.Key);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "5"), output.Artwork.Selection!.DerivedFrom);
        Assert.Equal("ConfirmedTitle", output.Artwork.Selection.MatchMethod);
    }

    [Fact]
    public void AnArtworkSource_DoesNotChangeTheIdentityKey_S5b()
    {
        var game = Foo();
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo Canonical") };
        var keyBefore = IdentitySelection.SelectActive(prior, IdentityQuery.From(game)).Key;
        _igdb.Cover = _ => NoArt();
        _sgdb.Title = _ => Found("5", "Foo Canonical");

        var output = Run(game, Ctx(_igdb, _sgdb), prior);

        Assert.Equal(keyBefore, IdentitySelection.SelectActive(output.NewRecord, IdentityQuery.From(game)).Key);
    }

    [Fact]
    public void AFallbackNeverAcceptsARejectedCandidate_S44e()
    {
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo Canonical") };
        prior.Rejected.Add(Cat.Confirmed(Cat.Sgdb, "5", "Foo Canonical"));
        _igdb.Cover = _ => NoArt();
        _sgdb.Title = _ => Found("5", "Foo Canonical");

        var output = Run(Foo(), Ctx(_igdb, _sgdb), prior);

        Assert.Empty(output.NewRecord!.ArtworkSources);
        Assert.Equal(0, _sgdb.CoverFetches);
    }

    [Fact]
    public void AConfirmedCatalogsOwnArt_IsTriedFirst_SoAnAutomaticCompetitorsCoverIsNeverFetched_S33a()
    {
        // The user confirmed SteamGridDB game B; IGDB would auto-resolve A. The confirmed catalog supplies art FIRST, and
        // once it has, IGDB's cover is never even requested.
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        _igdb.Title = _ => Found("A", "Foo");

        var output = Run(Foo(), Ctx(_igdb, _sgdb), prior);

        Assert.Equal(new[] { "B" }, _sgdb.FetchedIds);
        Assert.Equal(0, _igdb.CoverFetches);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), output.Artwork.Selection!.DerivedFrom);
    }

    // ---- Pinned covers ----------------------------------------------------------------------------------------------

    [Fact]
    public void APinnedCover_StillGetsItsIdentityResolved_ButNoCoverIsFetched_S22()
    {
        _igdb.Title = _ => Found("7", "Foo");

        var output = Run(Foo(), Ctx(_igdb), pinned: true, current: new ArtworkSelection { IsUserSelected = true });

        Assert.Equal("7", Assert.Single(output.NewRecord!.Resolved).Id);
        Assert.Equal(0, _igdb.CoverFetches);
        Assert.Equal(ArtworkHalfKind.None, output.Artwork.Kind);
    }

    // ---- Launcher art (Steam CDN) -----------------------------------------------------------------------------------

    [Fact]
    public void LauncherArt_IsUsed_ForASteamGameWhenNoCatalogHasArt()
    {
        var calls = 0;
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => { calls++; return (TestBitmaps.Cover(), false); });

        var output = Run(Games.Steam("100", "Foo"), ctx);

        Assert.Equal(1, calls);
        var selection = output.Artwork.Selection!;
        Assert.Equal((ArtworkProvider.SteamCdn, new IdentityKey(IdentifierNamespace.SteamApp, "100")), (selection.Provider, selection.DerivedFrom));
    }

    [Fact]
    public void LauncherArt_IsNeverFetched_AfterAConfirmationThatDoesNotProveConsistency_D7()
    {
        var calls = 0;
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => { calls++; return (TestBitmaps.Cover(), false); });
        _igdb.Cover = _ => NoArt();
        var prior = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo") };

        var output = Run(Games.Steam("100", "Foo"), ctx, prior);

        Assert.Equal(0, calls);
        Assert.NotEqual(ArtworkHalfKind.Set, output.Artwork.Kind);
    }

    [Fact]
    public void LauncherArt_IsFetched_AfterAConfirmationTheProviderProvedConsistent_D7()
    {
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => (TestBitmaps.Cover(), false));
        _igdb.Cover = _ => NoArt();
        var prior = new GameIdentityRecord
        {
            Confirmed = Cat.Confirmed(Cat.Igdb, "7", "Foo", verified: [new LauncherIdentifier(IdentifierNamespace.SteamApp, "100")]),
        };

        var output = Run(Games.Steam("100", "Foo"), ctx, prior);

        Assert.Equal(ArtworkProvider.SteamCdn, output.Artwork.Selection!.Provider);
    }

    [Fact]
    public void ANonSteamGame_HasNoLauncherArt()
    {
        var calls = 0;
        var ctx = Cat.Ctx([_igdb], launcherArt: (_, _) => { calls++; return (TestBitmaps.Cover(), false); });

        Run(Foo(), ctx);

        Assert.Equal(0, calls);
    }

    // ---- Quarantine -------------------------------------------------------------------------------------------------

    private static GameIdentityRecord Quarantined(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };
    }

    [Fact]
    public void AQuarantinedRecord_IsNeverResolvedOrWritten_AndNoCatalogIsAsked()
    {
        var output = Run(Foo(), Ctx(_igdb, _sgdb), Quarantined("""{ "Junk": 1 }"""));

        Assert.True(output.IdentityNotApplicable);
        Assert.Equal(0, _igdb.TitleSearches + _sgdb.TitleSearches + _igdb.CoverFetches + _sgdb.CoverFetches);
    }

    [Fact]
    public void OnAQuarantinedRecord_OnlyLauncherArtMayPublish_AndOnlyIfTheRawShowsNoConfirmation_S35()
    {
        ResolutionContext WithArt() => Cat.Ctx([_igdb], launcherArt: (_, _) => (TestBitmaps.Cover(), false));

        Assert.Equal(ArtworkHalfKind.Set, Run(Games.Steam(), WithArt(), Quarantined("""{ "Junk": 1 }""")).Artwork.Kind);
        Assert.Equal(ArtworkHalfKind.None, Run(Games.Steam(), WithArt(), Quarantined("""{ "Confirmed": { "Namespace": 5 } }""")).Artwork.Kind);
        Assert.Equal(ArtworkHalfKind.None, Run(Foo(), WithArt(), Quarantined("""{ "Junk": 1 }""")).Artwork.Kind); // not a Steam game
    }

    // ---- Legacy revalidation (8.1, S25-S27, S50) ---------------------------------------------------------------------------

    private static GameIdentityRecord WithLegacy(string id, LegacyStatus? status = null)
    {
        var r = new GameIdentityRecord();
        r.LegacyEvidence.Add(new LegacyAssociation
        {
            Namespace = Cat.Sgdb, Id = id, Title = "Old Match", SourceProvider = ArtworkProvider.SteamGridDb,
            MigratedAt = DateTime.UtcNow, Status = status ?? LegacyStatus.Pending,
        });
        return r;
    }

    private static ArtworkSelection LegacyArtwork(string id) => new()
    {
        Provider = ArtworkProvider.SteamGridDb, ProviderGameId = id, IsUserSelected = false, DerivedFrom = null,
    };

    [Fact]
    public void ALegacyMatch_ThatTheCurrentInputsCorroborate_BecomesAFreshResolvedIdentity_S26()
    {
        _sgdb.Title = _ => Found("777", "Foo");

        var output = Run(Foo(), Ctx(_sgdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Empty(output.NewRecord!.LegacyEvidence);
        Assert.Equal("777", Assert.Single(output.NewRecord.Resolved).Id);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "777"), output.Artwork.Selection!.DerivedFrom); // the artwork gains DerivedFrom
    }

    [Fact]
    public void ALegacyMatch_ThatTheCurrentInputsContradict_IsRemoved_AndReplacedByTheFreshCandidate_S25()
    {
        _sgdb.Title = _ => Found("888", "Foo");

        var output = Run(Foo(), Ctx(_sgdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Empty(output.NewRecord!.LegacyEvidence);
        Assert.Equal("888", Assert.Single(output.NewRecord.Resolved).Id);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "888"), output.Artwork.Selection!.DerivedFrom);
    }

    [Theory]
    [InlineData("NoMatch")]
    [InlineData("Ambiguous")]
    public void ALegacyMatch_ThatNoLongerMatchesAnything_IsRemovedEvenWithNoReplacement_AndItsCoverIsCleared_S25(string outcome)
    {
        _sgdb.Title = _ => outcome == "Ambiguous" ? CatalogSearchResult.Ambiguous() : CatalogSearchResult.NoMatch();

        var output = Run(Foo(), Ctx(_sgdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Empty(output.NewRecord!.LegacyEvidence);
        Assert.Empty(output.NewRecord.Resolved);
        Assert.Equal(ArtworkHalfKind.ClearAutomatic, output.Artwork.Kind);
    }

    [Fact]
    public void ALegacyMatch_IsPreserved_WhenTheProviderIsUnavailable_S27()
    {
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        var output = Run(Foo(), Ctx(_sgdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Equal("777", Assert.Single(output.NewRecord!.LegacyEvidence).Id);
        Assert.Empty(output.NewRecord.Resolved);        // never sticky, never identity
        Assert.NotEqual(ArtworkHalfKind.ClearAutomatic, output.Artwork.Kind);
    }

    [Fact]
    public void ALegacyMatch_IsUntouched_WhenItsProviderIsNotConfigured()
    {
        var output = Run(Foo(), Ctx(_igdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Single(output.NewRecord!.LegacyEvidence);
    }

    [Fact]
    public void RevalidationIsBudgeted_AnExhaustedBudgetLeavesTheLegacyMatchAlone()
    {
        _sgdb.Title = _ => CatalogSearchResult.NoMatch();
        var ctx = new ResolutionContext { Providers = [_sgdb], Budget = new ResolutionBudget(0) };

        var output = Run(Foo(), ctx, WithLegacy("777"), LegacyArtwork("777"));

        Assert.Single(output.NewRecord!.LegacyEvidence);
    }

    [Fact]
    public void ALegacyMatch_IsNeverUsedForAnIdBasedFetch_UntilItIsRevalidated_I10()
    {
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        Run(Foo(), Ctx(_sgdb), WithLegacy("777"), LegacyArtwork("777"));

        Assert.Empty(_sgdb.FetchedIds); // legacy evidence never enables id-keyed fetching
    }

    // ---- Legacy continuity through the real discovery -----------------------------------------------------------------

    private string CacheWith(string gameId, string sidecarSearchedName, int sidecarId = 777, bool writeSidecar = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-LegacyCache-" + Guid.NewGuid());
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        var bytes = TestImages.Png(60, 90);
        var path = Path.Combine(dir, $"{gameId}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");
        File.WriteAllBytes(path, bytes);
        if (writeSidecar)
        {
            File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(new
            {
                Id = sidecarId, Title = "Old Match", SearchedName = sidecarSearchedName, ImageSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            }));
        }

        return dir;
    }

    [Fact]
    public void LegacyContinuity_ShowsTheValidatedCachedImage_WhileTheProviderIsUnavailable_S41a()
    {
        var game = Foo();
        var ctx = new ResolutionContext { Providers = [_sgdb], LegacyCacheRoot = CacheWith(game.Id, "Foo") };
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        var output = Run(game, ctx, WithLegacy("777"), LegacyArtwork("777"));

        Assert.Equal(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.True(output.Artwork.LegacyContinuity);
        Assert.Null(output.Artwork.Selection!.DerivedFrom); // still the continuity marker - it proves nothing
    }

    [Fact]
    public void LegacyContinuity_IsWithheld_WhenTheLookupUsedDifferentInputs_AndTheAssociationBecomesStaleLookup_S41d_S50()
    {
        var game = Foo();
        var ctx = new ResolutionContext { Providers = [_sgdb], LegacyCacheRoot = CacheWith(game.Id, "A Name It Was Searched Under") };
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        var output = Run(game, ctx, WithLegacy("777"), LegacyArtwork("777"));

        Assert.NotEqual(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Equal(LegacyStatus.StaleLookup, Assert.Single(output.NewRecord!.LegacyEvidence).Status);
        Assert.Empty(output.NewRecord.Rejected); // a name mismatch is NOT a verdict: no rejection is ever created
    }

    [Fact]
    public void ANoContinuityFile_MeansNoContinuity_AndNothingIsDeleted()
    {
        var game = Foo();
        var dir = CacheWith(game.Id, "Foo", writeSidecar: false);
        var ctx = new ResolutionContext { Providers = [_sgdb], LegacyCacheRoot = dir };
        _sgdb.Title = _ => CatalogSearchResult.Unavailable("down");

        var output = Run(game, ctx, WithLegacy("777"), LegacyArtwork("777"));

        Assert.NotEqual(ArtworkHalfKind.Set, output.Artwork.Kind);
        Assert.Single(Directory.GetFiles(dir)); // read-only: the legacy file is still there
    }
}
