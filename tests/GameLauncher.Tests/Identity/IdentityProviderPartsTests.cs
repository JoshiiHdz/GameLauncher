using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>The pure/parsing and cache-file halves of the identity pipeline that no scan-level test can pin one by one:
/// response readers, the id-keyed cache, legacy-cache discovery, and Steam launcher art. Synthetic fixtures only.</summary>
public class IdentityProviderPartsTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdParts-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    // ---- IGDB external_games (documented schema: external_game_source, uid, game) ------------------------------------------

    [Fact]
    public void ExternalGames_ReturnsTheGameWhoseStoreIdEchoesTheRequest()
    {
        var ids = IgdbCoverArtProvider.ParseExternalGames("""[{"game":7,"uid":"1091500","external_game_source":1}]""", 1, "1091500");

        Assert.Equal(new[] { 7 }, ids);
    }

    [Fact]
    public void ExternalGames_IgnoresAnEntryThatProvablyDescribesADifferentMapping()
    {
        var ids = IgdbCoverArtProvider.ParseExternalGames(
            """[{"game":7,"uid":"1091500","external_game_source":1},{"game":8,"uid":"999","external_game_source":1},{"game":9,"uid":"1091500","external_game_source":5}]""", 1, "1091500");

        Assert.Equal(new[] { 7 }, ids);
    }

    [Fact]
    public void ExternalGames_TheSameGameListedTwice_IsOneGame_NotAmbiguity()
    {
        var ids = IgdbCoverArtProvider.ParseExternalGames(
            """[{"game":7,"uid":"1","external_game_source":1},{"game":7,"uid":"1","external_game_source":1}]""", 1, "1");

        Assert.Single(ids);
    }

    [Theory]
    [InlineData("""{"game":7}""")]                                                            // not an array
    [InlineData("""[7]""")]                                                                    // entry not an object
    [InlineData("""[{"game":7,"external_game_source":1}]""")]                                  // no uid
    [InlineData("""[{"game":7,"uid":1,"external_game_source":1}]""")]                          // uid not a string
    [InlineData("""[{"game":7,"uid":"1"}]""")]                                                 // no source
    [InlineData("""[{"game":7,"uid":"1","category":1}]""")]                                    // the DEPRECATED field is not a substitute
    [InlineData("""[{"game":7,"uid":"1","external_game_source":{"id":1}}]""")]                 // an expanded reference is not what was asked for
    [InlineData("""[{"uid":"1","external_game_source":1}]""")]                                 // echoes the request but names no game
    [InlineData("""[{"game":0,"uid":"1","external_game_source":1}]""")]                        // ... or a non-positive one
    [InlineData("""[{"game":"7","uid":"1","external_game_source":1}]""")]                      // ... or a non-numeric one
    public void ExternalGames_AnythingUnreadableThatCouldBeTheMapping_FailsClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.ParseExternalGames(json, 1, "1"));
    }

    [Fact]
    public void OnlySteamStoreIdsAreMappedThroughIgdb_GogAndEpicAreNot_U1()
    {
        Assert.Equal("Steam", IgdbCoverArtProvider.ExternalSourceNameFor(IdentifierNamespace.SteamApp));
        Assert.Null(IgdbCoverArtProvider.ExternalSourceNameFor(IdentifierNamespace.GogProduct));
        Assert.Null(IgdbCoverArtProvider.ExternalSourceNameFor(IdentifierNamespace.EpicApp));
        Assert.Null(IgdbCoverArtProvider.ExternalSourceNameFor(IdentifierNamespace.UbisoftGame));
    }

    [Fact]
    public void ExternalSource_IsFoundByItsDocumentedName_NotByANumberWeAssumed()
    {
        Assert.Equal(1, IgdbCoverArtProvider.ParseExternalSourceId("""[{"id":1,"name":"Steam"},{"id":5,"name":"GOG"}]""", "Steam"));
        Assert.Equal(26, IgdbCoverArtProvider.ParseExternalSourceId("""[{"id":26,"name":"steam"}]""", "Steam")); // case-insensitive; the id is whatever IGDB says
        Assert.Null(IgdbCoverArtProvider.ParseExternalSourceId("""[{"id":5,"name":"GOG"}]""", "Steam"));
        Assert.Null(IgdbCoverArtProvider.ParseExternalSourceId("[]", "Steam"));
    }

    [Theory]
    [InlineData("""{"id":1}""")]
    [InlineData("""[{"id":1}]""")]                                   // no name
    [InlineData("""[{"name":"Steam"}]""")]                           // no id
    [InlineData("""[{"id":0,"name":"Steam"}]""")]
    [InlineData("""[{"id":1,"name":"Steam"},{"id":2,"name":"Steam"}]""")] // two different ids for one name: no mapping can be trusted through it
    public void ExternalSource_AnUnreadableOrContradictoryListing_FailsClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.ParseExternalSourceId(json, "Steam"));
    }

    // ---- IGDB alternative names (design 4.5) ----------------------------------------------------------------------------------

    [Fact]
    public void AlternativeNames_OneGameHavingTheName_IsTheAnswer_EvenListedTwice()
    {
        var games = IgdbCoverArtProvider.ParseAlternativeNameGames(
            """[{"game":114795,"name":"Apex"},{"game":114795,"name":"APEX"}]""", "Apex", 50, out var truncated);

        Assert.Equal(new[] { 114795 }, games);
        Assert.False(truncated);
    }

    [Fact]
    public void AlternativeNames_TwoGamesHavingTheName_AreBothReported_SoTheCallerCanCallItAmbiguous()
    {
        var games = IgdbCoverArtProvider.ParseAlternativeNameGames(
            """[{"game":1,"name":"Apex"},{"game":2,"name":"Apex"}]""", "Apex", 50, out _);

        Assert.Equal(new[] { 1, 2 }, games);
    }

    [Fact]
    public void AlternativeNames_ARowWhoseNameIsProvablyDifferent_IsSkipped_ButNeverAnUnreadableOne()
    {
        var games = IgdbCoverArtProvider.ParseAlternativeNameGames(
            """[{"game":1,"name":"Apex Legends Mobile"},{"game":2,"name":"Apex"}]""", "Apex", 50, out _);

        Assert.Equal(new[] { 2 }, games);
    }

    [Theory]
    [InlineData("""{"game":1}""")]                                       // not an array
    [InlineData("""[7]""")]                                               // not an object
    [InlineData("""[{"game":1}]""")]                                      // no name: could be the second game
    [InlineData("""[{"game":1,"name":"  "}]""")]
    [InlineData("""[{"name":"Apex"}]""")]                                 // the right name but no game
    [InlineData("""[{"game":0,"name":"Apex"}]""")]
    [InlineData("""[{"game":"1","name":"Apex"}]""")]
    public void AlternativeNames_AnythingThatCouldHideASecondGame_FailsClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.ParseAlternativeNameGames(json, "Apex", 50, out _));
    }

    [Fact]
    public void AlternativeNames_AResponseThatFillsTheLimit_CannotProveUniqueness()
    {
        var rows = string.Join(",", Enumerable.Range(1, 3).Select(i => $$"""{"game":{{i}},"name":"Other {{i}}"}"""));

        IgdbCoverArtProvider.ParseAlternativeNameGames("[" + rows + "]", "Apex", 3, out var truncated);

        Assert.True(truncated);
    }

    // ---- IGDB picker candidates -------------------------------------------------------------------------------------

    [Fact]
    public void Candidates_CarryTheReleaseYearAndAThumbnailWhenIgdbGivesThem()
    {
        var list = IgdbCoverArtProvider.ParseCandidates(
            """[{"id":135400,"name":"Minecraft","first_release_date":1321574400,"cover":{"image_id":"co1abc"}},{"id":2,"name":"Plain"}]""");

        Assert.Equal(2, list.Count);
        Assert.Equal(("135400", "Minecraft", "2011"), (list[0].Id, list[0].Title, list[0].Detail));
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg", list[0].ThumbnailUrl);
        Assert.Null(list[1].Detail);
        Assert.Null(list[1].ThumbnailUrl);
    }

    [Fact]
    public void Candidates_AMalformedEntryIsNotOffered_ButNeverBreaksTheOthers_AndIsNeverIdentityEvidence()
    {
        var list = IgdbCoverArtProvider.ParseCandidates(
            """[{"id":1,"name":"Good"},{"id":"2","name":"Bad id"},{"id":3},{"id":-4,"name":"Negative"},{"id":5,"name":"  "},"nope",{"id":6,"name":"Also good","first_release_date":-1}]""");

        Assert.Equal(new[] { "1", "6" }, list.Select(c => c.Id));
        Assert.Null(list[1].Detail); // an impossible release date is dropped, not shown as a year
    }

    [Fact]
    public void Candidates_AResponseThatIsNotAnArray_IsAnError_NotAnEmptyList()
    {
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.ParseCandidates("""{"message":"nope"}"""));
    }

    // ---- The id-keyed cache -----------------------------------------------------------------------------------------

    [Fact]
    public void IdCache_RoundTrips_AndIsKeyedByTheCatalogIdAlone()
    {
        var dir = Dir();
        var path = IdKeyedCoverCache.PathFor(dir, "42", 2);
        IdKeyedCoverCache.Write(path, "42", "Foo", TestImages.Png(60, 90));

        Assert.NotNull(IdKeyedCoverCache.TryRead(path, "42", "test"));
        Assert.Equal("id-42-v2.png", Path.GetFileName(path));
        // another identity never finds it: the key IS the identity (I8) - nothing to invalidate on a change
        Assert.Null(IdKeyedCoverCache.TryRead(IdKeyedCoverCache.PathFor(dir, "43", 2), "43", "test"));
        // and a different cache VERSION is a different key too
        Assert.Null(IdKeyedCoverCache.TryRead(IdKeyedCoverCache.PathFor(dir, "42", 3), "42", "test"));
    }

    [Fact]
    public void IdCache_ASidecarForADifferentId_IsAMiss_AndBothFilesAreDiscarded()
    {
        var dir = Dir();
        var path = IdKeyedCoverCache.PathFor(dir, "42", 2);
        IdKeyedCoverCache.Write(path, "999", "Someone else", TestImages.Png(60, 90)); // a file at id 42's path claiming to be 999's

        Assert.Null(IdKeyedCoverCache.TryRead(path, "42", "test"));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".meta.json"));
    }

    [Fact]
    public void IdCache_ImageBytesThatNoLongerMatchTheirHash_AreAMiss()
    {
        var dir = Dir();
        var path = IdKeyedCoverCache.PathFor(dir, "42", 2);
        IdKeyedCoverCache.Write(path, "42", "Foo", TestImages.Png(60, 90));
        File.WriteAllBytes(path, TestImages.Png(60, 120)); // a valid image, but not the one the sidecar vouches for

        Assert.Null(IdKeyedCoverCache.TryRead(path, "42", "test"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("garbage")]
    [InlineData("wrong-json")]
    [InlineData("empty-hash")]
    public void IdCache_AnUnusableSidecar_IsAMiss(string kind)
    {
        var dir = Dir();
        var path = IdKeyedCoverCache.PathFor(dir, "42", 2);
        IdKeyedCoverCache.Write(path, "42", "Foo", TestImages.Png(60, 90));
        var meta = path + ".meta.json";
        switch (kind)
        {
            case "missing": File.Delete(meta); break;
            case "garbage": File.WriteAllText(meta, "{{{ not json"); break;
            case "wrong-json": File.WriteAllText(meta, "[1,2,3]"); break;
            case "empty-hash": File.WriteAllText(meta, """{"Id":"42","Title":"Foo","ImageSha256":""}"""); break;
        }

        Assert.Null(IdKeyedCoverCache.TryRead(path, "42", "test"));
    }

    [Fact]
    public void IdCache_ACorruptImage_IsAMiss()
    {
        var dir = Dir();
        var path = IdKeyedCoverCache.PathFor(dir, "42", 2);
        var junk = new byte[] { 1, 2, 3, 4, 5 };
        IdKeyedCoverCache.Write(path, "42", "Foo", junk); // hash matches the junk: still not an image

        Assert.Null(IdKeyedCoverCache.TryRead(path, "42", "test"));
    }

    [Fact]
    public void IdCache_AnUnwritableDirectory_IsSwallowed_NotThrown()
    {
        var dir = Dir();
        var blocker = Path.Combine(dir, "file");
        File.WriteAllText(blocker, "x");

        var exception = Record.Exception(() => IdKeyedCoverCache.Write(IdKeyedCoverCache.PathFor(Path.Combine(blocker, "sub"), "1", 1), "1", "t", TestImages.Png()));

        Assert.Null(exception);
    }

    // ---- Legacy cache discovery -------------------------------------------------------------------------------------

    private static string WriteLegacy(string root, string gameId, int version, int sidecarId, string searched, byte[]? bytes = null, string? sha = null)
    {
        Directory.CreateDirectory(root);
        bytes ??= TestImages.Png(60, 90);
        var path = Path.Combine(root, $"{gameId}-v{version}.png");
        File.WriteAllBytes(path, bytes);
        var sidecar = new { Id = sidecarId, Title = "Legacy Title", SearchedName = searched, ImageSha256 = sha ?? Convert.ToHexString(SHA256.HashData(bytes)) };
        File.WriteAllText(path + ".meta.json", JsonSerializer.Serialize(sidecar));
        return path;
    }

    [Fact]
    public void Legacy_AnIntactEntryLookedUpUnderTodaysTitle_IsValidContinuity()
    {
        var root = Dir();
        WriteLegacy(root, "manual-foo", 13, 555, "Foo");

        var found = LegacyCacheDiscovery.Discover(root, "manual-foo", "555", "Foo", 13);

        Assert.Equal(LegacyDiscoveryStatus.Valid, found.Status);
        Assert.NotNull(found.Image);
        Assert.NotNull(found.Bytes);
    }

    [Fact]
    public void Legacy_AnEntryLookedUpUnderADifferentName_IsStale_NotAVerdictAndNotContinuity()
    {
        var root = Dir();
        WriteLegacy(root, "ea-apex", 13, 555, "Apex");

        var found = LegacyCacheDiscovery.Discover(root, "ea-apex", "555", "Apex Legends", 13);

        Assert.Equal(LegacyDiscoveryStatus.StaleLookup, found.Status);
        Assert.Null(found.Image);
    }

    [Fact]
    public void Legacy_AnEntryFromAnotherCacheVersion_IsIgnored()
    {
        var root = Dir();
        WriteLegacy(root, "manual-foo", 12, 555, "Foo");
        WriteLegacy(root, "manual-foo", 14, 555, "Foo");

        Assert.Equal(LegacyDiscoveryStatus.NoContinuity, LegacyCacheDiscovery.Discover(root, "manual-foo", "555", "Foo", 13).Status);
    }

    [Theory]
    [InlineData("id-differs")]      // sidecar id is not the id settings.json recorded
    [InlineData("hash-differs")]    // sidecar describes other bytes
    [InlineData("nonpositive-id")]
    [InlineData("no-sidecar")]
    [InlineData("not-an-image")]
    [InlineData("bad-sidecar-json")]
    public void Legacy_AnythingUnverifiable_GivesNoContinuity(string kind)
    {
        var root = Dir();
        var path = WriteLegacy(root, "manual-foo", 13, kind == "nonpositive-id" ? 0 : 555, "Foo",
            bytes: kind == "not-an-image" ? [1, 2, 3] : null, sha: kind == "hash-differs" ? new string('A', 64) : null);
        if (kind == "no-sidecar") File.Delete(path + ".meta.json");
        if (kind == "bad-sidecar-json") File.WriteAllText(path + ".meta.json", "{{{");
        var recorded = kind == "id-differs" ? "556" : "555";

        var found = LegacyCacheDiscovery.Discover(root, "manual-foo", recorded, "Foo", 13);

        Assert.Equal(LegacyDiscoveryStatus.NoContinuity, found.Status);
        Assert.Null(found.Image);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("  ")]
    public void Legacy_AGameIdThatCouldEscapeTheCacheDirectory_IsRefused(string gameId)
    {
        var parent = Dir();
        var root = Path.Combine(parent, "cache");
        Directory.CreateDirectory(root);
        WriteLegacy(parent, "evil", 13, 555, "Foo"); // a valid decoy one level ABOVE the cache directory

        Assert.Equal(LegacyDiscoveryStatus.NoContinuity, LegacyCacheDiscovery.Discover(root, gameId, "555", "Foo", 13).Status);
    }

    [Fact]
    public void Legacy_DiscoveryIsReadOnly_NeverDeletesOrRewritesAFile()
    {
        var root = Dir();
        var path = WriteLegacy(root, "manual-foo", 13, 555, "Foo", sha: new string('B', 64)); // unverifiable: the worst case
        var (imageBefore, sidecarBefore, stampBefore) = (File.ReadAllBytes(path), File.ReadAllText(path + ".meta.json"), File.GetLastWriteTimeUtc(path));

        LegacyCacheDiscovery.Discover(root, "manual-foo", "555", "Foo", 13);
        LegacyCacheDiscovery.Discover(root, "manual-foo", "556", "Foo", 13);

        Assert.Equal(imageBefore, File.ReadAllBytes(path));
        Assert.Equal(sidecarBefore, File.ReadAllText(path + ".meta.json"));
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    // ---- Steam launcher art -----------------------------------------------------------------------------------------

    [Fact]
    public void LauncherArt_NeverGuessesByName_ANonSteamGameHasNone()
    {
        var query = IdentityQuery.From(Games.Manual("manual-foo", "Foo"));

        var (image, fromCache) = SteamLauncherArt.Fetch(query, Dir(), CancellationToken.None);

        Assert.Null(image);
        Assert.False(fromCache);
    }
}
