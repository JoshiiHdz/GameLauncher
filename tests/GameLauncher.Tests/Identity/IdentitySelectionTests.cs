using GameLauncher.Models;
using GameLauncher.Services.Identity;

namespace GameLauncher.Tests.Identity;

/// <summary>SelectActive (the single source of truth for what is active) and the ONE artwork-authorization predicate -
/// pure logic, no I/O. Covers the design's acceptance cases S29, S33 (logic), S44, S45, S46, S49 and the state derivation.</summary>
public class IdentitySelectionTests
{
    private static readonly GameEntry Game = Games.Steam("100", "Foo");
    private static readonly IdentityQuery Query = IdentityQuery.From(Game);
    private static readonly LauncherIdentifier SteamId = new(IdentifierNamespace.SteamApp, "100");

    private static GameIdentityRecord Rec() => new();

    private static ActiveIdentity Active(GameIdentityRecord r) => IdentitySelection.SelectActive(r, Query);

    private static bool Authorized(ArtworkSelection art, GameIdentityRecord r, bool legacyValidated = false) =>
        ArtworkAuthorization.IsAuthorized(art, Active(r), Query, r, legacyValidated);

    // ---- SelectActive ---------------------------------------------------------------------------------------------

    [Fact]
    public void ARecordWithNothing_HasNoActiveIdentity()
    {
        Assert.True(IdentitySelection.SelectActive(null, Query).IsEmpty);
        Assert.True(Active(Rec()).IsEmpty);
        Assert.Null(Active(Rec()).Primary);
    }

    [Fact]
    public void AConfirmedIdentity_IsPrimary_AndOutranksAnAutomaticOne()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo", IdentityTier.IdMapped));
        r.Confirmed = Cat.Confirmed(Cat.Sgdb, "9", "Foo");

        var active = Active(r);

        Assert.Equal(("SteamGridDbGame", "9"), (active.Primary!.Namespace.Value, active.Primary.Id));
        Assert.Equal(EntryAuthority.User, active.Primary.Authority);
    }

    [Fact]
    public void AutomaticEntries_AreOrderedByTier_ThenNamespacePriority()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Sgdb, "2", "Foo", IdentityTier.IdMapped));
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo", IdentityTier.TitleExact));

        Assert.Equal("2", Active(r).Primary!.Id); // a stronger tier wins even in a lower-priority namespace

        r.Resolved.Clear();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Sgdb, "2", "Foo"));
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo"));

        Assert.Equal("1", Active(r).Primary!.Id); // equal tiers: IGDB outranks SteamGridDB
    }

    [Fact]
    public void AStaleFingerprint_ARejectedEntry_AndAnUntrustedVersion_AreAllFilteredOut()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo", fingerprint: "an-old-fingerprint"));
        r.Resolved.Add(Cat.Resolved(Query, Cat.Sgdb, "2", "Foo", version: IdentitySelection.MinTrustedResolverVersion - 1));
        r.Resolved.Add(Cat.Resolved(Query, IdentifierNamespace.Create("Other"), "3", "Foo"));
        r.Rejected.Add(Cat.Confirmed(IdentifierNamespace.Create("Other"), "3", "Foo"));

        Assert.True(Active(r).IsEmpty);
        Assert.Null(IdentitySelection.FreshResolved(r, Query, Cat.Igdb));
    }

    [Fact]
    public void LegacyEvidence_IsNeverConsulted()
    {
        var r = Rec();
        r.LegacyEvidence.Add(new LegacyAssociation { Namespace = Cat.Sgdb, Id = "5", Title = "Foo", SourceProvider = ArtworkProvider.SteamGridDb, Status = LegacyStatus.Pending });

        Assert.True(Active(r).IsEmpty);
    }

    [Fact]
    public void ArtworkSources_NeverEnterTheKey_NeverBecomePrimary_AndNeedTheirBasis()
    {
        var r = Rec();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        var keyWithout = Active(r).Key;
        r.ArtworkSources.Add(new ArtworkSourceAssociation { Namespace = Cat.Sgdb, Id = "5", Title = "Foo", ResolverVersion = 1, Basis = r.Confirmed.Key });

        var active = Active(r);

        Assert.Equal(keyWithout, active.Key); // an artwork source is not identity
        Assert.Equal("1", active.Primary!.Id);
        Assert.Contains(active.Entries, e => e.Role == EntryRole.ArtworkSource && e.Id == "5" && e.ArtworkEligible);
        Assert.NotEqual(Active(Rec()).AuthKey, active.AuthKey);

        r.ArtworkSources[0] = r.ArtworkSources[0] with { Basis = new IdentityKey(Cat.Igdb, "OTHER") };
        Assert.DoesNotContain(Active(r).Entries, e => e.Role == EntryRole.ArtworkSource); // basis no longer stands
    }

    [Fact]
    public void AnArtworkSource_ThatWasRejected_IsNotProduced_S44()
    {
        var r = Rec();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        r.ArtworkSources.Add(new ArtworkSourceAssociation { Namespace = Cat.Sgdb, Id = "5", Title = "Foo", ResolverVersion = 1, Basis = r.Confirmed.Key });
        r.Rejected.Add(Cat.Confirmed(Cat.Sgdb, "5", "Foo"));

        Assert.DoesNotContain(Active(r).Entries, e => e.Id == "5");
    }

    [Fact]
    public void AnArtworkSource_InTheConfirmedNamespace_WithAnotherId_IsNotProduced()
    {
        var r = Rec();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        r.ArtworkSources.Add(new ArtworkSourceAssociation { Namespace = Cat.Igdb, Id = "2", Title = "Foo", ResolverVersion = 1, Basis = r.Confirmed.Key });

        Assert.DoesNotContain(Active(r).Entries, e => e.Id == "2");
    }

    [Fact]
    public void SameNamespaceDifferentId_IsIneligible_RegardlessOfTitle_S45()
    {
        // Two catalog entries in ONE namespace are two products even with identical titles.
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "A", "Foo"));
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "B", "Foo");

        var a = Active(r).Entries.Single(e => e.Id == "A");

        Assert.False(a.ArtworkEligible);
        Assert.Equal("SameNamespaceDifferentId", a.IneligibleReason);
        Assert.Null(Active(r).IdFor(Cat.Igdb) is { Id: "A" } ? Active(r).IdFor(Cat.Igdb) : null); // IdFor never returns it
        Assert.Equal("B", Active(r).IdFor(Cat.Igdb)!.Id);
    }

    [Fact]
    public void ADifferentNamespaceThatDisagreesWithThePrimarysTitle_IsIneligible_S29()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo"));
        r.Resolved.Add(Cat.Resolved(Query, Cat.Sgdb, "2", "Completely Different"));

        var entries = Active(r).Entries;

        Assert.True(entries.Single(e => e.Id == "1").ArtworkEligible);
        Assert.False(entries.Single(e => e.Id == "2").ArtworkEligible);
        Assert.Null(Active(r).IdFor(Cat.Sgdb)); // never returned
    }

    [Fact]
    public void ADifferentNamespaceThatAgreesOnTheCanonicalTitle_IsEligible()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "A Way Out"));
        r.Resolved.Add(Cat.Resolved(Query, Cat.Sgdb, "2", "AWayOut"));

        Assert.All(Active(r).Entries, e => Assert.True(e.ArtworkEligible));
    }

    [Fact]
    public void AUserConfirmedSteamGridDbGame_OutranksAnAutomaticIgdbGame_AndTheIgdbOneLosesEligibility_S33()
    {
        // The audit's acceptance test at the logic level: eligibility is judged relative to the PRIMARY, and the primary
        // is the user's choice even though IGDB outranks SteamGridDB among AUTOMATIC entries.
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "A", "Foo"));
        r.Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar");

        var active = Active(r);

        Assert.Equal("B", active.Primary!.Id);
        Assert.False(active.Entries.Single(e => e.Id == "A").ArtworkEligible);
        Assert.Null(active.IdFor(Cat.Igdb));
    }

    // ---- The artwork-authorization predicate ----------------------------------------------------------------------

    [Fact]
    public void PinnedArtwork_IsNeverSubjectToTheGate()
    {
        var pinned = new ArtworkSelection { IsUserSelected = true, Provider = ArtworkProvider.UserLocalFile };
        var r = Rec();
        r.Rejected.Add(Cat.Confirmed(Cat.Igdb, "1", "x"));

        Assert.True(Authorized(pinned, r));
    }

    [Fact]
    public void CatalogArtwork_NeedsAnEligibleActiveEntryForItsExactIdentity()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo"));

        Assert.True(Authorized(Cat.Auto(Cat.Igdb, "1"), r));
        Assert.False(Authorized(Cat.Auto(Cat.Igdb, "2"), r));   // a different id
        Assert.False(Authorized(Cat.Auto(Cat.Sgdb, "1"), r));   // the same id in ANOTHER namespace is a different thing (I6)
        Assert.False(Authorized(Cat.Auto(Cat.Igdb, "1"), Rec())); // nothing active at all
    }

    [Fact]
    public void ArtworkForAnIneligibleEntry_IsNotAuthorized_EvenThoughItsIdIsPresent_S33()
    {
        var r = Rec();
        r.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "A", "Foo"));
        r.Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar");

        Assert.Contains(Active(r).Entries, e => e.Id == "A"); // present in Entries...
        Assert.False(Authorized(Cat.Auto(Cat.Igdb, "A"), r)); // ...but never authorized
        Assert.True(Authorized(Cat.Auto(Cat.Sgdb, "B"), r));
    }

    [Fact]
    public void ARejectedArtworkSource_IsNotAuthorized_EvenIfAStaleEntryIsStillInTheFile_S44d()
    {
        var r = Rec();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        // a hand-preserved ArtworkSources entry for C still present after C was rejected (the restart case)
        r.ArtworkSources.Add(new ArtworkSourceAssociation { Namespace = Cat.Sgdb, Id = "C", Title = "Foo", ResolverVersion = 1, Basis = r.Confirmed.Key });
        r.Rejected.Add(Cat.Confirmed(Cat.Sgdb, "C", "Foo"));

        Assert.False(Authorized(Cat.Auto(Cat.Sgdb, "C"), r));
    }

    [Fact]
    public void UserDecisionConsistent_IsTheSharedClause_RejectedAndSameNamespaceCompetitorsFail()
    {
        var r = Rec();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        r.Rejected.Add(Cat.Confirmed(Cat.Sgdb, "9", "x"));

        Assert.True(IdentitySelection.UserDecisionConsistent(Cat.Igdb, "1", r));
        Assert.False(IdentitySelection.UserDecisionConsistent(Cat.Igdb, "2", r));   // any OTHER id in the confirmed namespace
        Assert.False(IdentitySelection.UserDecisionConsistent(Cat.Sgdb, "9", r));   // rejected
        Assert.True(IdentitySelection.UserDecisionConsistent(Cat.Sgdb, "10", r));   // another namespace, not rejected
        Assert.True(IdentitySelection.UserDecisionConsistent(Cat.Igdb, "2", null));
    }

    // ---- Legacy continuity (S46) ---------------------------------------------------------------------------------

    private static ArtworkSelection LegacyArt(string id = "5") => new()
    {
        Provider = ArtworkProvider.SteamGridDb, ProviderGameId = id, IsUserSelected = false, DerivedFrom = null,
    };

    private static GameIdentityRecord WithLegacy(string id = "5", LegacyStatus? status = null)
    {
        var r = Rec();
        r.LegacyEvidence.Add(new LegacyAssociation { Namespace = Cat.Sgdb, Id = id, Title = "Foo", SourceProvider = ArtworkProvider.SteamGridDb, Status = status ?? LegacyStatus.Pending });
        return r;
    }

    [Fact]
    public void LegacyContinuity_IsAuthorized_OnlyWhenPending_Validated_AndNothingIsConfirmed()
    {
        Assert.True(Authorized(LegacyArt(), WithLegacy(), legacyValidated: true));
        Assert.False(Authorized(LegacyArt(), WithLegacy(), legacyValidated: false));                    // the file validation is required
        Assert.False(Authorized(LegacyArt(), WithLegacy(status: LegacyStatus.StaleLookup), true));      // withheld, not a verdict
        Assert.False(Authorized(LegacyArt("6"), WithLegacy("5"), true));                                // a different id
        Assert.False(Authorized(LegacyArt(), Rec(), true));                                             // no association at all
    }

    [Fact]
    public void LegacyContinuity_EndsTheMomentTheUserConfirms_S46()
    {
        var r = WithLegacy();
        r.Confirmed = Cat.Confirmed(Cat.Igdb, "77", "Other");

        Assert.False(Authorized(LegacyArt(), r, legacyValidated: true));
    }

    [Fact]
    public void LegacyContinuity_ForARejectedCandidate_IsNotAuthorized()
    {
        var r = WithLegacy();
        r.Rejected.Add(Cat.Confirmed(Cat.Sgdb, "5", "Foo"));

        Assert.False(Authorized(LegacyArt(), r, legacyValidated: true));
    }

    // ---- Launcher-derived artwork (D7, S49) -----------------------------------------------------------------------

    private static ArtworkSelection SteamArt(string appId = "100") => new()
    {
        Provider = ArtworkProvider.SteamCdn, ProviderGameId = $"steam-{appId}", IsUserSelected = false,
        DerivedFrom = new IdentityKey(IdentifierNamespace.SteamApp, appId),
    };

    [Fact]
    public void LauncherArt_IsAuthorized_WhenItsIdIsLive_AndNoIdentityIsConfirmed_S49c()
    {
        Assert.True(Authorized(SteamArt("100"), Rec()));
        Assert.False(Authorized(SteamArt("999"), Rec())); // the launcher does not carry that id
    }

    [Fact]
    public void LauncherArt_AfterAConfirmation_IsRemovedUnlessProvenConsistent_S49()
    {
        var unproven = Rec();
        unproven.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        var proven = Rec();
        proven.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo", verified: [SteamId]);

        Assert.False(Authorized(SteamArt(), unproven));  // (a) unproven: not authorized
        Assert.True(Authorized(SteamArt(), proven));     // (b) provider-proven: stays
    }

    // ---- Quarantine (S35, S47) ------------------------------------------------------------------------------------

    private static GameIdentityRecord Quarantined(string rawJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
        return new GameIdentityRecord { Quarantined = doc.RootElement.Clone() };
    }

    [Fact]
    public void ACatalogCover_IsNeverAuthorized_AgainstAQuarantinedRecord()
    {
        Assert.False(Authorized(Cat.Auto(Cat.Igdb, "1"), Quarantined("""{ "Junk": 1 }""")));
    }

    [Fact]
    public void LauncherArt_OnAQuarantinedRecord_NeedsALiveId_AndNoNonNullConfirmedInTheRaw()
    {
        Assert.True(Authorized(SteamArt(), Quarantined("""{ "Resolved": "garbage" }""")));            // no Confirmed at all
        Assert.True(Authorized(SteamArt(), Quarantined("""{ "Confirmed": null }""")));                // an explicit null
        Assert.False(Authorized(SteamArt(), Quarantined("""{ "Confirmed": { "Namespace": 5 } }""")));  // a non-null Confirmed
        Assert.False(Authorized(SteamArt(), Quarantined("42")));                                       // undeterminable: an opaque record may hold a decision
        Assert.False(Authorized(SteamArt("999"), Quarantined("""{ "Junk": 1 }""")));                  // not a live launcher id
    }

    [Fact]
    public void ASelectionOnAQuarantinedRecord_IsEmpty_AndTheStateIsQuarantined()
    {
        var r = Quarantined("""{ "Junk": 1 }""");

        Assert.True(Active(r).IsEmpty);
        Assert.Equal((IdentityState.Unresolved, "Quarantined"), (IdentitySelection.DeriveState(r, Query).State, IdentitySelection.DeriveState(r, Query).UnresolvedReason));
    }

    // ---- State derivation -----------------------------------------------------------------------------------------

    [Fact]
    public void TheFourStates_AreDerivedFromTheFacts()
    {
        var confirmed = Rec();
        confirmed.Confirmed = Cat.Confirmed(Cat.Igdb, "1", "Foo");
        var auto = Rec();
        auto.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo"));
        var noMatch = Rec();
        noMatch.LastAttempt = new ResolutionAttempt { Outcome = LookupOutcome.NoMatch };

        Assert.Equal(IdentityState.UserConfirmed, IdentitySelection.DeriveState(confirmed, Query).State);
        Assert.Equal(IdentityState.AutoResolved, IdentitySelection.DeriveState(auto, Query).State);
        Assert.Equal(("NoMatch", IdentityState.Unresolved), (IdentitySelection.DeriveState(noMatch, Query).UnresolvedReason!, IdentitySelection.DeriveState(noMatch, Query).State));
        Assert.Equal(IdentityState.Detected, IdentitySelection.DeriveState(Rec(), Query).State); // a launcher id, nothing attempted yet
        Assert.Equal("NeverAttempted", IdentitySelection.DeriveState(null, IdentityQuery.From(Games.Manual())).UnresolvedReason);
    }

    [Fact]
    public void AResolvedEntryThatWentStaleOrRejected_CanNeverMakeAGameAutoResolved()
    {
        var stale = Rec();
        stale.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo", fingerprint: "old"));
        var rejected = Rec();
        rejected.Resolved.Add(Cat.Resolved(Query, Cat.Igdb, "1", "Foo"));
        rejected.Rejected.Add(Cat.Confirmed(Cat.Igdb, "1", "Foo"));

        Assert.NotEqual(IdentityState.AutoResolved, IdentitySelection.DeriveState(stale, Query).State);
        Assert.NotEqual(IdentityState.AutoResolved, IdentitySelection.DeriveState(rejected, Query).State);
    }
}
