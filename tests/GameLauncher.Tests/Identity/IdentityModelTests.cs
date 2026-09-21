using System.IO;
using System.Reflection;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.Identity;

namespace GameLauncher.Tests.Identity;

/// <summary>The identity data model: DetectedTitle and the query/fingerprint (a display name can never reach a provider,
/// I7), tolerant loading and quarantine (S20), and migration from v1.18.x settings (S18). Every settings test uses its own
/// temp directory and SettingsService - the real AppData is never touched.</summary>
public class IdentityModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdModel-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private AppSettings LoadFrom(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return new SettingsService(_dir).Load();
    }

    private string SaveAndRead(AppSettings settings)
    {
        Assert.True(new SettingsService(_dir).Save(settings));
        return File.ReadAllText(Path.Combine(_dir, "settings.json"));
    }

    // ---- DetectedTitle, the query and its fingerprint (I7, S1) ---------------------------------------------------

    [Fact]
    public void AGameEntry_KeepsItsDetectedTitle_WhateverItsDisplayNameBecomes()
    {
        var game = Games.Steam(title: "Cyberpunk 2077");

        game.Name = "My Favourite RPG"; // a custom name overlaid later

        Assert.Equal("Cyberpunk 2077", game.DetectedTitle);
        Assert.Equal("My Favourite RPG", game.Name);
    }

    [Fact]
    public void TheQuery_IsBuiltFromTheDetectedTitle_NeverFromTheDisplayName()
    {
        var game = Games.Steam(title: "Cyberpunk 2077");
        game.Name = "Renamed";

        var query = IdentityQuery.From(game);

        Assert.Equal("Cyberpunk 2077", query.DetectedTitle);
        Assert.Equal("Cyberpunk 2077", query.SearchTitle);
    }

    [Fact]
    public void ARename_NeverChangesTheFingerprint()
    {
        var game = Games.Steam();
        var before = IdentityQuery.From(game).Fingerprint;

        game.Name = "Something Else Entirely";

        Assert.Equal(before, IdentityQuery.From(game).Fingerprint);
    }

    [Fact]
    public void TheFingerprint_ChangesWithTheDetectedInputs()
    {
        var baseline = IdentityQuery.From(Games.Steam("1", "Game")).Fingerprint;

        Assert.NotEqual(baseline, IdentityQuery.From(Games.Steam("1", "Other Game")).Fingerprint);                 // title
        Assert.NotEqual(baseline, IdentityQuery.From(Games.Steam("2", "Game")).Fingerprint);                       // launcher id
        Assert.NotEqual(baseline, new IdentityQuery("Game", GameSource.Manual, curatedHint: "Game Deluxe").Fingerprint); // hint / source
    }

    [Fact]
    public void TheFingerprint_IgnoresPunctuationAndCase_LikeTheMatcher()
    {
        Assert.Equal(new IdentityQuery("A Way Out", GameSource.Ea).Fingerprint, new IdentityQuery("AWAYOUT", GameSource.Ea).Fingerprint);
    }

    [Fact]
    public void TheSearchTitle_IsTheCuratedHintWhenThereIsOne()
    {
        Assert.Equal("Apex Legends", IdentityQuery.From(Games.Ea()).SearchTitle);
        Assert.Equal("Apex", IdentityQuery.From(Games.Ea(catalogName: null)).SearchTitle);
    }

    [Fact]
    public void AnIdentityQuery_HasNoDisplayNameMember_AndNoCatalogMethodTakesAGameEntry()
    {
        // I7 enforced by TYPES: a provider cannot read a custom name because there is nowhere to put one.
        var members = typeof(IdentityQuery).GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToList();
        Assert.DoesNotContain("Name", members);
        Assert.DoesNotContain("CustomName", members);
        Assert.DoesNotContain("DisplayName", members);
        Assert.DoesNotContain(typeof(IdentityQuery).GetProperties(), p => p.PropertyType == typeof(GameEntry));

        foreach (var method in typeof(ICatalogProvider).GetMethods())
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(GameEntry));
    }

    [Theory]
    [InlineData(GameSource.Steam, "steam-1091500", "SteamApp", "1091500")]
    [InlineData(GameSource.Gog, "gog-1207658924", "GogProduct", "1207658924")]
    [InlineData(GameSource.Epic, "epic-Sugar", "EpicApp", "Sugar")]
    [InlineData(GameSource.Ubisoft, "ubisoft-12", "UbisoftGame", "12")]
    public void LauncherIds_AreTakenFromTheScannersOwnIds(GameSource source, string gameId, string expectedNamespace, string expectedId)
    {
        var game = new GameEntry { Id = gameId, Name = "X", ExecutablePath = "x", InstallDir = "x", Source = source };

        var id = Assert.Single(IdentityQuery.From(game).LauncherIds);

        Assert.Equal(expectedNamespace, id.Namespace.Value);
        Assert.Equal(expectedId, id.Id);
    }

    [Fact]
    public void ManualAndUnkeyedSources_CarryNoLauncherIds()
    {
        Assert.Empty(IdentityQuery.From(Games.Manual()).LauncherIds);
        Assert.Empty(IdentityQuery.From(new GameEntry { Id = "steam-", Name = "X", ExecutablePath = "x", InstallDir = "x", Source = GameSource.Steam }).LauncherIds);
    }

    // ---- Round trip and forward compatibility ----------------------------------------------------------------------

    private const string Known = """
        {
          "WatchedFolders": [],
          "Overrides": {
            "g1": { "Favorite": true, "Identity": { "Confirmed": { "Namespace": "IgdbGame", "Id": "7", "Title": "Game", "At": "2026-01-01T00:00:00Z" },
                     "Rejected": [ { "Namespace": "SteamGridDbGame", "Id": "9", "Title": "Other", "At": "2026-01-01T00:00:00Z" } ] },
                    "IdentityRevision": 3, "DecisionRevision": 5 }
          }
        }
        """;

    [Fact]
    public void AnIdentityRecord_RoundTrips_WithItsRevisions()
    {
        var settings = LoadFrom(Known);
        var over = settings.Overrides["g1"];

        Assert.Equal("7", over.Identity!.Confirmed!.Id);
        Assert.Equal(IdentifierNamespace.IgdbGame, over.Identity.Confirmed.Namespace);
        Assert.Equal(3, over.IdentityRevision);
        Assert.Equal(5, over.DecisionRevision);

        var reloaded = LoadFrom(SaveAndRead(settings)).Overrides["g1"];
        Assert.Equal("7", reloaded.Identity!.Confirmed!.Id);
        Assert.Equal("9", Assert.Single(reloaded.Identity.Rejected).Id);
        Assert.Equal(3, reloaded.IdentityRevision);
        Assert.Equal(5, reloaded.DecisionRevision);
    }

    [Fact]
    public void AnUnknownNamespace_IsAValidRoundTrippableValue_NotAFailure()
    {
        // The whole reason for open string types: a namespace this version has never heard of must not break loading.
        var json = Known.Replace("\"Namespace\": \"IgdbGame\"", "\"Namespace\": \"FutureCatalog\"");

        var settings = LoadFrom(json);

        Assert.False(settings.Overrides["g1"].Identity!.IsQuarantined);
        Assert.Equal("FutureCatalog", settings.Overrides["g1"].Identity!.Confirmed!.Namespace.Value);
        Assert.Contains("FutureCatalog", SaveAndRead(settings));
    }

    [Fact]
    public void UnknownProperties_OfEveryLevel_SurviveALoadSaveCycle()
    {
        var json = """
            { "SomeFutureSetting": {"a": 1}, "WatchedFolders": [],
              "Overrides": { "g1": { "Favorite": true, "FutureOverrideField": [1,2], "Identity": { "FutureIdentityField": "x" } } } }
            """;

        var saved = SaveAndRead(LoadFrom(json));

        Assert.Contains("SomeFutureSetting", saved);
        Assert.Contains("FutureOverrideField", saved);
        Assert.Contains("FutureIdentityField", saved);
    }

    [Fact]
    public void ANullIdentity_LoadsAsNoIdentity()
    {
        var settings = LoadFrom("""{ "Overrides": { "g1": { "Favorite": true, "Identity": null } } }""");

        Assert.Null(settings.Overrides["g1"].Identity);
        Assert.True(settings.Overrides["g1"].Favorite);
    }

    // ---- S20: nothing an identity value contains may discard unrelated user data -----------------------------------

    public static IEnumerable<object[]> UnreadableIdentities()
    {
        yield return new object[] { "a string", "\"not an object\"" };
        yield return new object[] { "a number", "42" };
        yield return new object[] { "an array", "[1, 2, 3]" };
        yield return new object[] { "a boolean", "true" };
        yield return new object[] { "a namespace of the wrong type", """{ "Confirmed": { "Namespace": 5, "Id": "1", "Title": "t", "At": "2026-01-01T00:00:00Z" } }""" };
        yield return new object[] { "a blank id", """{ "Confirmed": { "Namespace": "IgdbGame", "Id": "", "Title": "t", "At": "2026-01-01T00:00:00Z" } }""" };
        yield return new object[] { "a missing id", """{ "Rejected": [ { "Namespace": "IgdbGame", "Title": "t", "At": "2026-01-01T00:00:00Z" } ] }""" };
        yield return new object[] { "an unparseable date", """{ "Confirmed": { "Namespace": "IgdbGame", "Id": "1", "Title": "t", "At": "yesterday-ish" } }""" };
        yield return new object[] { "a list of the wrong type", """{ "Resolved": "nope" }""" };
        yield return new object[] { "a null entry in a list", """{ "Rejected": [ null ] }""" };
    }

    [Theory]
    [MemberData(nameof(UnreadableIdentities))]
    public void AnUnreadableIdentity_IsQuarantined_AndEverythingElseSurvives(string why, string identityJson)
    {
        var json = $$"""
            { "WatchedFolders": [ { "Path": "C:\\Games" } ], "SteamGridDbApiKey": "key",
              "Overrides": { "bad": { "Favorite": true, "CustomName": "Kept", "Identity": {{identityJson}} },
                             "good": { "Favorite": true, "Hidden": true, "Identity": { "Confirmed": { "Namespace": "IgdbGame", "Id": "1", "Title": "t", "At": "2026-01-01T00:00:00Z" } } } } }
            """;

        var settings = LoadFrom(json);

        Assert.True(settings.Overrides["bad"].Identity!.IsQuarantined, why);
        Assert.True(settings.Overrides["bad"].Favorite, why);
        Assert.Equal("Kept", settings.Overrides["bad"].CustomName);
        Assert.Equal("key", settings.SteamGridDbApiKey);
        Assert.Single(settings.WatchedFolders);
        Assert.Equal("1", settings.Overrides["good"].Identity!.Confirmed!.Id); // another game's identity is intact
        Assert.True(settings.Overrides["good"].Hidden);
    }

    [Theory]
    [MemberData(nameof(UnreadableIdentities))]
    public void AQuarantinedSubtree_IsWrittenBackVerbatim(string why, string identityJson)
    {
        var json = $$"""{ "Overrides": { "bad": { "Favorite": true, "Identity": {{identityJson}} } } }""";

        var saved = SaveAndRead(LoadFrom(json));

        using var doc = JsonDocument.Parse(saved);
        using var original = JsonDocument.Parse(identityJson);
        var written = doc.RootElement.GetProperty("Overrides").GetProperty("bad").GetProperty("Identity");
        Assert.Equal(JsonSerializer.Serialize(original.RootElement), JsonSerializer.Serialize(written));
        Assert.NotNull(why);
    }

    [Fact]
    public void AQuarantinedSubtree_StillRoundTrips_AfterASecondLoadSave()
    {
        var json = """{ "Overrides": { "bad": { "Identity": { "Confirmed": { "Namespace": 5 }, "Extra": [1] } } } }""";

        var second = SaveAndRead(LoadFrom(SaveAndRead(LoadFrom(json))));

        static string IdentityOf(string document)
        {
            using var doc = JsonDocument.Parse(document);
            return JsonSerializer.Serialize(doc.RootElement.GetProperty("Overrides").GetProperty("bad").GetProperty("Identity"));
        }

        Assert.Equal(IdentityOf(json), IdentityOf(second)); // the subtree, structurally unchanged after two cycles
    }

    [Fact]
    public void ABadIdentityConflictElement_IsKeptVerbatim_AndGoodOnesStillParse()
    {
        var json = """
            { "IdentityConflicts": [
                { "Kind": "SameNamespaceDifferentId", "WinnerGameId": "w", "LoserGameId": "l", "DetectedAt": "2026-01-01T00:00:00Z" },
                "just a string",
                { "Kind": 12, "WinnerGameId": [] } ],
              "Overrides": { "g": { "Favorite": true } } }
            """;

        var settings = LoadFrom(json);

        Assert.Equal(3, settings.IdentityConflicts.Count);
        Assert.Equal("w", settings.IdentityConflicts[0].WinnerGameId);
        Assert.NotNull(settings.IdentityConflicts[1].Raw);
        Assert.NotNull(settings.IdentityConflicts[2].Raw);
        Assert.True(settings.Overrides["g"].Favorite);

        using var saved = JsonDocument.Parse(SaveAndRead(settings));
        var conflicts = saved.RootElement.GetProperty("IdentityConflicts");
        Assert.Equal(3, conflicts.GetArrayLength());
        Assert.Equal("just a string", conflicts[1].GetString());
    }

    [Fact]
    public void ARevisionOfTheWrongType_LoadsAsZero_AndAnOutOfRangeOneIsClamped()
    {
        var settings = LoadFrom("""
            { "Overrides": { "a": { "IdentityRevision": "seven", "DecisionRevision": { "x": 1 } },
                             "b": { "IdentityRevision": -4, "DecisionRevision": 9223372036854775807 },
                             "c": { "IdentityRevision": 12, "DecisionRevision": 30 } } }
            """);

        Assert.Equal((0, 0), (settings.Overrides["a"].IdentityRevision, settings.Overrides["a"].DecisionRevision));
        Assert.Equal((0, 0), (settings.Overrides["b"].IdentityRevision, settings.Overrides["b"].DecisionRevision));
        Assert.Equal((12, 30), (settings.Overrides["c"].IdentityRevision, settings.Overrides["c"].DecisionRevision));
    }

    [Fact]
    public void ADerivedFromThatCannotBeRead_LoadsAsNull_NotAsAFailure()
    {
        var settings = LoadFrom("""
            { "Overrides": { "g": { "Artwork": { "Provider": 3, "IsUserSelected": false, "ProviderGameId": "5", "DerivedFrom": "garbage" }, "Favorite": true } } }
            """);

        Assert.Null(settings.Overrides["g"].Artwork!.DerivedFrom);
        Assert.True(settings.Overrides["g"].Favorite);
    }

    // ---- S18: migration from v1.18.x ------------------------------------------------------------------------------

    private const string V1181 = """
        {
          "WatchedFolders": [],
          "Overrides": {
            "pinned": { "Favorite": true, "ArtworkRevision": 4,
              "Artwork": { "Provider": 0, "RetrievedFrom": 0, "AssetId": "abc123", "AssetExtension": "png", "MatchMethod": "UserLocalFile",
                           "IsUserSelected": true, "SelectedAt": "2026-01-01T00:00:00Z" } },
            "sgdb-auto": { "ArtworkRevision": 2,
              "Artwork": { "Provider": 1, "RetrievedFrom": 1, "ProviderGameId": "777", "ProviderTitle": "Old Match", "MatchMethod": "ExactTitle",
                           "IsUserSelected": false, "SelectedAt": "2026-01-01T00:00:00Z" } },
            "steam-42": { "Artwork": { "Provider": 2, "RetrievedFrom": 2, "ProviderGameId": "steam-42", "MatchMethod": "SteamAppId",
                           "IsUserSelected": false, "SelectedAt": "2026-01-01T00:00:00Z" } },
            "igdb-local": { "Artwork": { "Provider": 3, "RetrievedFrom": 1, "ProviderGameId": "555", "MatchMethod": "ExactTitle",
                           "IsUserSelected": false, "SelectedAt": "2026-01-01T00:00:00Z" } }
          },
          "ArtworkConflicts": [ { "WinnerGameId": "w", "LoserGameId": "l", "DetectedAt": "2026-01-01T00:00:00Z",
                                   "LoserSelection": { "Provider": 0, "AssetId": "zzz", "AssetExtension": "png", "IsUserSelected": true } } ]
        }
        """;

    [Fact]
    public void Migration_LeavesUserSelectedCovers_ByteForByteUntouched()
    {
        var pinned = LoadFrom(V1181).Overrides["pinned"];

        Assert.True(pinned.Artwork!.IsUserSelected);
        Assert.Equal(("abc123", "png", 4), (pinned.Artwork.AssetId, pinned.Artwork.AssetExtension, (int)pinned.ArtworkRevision));
        Assert.Null(pinned.Artwork.DerivedFrom);
        Assert.Null(pinned.Identity); // a user cover implies NO identity
    }

    [Fact]
    public void Migration_TurnsAnAutomaticSteamGridDbMatch_IntoUnverifiedLegacyEvidence_NeverAResolvedIdentity()
    {
        var over = LoadFrom(V1181).Overrides["sgdb-auto"];

        var legacy = Assert.Single(over.Identity!.LegacyEvidence);
        Assert.Equal((IdentifierNamespace.SteamGridDbGame, "777", LegacyStatus.Pending), (legacy.Namespace, legacy.Id, legacy.Status));
        Assert.Empty(over.Identity.Resolved);
        Assert.Null(over.Identity.Confirmed);
        Assert.Null(over.Artwork!.DerivedFrom); // the continuity marker
    }

    [Fact]
    public void Migration_StateIsUnresolvedPendingRevalidation_NeverAnIdentity()
    {
        var over = LoadFrom(V1181).Overrides["sgdb-auto"];
        var query = IdentityQuery.From(Games.Manual("sgdb-auto"));

        var active = IdentitySelection.SelectActive(over.Identity, query);
        var state = IdentitySelection.DeriveState(over.Identity, query, active);

        Assert.True(active.IsEmpty); // legacy evidence is never consulted (I10)
        Assert.Equal((IdentityState.Unresolved, "PendingRevalidation"), (state.State, state.UnresolvedReason));
    }

    [Fact]
    public void Migration_SteamCdnArtwork_GainsItsLauncherIdAsDerivedFrom_AndNoLegacyAssociation()
    {
        var over = LoadFrom(V1181).Overrides["steam-42"];

        Assert.Equal(new IdentityKey(IdentifierNamespace.SteamApp, "42"), over.Artwork!.DerivedFrom);
        Assert.Null(over.Identity); // the launcher's own game is not catalog evidence (S42)
    }

    [Fact]
    public void Migration_DoesNotMigrateTheDeveloperLocalIgdbCache()
    {
        var over = LoadFrom(V1181).Overrides["igdb-local"];

        Assert.Null(over.Identity);
        Assert.Null(over.Artwork!.DerivedFrom);
    }

    [Fact]
    public void Migration_PreservesArtworkConflicts()
    {
        var settings = LoadFrom(V1181);

        Assert.Equal("zzz", Assert.Single(settings.ArtworkConflicts).LoserSelection.AssetId);
        Assert.Empty(settings.IdentityConflicts);
    }

    [Fact]
    public void Migration_IsIdempotent_ASecondLoadChangesNothing()
    {
        var first = LoadFrom(V1181);
        var savedOnce = SaveAndRead(first);

        var second = LoadFrom(savedOnce);
        var savedTwice = SaveAndRead(second);

        JsonAssert.SameStructure(savedOnce, savedTwice);
        Assert.False(IdentityMigration.Apply(second)); // a third application finds nothing left to do
        Assert.Single(second.Overrides["sgdb-auto"].Identity!.LegacyEvidence);
    }

    [Fact]
    public void Migration_NeverTouchesAQuarantinedRecord()
    {
        var settings = LoadFrom("""
            { "Overrides": { "g": { "Artwork": { "Provider": 1, "ProviderGameId": "5", "IsUserSelected": false }, "Identity": 42 } } }
            """);

        Assert.True(settings.Overrides["g"].Identity!.IsQuarantined);
        Assert.Empty(settings.Overrides["g"].Identity!.LegacyEvidence);
    }
}
