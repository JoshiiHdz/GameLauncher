using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>B1 provider parity, SteamGridDB-specific half (the shared half is CoverProviderConformanceTests):
/// the UNIQUENESS requirement at the selection level - including what became of the storefront-tag
/// preference - the no-retry-after-ambiguity rule, and the real transport behaviour (status codes, bounded
/// and stalled bodies) through the per-instance HTTP seam.</summary>
public class SteamGridDbProviderParityTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-SgdbParity-" + Guid.NewGuid());
        _dirs.Add(dir);
        return dir;
    }

    private static GameEntry Game(string name = "Test Game", GameSource source = GameSource.Manual) => new()
    {
        Id = "manual-sgdb-parity", Name = name, ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = source,
    };

    // ---- Selection: uniqueness ---------------------------------------------------------------------------

    [Fact]
    public void Select_TwoDistinctConfidentCandidates_AreAmbiguous_NotTheFirstRanked()
    {
        var json = """{ "data": [ { "id": 1, "name": "Test Game" }, { "id": 2, "name": "Test Game" } ] }""";

        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Test Game", storefrontTag: null, out var ambiguous);

        Assert.Null(matched);
        Assert.True(ambiguous);
    }

    [Fact]
    public void Select_AStorefrontTag_NoLongerBreaksATie_BetweenTwoConfidentCandidates()
    {
        // Previously the tagged candidate won ("the strongest signal available"). A storefront tag says a game
        // by this name is sold there, not that it is THIS product - two same-titled products can both be
        // tagged - so under a uniqueness rule it must not resolve a real ambiguity.
        var json = """
            { "data": [
                { "id": 1, "name": "Prey", "types": ["gog"] },
                { "id": 2, "name": "Prey", "types": ["steam"] }
            ] }
            """;

        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Prey", storefrontTag: "steam", out var ambiguous);

        Assert.Null(matched);
        Assert.True(ambiguous);
    }

    [Fact]
    public void Select_TwoConfidentCandidates_BothTagged_AreAmbiguous()
    {
        var json = """
            { "data": [
                { "id": 1, "name": "Prey", "types": ["steam"] },
                { "id": 2, "name": "Prey", "types": ["steam"] }
            ] }
            """;

        SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Prey", "steam", out var ambiguous);

        Assert.True(ambiguous);
    }

    [Fact]
    public void Select_ASingleConfidentCandidate_Matches_WhetherOrNotItCarriesTheTag()
    {
        var untagged = """{ "data": [ { "id": 7, "name": "Test Game", "types": [] } ] }""";
        var tagged = """{ "data": [ { "id": 7, "name": "Test Game", "types": ["steam"] } ] }""";

        var a = SteamGridDbCoverArtProvider.SelectMatchedGame(untagged, "Test Game", "steam", out var ambiguousA);
        var b = SteamGridDbCoverArtProvider.SelectMatchedGame(tagged, "Test Game", "steam", out var ambiguousB);

        Assert.Equal(7, a?.Id);
        Assert.Equal(7, b?.Id);
        Assert.False(ambiguousA);
        Assert.False(ambiguousB);
    }

    [Fact]
    public void Select_ARepeatedId_IsOneCandidate()
    {
        var json = """{ "data": [ { "id": 7, "name": "Test Game" }, { "id": 7, "name": "Test Game" } ] }""";

        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Test Game", null, out var ambiguous);

        Assert.Equal(7, matched?.Id);
        Assert.False(ambiguous);
    }

    [Fact]
    public void Select_NoConfidentCandidate_IsNoMatch_NotAmbiguous()
    {
        var json = """{ "data": [ { "id": 1, "name": "Something Else" }, { "id": 2, "name": "Another Thing" } ] }""";

        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Test Game", null, out var ambiguous);

        Assert.Null(matched);
        Assert.False(ambiguous); // the distinction the whole status model exists for
    }

    [Fact]
    public void Select_ExactMatchAlongsideNonConfidentLookalikes_IsStillUnique()
    {
        // Look-alikes are not candidates at all, so they cannot create ambiguity.
        var json = """
            { "data": [
                { "id": 1, "name": "Test Game: Deluxe Edition" },
                { "id": 2, "name": "Test Game" },
                { "id": 3, "name": "Test Game 2" }
            ] }
            """;

        var matched = SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Test Game", null, out var ambiguous);

        Assert.Equal(2, matched?.Id);
        Assert.False(ambiguous);
    }

    [Fact]
    public void Select_TheLegacyOverloads_TreatAmbiguityAsNull()
    {
        var json = """{ "data": [ { "id": 1, "name": "Test Game" }, { "id": 2, "name": "Test Game" } ] }""";

        Assert.Null(SteamGridDbCoverArtProvider.SelectGameId(json, "Test Game", null));
        Assert.Null(SteamGridDbCoverArtProvider.SelectMatchedGame(json, "Test Game", null));
    }

    // ---- GetCoverArt: an ambiguous identity is not retried --------------------------------------------------

    [Fact]
    public void AnAmbiguousPrimarySearch_IsNotRetriedWithTheCompactedWordsForm()
    {
        // "AWayOut" would normally retry as "A Way Out" when the first search finds nothing confident. But an
        // Ambiguous result is a settled fact about the identity - a second attempt would be a second guess.
        var searches = new List<string>();
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = q =>
            {
                searches.Add(q);
                return """{ "data": [ { "id": 1, "name": "A Way Out" }, { "id": 2, "name": "A Way Out" } ] }""";
            },
        };

        var art = provider.GetCoverArt(Game("AWayOut"), out _, out _, out var status, NewCacheDir());

        Assert.Null(art);
        Assert.Equal(CoverLookupStatus.Ambiguous, status);
        Assert.Single(searches);
    }

    [Fact]
    public void ANoMatchPrimarySearch_StillRetriesWithTheCompactedWordsForm()
    {
        // The retry itself is unchanged for the case it exists for.
        var searches = new List<string>();
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = q =>
            {
                searches.Add(q);
                return q == "A Way Out"
                    ? """{ "data": [ { "id": 36897, "name": "A Way Out" } ] }"""
                    : """{ "data": [] }""";
            },
            FetchGridImageBytesOverride = _ => TestImages.Png(),
        };

        provider.GetCoverArt(Game("AWayOut"), out _, out var matched, out var status, NewCacheDir());

        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.Equal(36897, matched?.Id);
        Assert.Equal(new[] { "AWayOut", "A Way Out" }, searches);
    }

    // ---- The cache-version bump ------------------------------------------------------------------------------

    [Fact]
    public void TheCacheVersion_WasBumpedPastTheLastShippedOne_SoOldEntriesAreReEvaluated()
    {
        // v12 shipped WITHOUT the uniqueness check, so a v12 entry may be the wrong one of several same-titled
        // products. The cache is keyed by version precisely so a logic change makes old entries unreachable.
        Assert.True(SteamGridDbCoverArtProvider.CacheVersionForTest >= 13);
    }

    [Fact]
    public void AVersion12CacheEntry_IsNotServed_ItWasCachedBeforeUniquenessAndValidation()
    {
        var dir = NewCacheDir();
        Directory.CreateDirectory(dir);
        var game = Game();
        var v12 = Path.Combine(dir, $"{game.Id}-v12.png");
        var png = TestImages.Png();
        File.WriteAllBytes(v12, png);
        File.WriteAllText(v12 + ".meta.json",
            $$"""{"Id":11,"Title":"Test Game","SearchedName":"Test Game","ImageSha256":"{{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))}}"}""");
        var searches = 0;
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => { searches++; return """{ "data": [ { "id": 11, "name": "Test Game" } ] }"""; },
            FetchGridImageBytesOverride = _ => TestImages.Png(),
        };

        provider.GetCoverArt(game, out var fromCache, out _, out var status, dir);

        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.False(fromCache); // a fully valid v12 entry was ignored; the lookup re-ran under the new logic
        Assert.Equal(1, searches);
        Assert.True(File.Exists(v12)); // and left alone: an old file is ignored, never deleted by this provider
    }

    // ---- The real transport: status codes, bounded and stalled bodies ----------------------------------------

    private const string SearchOk = """{ "data": [ { "id": 5, "name": "Test Game", "types": [] } ] }""";
    private const string GridOk = """{ "data": [ { "url": "https://cdn2.steamgriddb.com/grid/cover.png" } ] }""";

    private static FuncHttpHandler Handler(
        Func<HttpResponseMessage>? search = null, Func<HttpResponseMessage>? grid = null,
        Func<CancellationToken, HttpResponseMessage>? image = null) =>
        new((req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/search/autocomplete/", StringComparison.Ordinal))
                return search?.Invoke() ?? FakeIgdbHandler.Json(SearchOk);
            if (path.Contains("/grids/game/", StringComparison.Ordinal))
                return grid?.Invoke() ?? FakeIgdbHandler.Json(GridOk);
            return image?.Invoke(ct) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) };
        });

    private static SteamGridDbCoverArtProvider Provider(FuncHttpHandler handler, TimeSpan? imageTimeout = null) =>
        new("key") { HttpHandlerOverrideForTest = handler, ImageDownloadTimeoutOverrideForTest = imageTimeout };

    [Fact]
    public void FullRealPath_ResolvesCaches_AndTheSecondLookupMakesNoRequests()
    {
        var dir = NewCacheDir();
        var handler = Handler();
        var provider = Provider(handler);
        var game = Game();

        var first = provider.GetCoverArt(game, out var firstFromCache, out var matched, out var firstStatus, dir);
        var callsAfterFirst = handler.Calls;
        var second = provider.GetCoverArt(game, out var secondFromCache, out _, out var secondStatus, dir);

        Assert.NotNull(first);
        Assert.Equal(CoverLookupStatus.Resolved, firstStatus);
        Assert.False(firstFromCache);
        Assert.Equal(5, matched?.Id);
        Assert.Equal(3, callsAfterFirst); // search, grid listing, image
        Assert.NotNull(second);
        Assert.Equal(CoverLookupStatus.Resolved, secondStatus);
        Assert.True(secondFromCache);
        Assert.Equal(callsAfterFirst, handler.Calls); // the cache hit made no request at all
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void ANonSuccessSearchStatus_IsUnavailable_NotNoMatch_AndNothingFurtherIsRequested(HttpStatusCode code)
    {
        // Before B1 this returned null and read as "SteamGridDB has no such game", which is a claim about the
        // catalog that an authentication or server failure cannot support.
        var handler = Handler(search: () => new HttpResponseMessage(code));

        var art = Provider(handler).GetCoverArt(Game(), out _, out _, out var status, NewCacheDir());

        Assert.Null(art);
        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void AGridListing404_IsIdentifiedWithoutUsableArt()
    {
        var handler = Handler(grid: () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var art = Provider(handler).GetCoverArt(Game(), out _, out var matched, out var status, NewCacheDir());

        Assert.Null(art);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Null(matched); // no art, so no cached evidence either
    }

    [Fact]
    public void AGridListing500_IsUnavailable_NotNoArt()
    {
        var handler = Handler(grid: () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Provider(handler).GetCoverArt(Game(), out _, out _, out var status, NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
    }

    [Fact]
    public void AnImage404_IsIdentifiedWithoutUsableArt_AndNotCached()
    {
        var dir = NewCacheDir();
        var handler = Handler(image: _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Provider(handler).GetCoverArt(Game(), out _, out _, out var status, dir);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void AnImage500_IsUnavailable()
    {
        var handler = Handler(image: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Provider(handler).GetCoverArt(Game(), out _, out _, out var status, NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
    }

    [Fact]
    public void AnEndlessImageBody_IsCappedAtTheByteLimit_NotReadIntoMemoryForever()
    {
        // GetByteArrayAsync (the old call) has no cap: this body never ends.
        var dir = NewCacheDir();
        var handler = Handler(image: _ => FakeIgdbHandler.EndlessBody());

        var sw = Stopwatch.StartNew();
        Provider(handler).GetCoverArt(Game(), out _, out _, out var status, dir);
        sw.Stop();

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"the cap should end this promptly, took {sw.Elapsed}");
    }

    [Fact]
    public void AStalledImageBody_IsUnavailable_WithinItsTimeBudget()
    {
        var dir = NewCacheDir();
        var handler = Handler(image: _ => FakeIgdbHandler.StallingBody());

        var sw = Stopwatch.StartNew();
        Provider(handler, imageTimeout: TimeSpan.FromMilliseconds(300)).GetCoverArt(Game(), out _, out _, out var status, dir);
        sw.Stop();

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"the download's own budget should end this, took {sw.Elapsed}");
    }

    [Fact]
    public void AnOversizedDownloadedImage_IsIdentifiedWithoutUsableArt_AndNotCached()
    {
        var dir = NewCacheDir();
        var handler = Handler(image: _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png(9000, 100)) });

        Provider(handler).GetCoverArt(Game(), out _, out _, out var status, dir);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Empty(Directory.GetFiles(dir));
    }
}
