using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>The identity-facing methods of the two real providers, driven through the real HttpClient pipeline against fake
/// transports (never a real host): status mapping, fail-closed parsing, the id-keyed cache, host restrictions on picker image
/// downloads, and the boundary rule - only the caller's own cancellation escapes as an exception.
/// Response SHAPES are the documented ones; whether the live services really return them is on the external checklist.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class IdentityProviderHttpTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdHttp-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static IgdbCoverArtProvider Igdb(FakeIgdbHandler handler) =>
        new("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler, BackoffDelayOverrideForTest = _ => { } };

    private static FakeIgdbHandler IgdbApi(Func<string, string, HttpResponseMessage> respond, Func<HttpRequestMessage, HttpResponseMessage>? image = null) =>
        new()
        {
            OnApi = (request, _, _) => respond(request.RequestUri!.AbsolutePath, request.Content!.ReadAsStringAsync().Result),
            OnImage = image is null ? null : (request, _) => image(request),
        };

    // ---- IGDB: title path -----------------------------------------------------------------------------------------------

    [Fact]
    public void IgdbTitle_OneExactMatch_IsFound()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"id":11,"name":"Foo"},{"id":12,"name":"Foo Deluxe Edition"}]""")));

        var result = provider.SearchByTitleForIdentity("Foo", CancellationToken.None);

        Assert.Equal(CatalogStatus.Match, result.Status);
        Assert.Equal(("11", "Foo"), (result.Match!.Id, result.Match!.Title));
    }

    [Fact]
    public void IgdbTitle_TwoExactMatches_AreAmbiguous_NeverTheFirstRanked()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"id":11,"name":"Foo"},{"id":12,"name":"Foo"}]""")));

        Assert.Equal(CatalogStatus.Ambiguous, provider.SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbTitle_NothingExact_IsNoMatch()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"id":11,"name":"Something Else"}]""")));

        Assert.Equal(CatalogStatus.NoMatch, provider.SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbTitle_AKnownUmbrellaName_IsAmbiguous_WithoutAnyRequest()
    {
        var handler = IgdbApi((_, _) => Ok("[]"));

        Assert.Equal(CatalogStatus.Ambiguous, Igdb(handler).SearchByTitleForIdentity("Call of Duty", CancellationToken.None).Status);
        Assert.Equal(0, handler.ApiCalls);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(403)]
    [InlineData(429)]
    public void IgdbTitle_AServiceFailure_IsUnavailable_NeverANonMatch(int status)
    {
        var provider = Igdb(IgdbApi((_, _) => new HttpResponseMessage((HttpStatusCode)status)));

        Assert.Equal(CatalogStatus.Unavailable, provider.SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbTitle_AMalformedResponse_IsUnavailable_NotNoMatch()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("""{"not":"an array"}""")));

        Assert.Equal(CatalogStatus.Unavailable, provider.SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbTitle_TheCallersCancellation_IsTheOnlyThingThatEscapes()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("[]")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => provider.SearchByTitleForIdentity("Foo", cts.Token));
    }

    // ---- IGDB: external id path (documented schema: external_game_sources by name, then external_game_source + uid) ---------------

    private const string SteamSource = """[{"id":1,"name":"Steam"}]""";

    /// <summary>An IGDB API whose `external_game_sources` answers `sources` and whose other endpoints go to `respond`.</summary>
    private static FakeIgdbHandler IgdbWithSources(string sources, Func<string, string, HttpResponseMessage> respond, List<string>? seen = null) =>
        IgdbApi((path, body) =>
        {
            seen?.Add(path + " " + body);
            return path.EndsWith("/external_game_sources", StringComparison.Ordinal) ? Ok(sources) : respond(path, body);
        });

    [Fact]
    public void IgdbExternalId_MapsAStoreIdToOneGame_AndNamesIt_UsingOnlyTheDocumentedFields()
    {
        var seen = new List<string>();
        var provider = Igdb(IgdbWithSources(SteamSource, (path, _) => path.EndsWith("/external_games", StringComparison.Ordinal)
            ? Ok("""[{"game":7,"uid":"1091500","external_game_source":1}]""")
            : Ok("""[{"id":7,"name":"Cyberpunk 2077"}]"""), seen));

        var result = provider.MapExternalId("Steam", "1091500", CancellationToken.None);

        Assert.Equal(CatalogStatus.Match, result.Status);
        Assert.Equal(("7", "Cyberpunk 2077"), (result.Match!.Id, result.Match!.Title));
        var query = Assert.Single(seen, r => r.StartsWith("/v4/external_games ", StringComparison.Ordinal));
        Assert.Contains("uid = \"1091500\"", query);
        Assert.Contains("external_game_source = 1", query);   // the id IGDB itself reported for "Steam"
        Assert.DoesNotContain("category", string.Join("\n", seen)); // the deprecated field is never requested or filtered on
        Assert.Contains(seen, r => r.Contains("name = \"Steam\"", StringComparison.Ordinal));
    }

    [Fact]
    public void IgdbExternalId_TheSourceIdIsWhateverIgdbSays_AndIsLookedUpOnlyOncePerProvider()
    {
        var sourceCalls = 0;
        var handler = IgdbApi((path, body) =>
        {
            if (path.EndsWith("/external_game_sources", StringComparison.Ordinal))
            {
                sourceCalls++;
                return Ok("""[{"id":26,"name":"Steam"}]""");
            }

            Assert.Contains("external_game_source = 26", body);
            return Ok("[]");
        });
        var provider = Igdb(handler);

        provider.MapExternalId("Steam", "1", CancellationToken.None);
        provider.MapExternalId("Steam", "2", CancellationToken.None);

        Assert.Equal(1, sourceCalls);
    }

    [Fact]
    public void IgdbExternalId_NoMapping_IsNoMatch_SoTheTitlePathStillRuns()
    {
        var provider = Igdb(IgdbWithSources(SteamSource, (_, _) => Ok("[]")));

        Assert.Equal(CatalogStatus.NoMatch, provider.MapExternalId("Steam", "1", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbExternalId_AnIgdbWithNoSuchSource_IsNoMatch_NotAGuess()
    {
        var handler = IgdbWithSources("""[{"id":5,"name":"GOG"}]""", (_, _) => Ok("[]"));

        Assert.Equal(CatalogStatus.NoMatch, Igdb(handler).MapExternalId("Steam", "1", CancellationToken.None).Status);
        Assert.Equal(1, handler.ApiCalls); // it asked which sources exist, found none called Steam, and stopped
    }

    [Fact]
    public void IgdbExternalId_AStoreIdMappedToTwoGames_IsAmbiguous()
    {
        var provider = Igdb(IgdbWithSources(SteamSource, (_, _) =>
            Ok("""[{"game":7,"uid":"1","external_game_source":1},{"game":8,"uid":"1","external_game_source":1}]""")));

        Assert.Equal(CatalogStatus.Ambiguous, provider.MapExternalId("Steam", "1", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbExternalId_AMappingThatDoesNotEchoTheRequest_IsIgnored()
    {
        var provider = Igdb(IgdbWithSources(SteamSource, (_, _) => Ok("""[{"game":7,"uid":"OTHER","external_game_source":1}]""")));

        Assert.Equal(CatalogStatus.NoMatch, provider.MapExternalId("Steam", "1", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbExternalId_AnUnreadableResponse_IsUnavailable_AndSoIsAnUnreadableSourceListing_AndAGameWithNoName()
    {
        Assert.Equal(CatalogStatus.Unavailable,
            Igdb(IgdbWithSources(SteamSource, (_, _) => Ok("""[{"game":7,"uid":"1"}]"""))).MapExternalId("Steam", "1", CancellationToken.None).Status);

        Assert.Equal(CatalogStatus.Unavailable,
            Igdb(IgdbWithSources("""{"unexpected":true}""", (_, _) => Ok("[]"))).MapExternalId("Steam", "1", CancellationToken.None).Status);

        var noName = Igdb(IgdbWithSources(SteamSource, (path, _) => path.EndsWith("/external_games", StringComparison.Ordinal)
            ? Ok("""[{"game":7,"uid":"1","external_game_source":1}]""") : Ok("""[{"id":7}]""")));
        Assert.Equal(CatalogStatus.Unavailable, noName.MapExternalId("Steam", "1", CancellationToken.None).Status);
    }

    [Theory]
    [InlineData("""[{"game":7,"uid":"1091500","external_game_source":1}]""", "1091500", LauncherConsistency.Consistent)]
    [InlineData("""[{"game":7,"uid":"1","external_game_source":1},{"game":7,"uid":"1091500","external_game_source":1}]""", "1091500", LauncherConsistency.Consistent)]
    [InlineData("""[{"game":7,"uid":"555","external_game_source":1}]""", "1091500", LauncherConsistency.Contradicted)]
    [InlineData("""[]""", "1091500", LauncherConsistency.Unknown)]                                 // no listing cannot contradict
    [InlineData("""[{"game":7,"external_game_source":1}]""", "1091500", LauncherConsistency.Unknown)]  // unreadable cannot contradict
    [InlineData("""[{"game":7,"uid":"555","external_game_source":9}]""", "1091500", LauncherConsistency.Unknown)] // another source's id says nothing
    [InlineData("""{"x":1}""", "1091500", LauncherConsistency.Unknown)]
    public void IgdbConsistency_ContradictsOnlyWhenIgdbListsADIFFERENTStoreId(string response, string uid, LauncherConsistency expected)
    {
        var provider = Igdb(IgdbWithSources(SteamSource, (_, _) => Ok(response)));

        Assert.Equal(expected, provider.CheckExternalConsistency("7", "Steam", uid, CancellationToken.None));
    }

    [Fact]
    public void IgdbConsistency_AFailure_ANonNumericGameId_OrNoSuchSource_IsUnknown()
    {
        Assert.Equal(LauncherConsistency.Unknown,
            Igdb(IgdbWithSources(SteamSource, (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError))).CheckExternalConsistency("7", "Steam", "1", CancellationToken.None));
        Assert.Equal(LauncherConsistency.Unknown,
            Igdb(IgdbWithSources(SteamSource, (_, _) => Ok("[]"))).CheckExternalConsistency("abc", "Steam", "1", CancellationToken.None));
        Assert.Equal(LauncherConsistency.Unknown,
            Igdb(IgdbWithSources("[]", (_, _) => Ok("""[{"game":7,"uid":"5","external_game_source":1}]"""))).CheckExternalConsistency("7", "Steam", "1", CancellationToken.None));
    }

    // ---- IGDB: alternative names (design 4.5) ---------------------------------------------------------------------------------

    [Fact]
    public void IgdbAlternativeName_ExactlyOneGameHasTheName_IsFound_AndNamedByItsOwnTitle()
    {
        var bodies = new List<string>();
        var provider = Igdb(IgdbApi((path, body) =>
        {
            bodies.Add(path + " " + body);
            return path.EndsWith("/alternative_names", StringComparison.Ordinal)
                ? Ok("""[{"id":1,"game":114795,"name":"Apex"}]""")
                : Ok("""[{"id":114795,"name":"Apex Legends"}]""");
        }));

        var result = provider.SearchByAlternativeName("Apex", CancellationToken.None);

        Assert.Equal(CatalogStatus.Match, result.Status);
        Assert.Equal(("114795", "Apex Legends"), (result.Match!.Id, result.Match!.Title));
        Assert.Contains(bodies, b => b.StartsWith("/v4/alternative_names ", StringComparison.Ordinal) && b.Contains("name ~ \"Apex\"", StringComparison.Ordinal));
    }

    [Fact]
    public void IgdbAlternativeName_NoGame_IsNoMatch_AndTwoGamesAreAmbiguous_AndNeitherGuesses()
    {
        Assert.Equal(CatalogStatus.NoMatch, Igdb(IgdbApi((_, _) => Ok("[]"))).SearchByAlternativeName("Apex", CancellationToken.None).Status);

        var handler = IgdbApi((_, _) => Ok("""[{"game":1,"name":"Apex"},{"game":2,"name":"Apex"}]"""));
        Assert.Equal(CatalogStatus.Ambiguous, Igdb(handler).SearchByAlternativeName("Apex", CancellationToken.None).Status);
        Assert.Equal(1, handler.ApiCalls); // it never went on to fetch a "best" game's name
    }

    [Fact]
    public void IgdbAlternativeName_AFullLimitOfRows_CannotProveUniqueness_SoIsAmbiguous()
    {
        var rows = string.Join(",", Enumerable.Range(1, IgdbCoverArtProvider.AlternativeNameLimit).Select(i => $$"""{"game":{{i}},"name":"Other {{i}}"}"""));

        Assert.Equal(CatalogStatus.Ambiguous,
            Igdb(IgdbApi((_, _) => Ok("[" + rows + "]"))).SearchByAlternativeName("Apex", CancellationToken.None).Status);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(403)]
    public void IgdbAlternativeName_AServiceFailure_IsUnavailable_NeverNoMatch(int status)
    {
        Assert.Equal(CatalogStatus.Unavailable,
            Igdb(IgdbApi((_, _) => new HttpResponseMessage((HttpStatusCode)status))).SearchByAlternativeName("Apex", CancellationToken.None).Status);
    }

    [Fact]
    public void IgdbAlternativeName_AMalformedResponse_IsUnavailable_AndCancellationEscapes()
    {
        Assert.Equal(CatalogStatus.Unavailable,
            Igdb(IgdbApi((_, _) => Ok("""[{"game":1}]"""))).SearchByAlternativeName("Apex", CancellationToken.None).Status);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Igdb(IgdbApi((_, _) => Ok("[]"))).SearchByAlternativeName("Apex", cts.Token));
    }

    // ---- IGDB: id-keyed cover -------------------------------------------------------------------------------------------

    [Fact]
    public void IgdbCover_IsFetchedByIdOnce_ThenServedFromTheCacheWithNoNetworkAtAll()
    {
        var cache = Dir();
        var handler = IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ => Bytes(TestImages.Png(60, 90)));
        var provider = Igdb(handler);

        var first = provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);
        var callsAfterFirst = (handler.ApiCalls, handler.ImageCalls);
        var second = provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);

        Assert.Equal((CoverLookupStatus.Resolved, false), (first.Status, first.FromCache));
        Assert.Equal((CoverLookupStatus.Resolved, true), (second.Status, second.FromCache));
        Assert.Equal(callsAfterFirst, (handler.ApiCalls, handler.ImageCalls));
        Assert.True(File.Exists(IdKeyedCoverCache.PathFor(cache, "7", IgdbCoverArtProvider.IdCacheVersion)));
    }

    [Fact]
    public void IgdbCover_AnotherId_NeverReadsThisIdsCache()
    {
        var cache = Dir();
        var handler = IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ => Bytes(TestImages.Png(60, 90)));
        var provider = Igdb(handler);
        provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);

        var other = provider.FetchCoverForId("8", "Bar", cache, CancellationToken.None);

        Assert.False(other.FromCache);
        Assert.Equal(2, handler.ImageCalls);
    }

    [Fact]
    public void IgdbCover_AGameWithNoCover_IsIdentifiedWithoutUsableArt_AndCachesNothing()
    {
        var cache = Dir();
        var provider = Igdb(IgdbApi((_, _) => Ok("[]")));

        var result = provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, result.Status);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void IgdbCover_AnUndecodableImage_IsIdentifiedWithoutUsableArt_AndCachesNothing()
    {
        var cache = Dir();
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ => Bytes([1, 2, 3, 4])));

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None).Status);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void IgdbCover_AServiceFailure_IsUnavailable_NotNoArt_AndCachesNothing()
    {
        var cache = Dir();
        var provider = Igdb(IgdbApi((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Equal(CoverLookupStatus.Unavailable, provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None).Status);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void IgdbCachedCover_IsReadWithNoRequestAtAll_OnlyForItsOwnId_AndACorruptEntryIsAMiss()
    {
        var cache = Dir();
        var handler = IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ => Bytes(TestImages.Png(60, 90)));
        var provider = Igdb(handler);
        Assert.Null(provider.ReadCachedCoverForId("7", cache));            // an empty cache is a miss - and asks nobody
        provider.FetchCoverForId("7", "Foo", cache, CancellationToken.None);
        var calls = (handler.ApiCalls, handler.ImageCalls);

        Assert.NotNull(provider.ReadCachedCoverForId("7", cache));
        Assert.Null(provider.ReadCachedCoverForId("8", cache));            // another id never reads this id's image
        Assert.Null(provider.ReadCachedCoverForId("../x", cache));         // and a non-numeric id never reaches the file system
        Assert.Equal(calls, (handler.ApiCalls, handler.ImageCalls));
        Assert.NotNull(new IgdbCatalog(provider, cache).TryReadCachedCover("7"));   // what the resolver actually calls
        Assert.Null(new IgdbCatalog(provider, cache).TryReadCachedCover("8"));

        File.WriteAllBytes(IdKeyedCoverCache.PathFor(cache, "7", IgdbCoverArtProvider.IdCacheVersion), [1, 2, 3, 4]);
        Assert.Null(provider.ReadCachedCoverForId("7", cache));            // a corrupt entry is not "usable pixels"
        Assert.Equal(calls, (handler.ApiCalls, handler.ImageCalls));
    }

    [Fact]
    public void SgdbCachedCover_IsReadWithNoRequestAtAll_OnlyForItsOwnId_AndACorruptEntryIsAMiss()
    {
        var cache = Dir();
        var handler = SgdbApi(url => url.Contains("/grids/game/", StringComparison.Ordinal)
            ? Ok("""{"data":[{"id":1,"url":"https://cdn2.steamgriddb.com/grid/a.png"}]}""")
            : Bytes(TestImages.Png(60, 90)));
        var provider = Sgdb(handler);
        Assert.Null(provider.ReadCachedCoverForId("9", cache));
        provider.FetchCoverForId("9", "Foo", cache, CancellationToken.None);
        var calls = handler.Calls;

        Assert.NotNull(provider.ReadCachedCoverForId("9", cache));
        Assert.Null(provider.ReadCachedCoverForId("10", cache));
        Assert.Null(provider.ReadCachedCoverForId("../x", cache));
        Assert.Equal(calls, handler.Calls);
        Assert.NotNull(new SteamGridDbCatalog(provider, cache).TryReadCachedCover("9"));
        Assert.Null(new SteamGridDbCatalog(provider, cache).TryReadCachedCover("10"));

        File.WriteAllBytes(IdKeyedCoverCache.PathFor(cache, "9", SteamGridDbCoverArtProvider.IdCacheVersion), [1, 2, 3, 4]);
        Assert.Null(provider.ReadCachedCoverForId("9", cache));
        Assert.Equal(calls, handler.Calls);
    }

    [Fact]
    public void IgdbCover_ANonNumericId_IsRefusedWithoutARequest()
    {
        var handler = IgdbApi((_, _) => Ok("[]"));

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, Igdb(handler).FetchCoverForId("../x", "Foo", Dir(), CancellationToken.None).Status);
        Assert.Equal(0, handler.ApiCalls);
    }

    [Fact]
    public void IgdbCover_ACancelledLookup_ThrowsAndCachesNothing()
    {
        var cache = Dir();
        using var cts = new CancellationTokenSource();
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ =>
        {
            cts.Cancel(); // cancelled while the image is in flight
            return Bytes(TestImages.Png(60, 90));
        }));

        Assert.ThrowsAny<OperationCanceledException>(() => provider.FetchCoverForId("7", "Foo", cache, cts.Token));
        Assert.Empty(Directory.GetFiles(cache));
    }

    /// <summary>A body that cancels the caller's token in the very read that reports end-of-stream: the bytes ARE complete and the
    /// download loop has no further token check, so only the provider's own check before its cache write can notice the
    /// cancellation. This is the one window in which a cancelled lookup could otherwise still leave a cache entry behind.</summary>
    private sealed class CancelAtEndOfBody(byte[] bytes, CancellationTokenSource cts) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Track(_inner.Read(buffer.Span)));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Track(_inner.Read(buffer, offset, count)));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Track(int read)
        {
            if (read == 0)
                cts.Cancel();
            return read;
        }
    }

    [Fact]
    public void IgdbCover_CancelledAsTheLastByteArrives_ThrowsAndCachesNothing()
    {
        var cache = Dir();
        using var cts = new CancellationTokenSource();
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"image_id":"co1"}]"""), _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAtEndOfBody(TestImages.Png(60, 90), cts)) }));

        Assert.ThrowsAny<OperationCanceledException>(() => provider.FetchCoverForId("7", "Foo", cache, cts.Token));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void SgdbCover_CancelledAsTheLastByteArrives_ThrowsAndCachesNothing()
    {
        var cache = Dir();
        using var cts = new CancellationTokenSource();
        var provider = Sgdb(SgdbApi(url => url.Contains("/grids/game/", StringComparison.Ordinal)
            ? Ok("""{"data":[{"id":1,"url":"https://cdn2.steamgriddb.com/grid/a.png"}]}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAtEndOfBody(TestImages.Png(60, 90), cts)) }));

        Assert.ThrowsAny<OperationCanceledException>(() => provider.FetchCoverForId("9", "Foo", cache, cts.Token));
        Assert.Empty(Directory.GetFiles(cache));
    }

    // ---- IGDB: picker ---------------------------------------------------------------------------------------------------

    [Fact]
    public void IgdbPicker_RetriesWithoutTheCoverExpansion_WhenIgdbRejectsIt()
    {
        var bodies = new List<string>();
        var provider = Igdb(IgdbApi((_, body) =>
        {
            bodies.Add(body);
            return body.Contains("cover.image_id", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Ok("""[{"id":1,"name":"Foo"}]""");
        }));

        var list = provider.SearchCandidatesForPicker("Foo", CancellationToken.None);

        Assert.Single(list);                                                                  // the same game from both queries is listed once
        Assert.Equal(4, bodies.Count);                                                        // relevance + newest, each retried without the cover expansion
        Assert.Equal(2, bodies.Count(b => !b.Contains("cover.image_id", StringComparison.Ordinal)));
    }

    // ---- IGDB: the picker also lists the NEWEST matches (a franchise name buries the game you installed) ----------------------

    private static string GamesJson(params (int Id, string Name, int Year)[] games) =>
        "[" + string.Join(",", games.Select(g => $$"""{"id":{{g.Id}},"name":"{{g.Name}}","first_release_date":{{new DateTimeOffset(g.Year, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()}}}""")) + "]";

    // What IGDB really returned for "Call of Duty": the old games first (Black Ops 7 was result 47 of 97).
    private static readonly string CodByRelevance = GamesJson((621, "Call of Duty", 2003), (294571, "Call of Duty 2", 2007), (949, "Call of Duty 3", 2006),
        (119160, "Call of Duty 2", 2006), (80555, "Duty Calls", 2011), (28204, "Call of Duty: WWII", 2017), (77290, "Call of Duty", 2004),
        (322845, "Call of Duty", 2004), (217815, "Call of Duty: Warzone", 2022), (545, "Call of Duty: Black Ops", 2010));

    private static readonly string CodNewest = GamesJson((403310, "Call of Duty: Modern Warfare 4", 2026), (348220, "Call of Duty: Black Ops 7", 2025),
        (302156, "Call of Duty: Black Ops 6", 2024), (260780, "Call of Duty: Modern Warfare III", 2023), (217815, "Call of Duty: Warzone", 2022));

    [Fact]
    public void IgdbPicker_AFranchiseName_StillListsTheNewestGame_Instead_OfOnlyIgdbsOldestFirstRanking()
    {
        var bodies = new List<string>();
        var provider = Igdb(IgdbApi((_, body) =>
        {
            bodies.Add(body);
            return Ok(body.StartsWith("search", StringComparison.Ordinal) || body.Contains("; search", StringComparison.Ordinal) ? CodByRelevance : CodNewest);
        }));

        var list = provider.SearchCandidatesForPicker("Call of Duty", CancellationToken.None);

        var titles = list.Select(c => c.Title).ToList();
        Assert.Equal(new[] { "621", "77290", "322845" }, list.Take(3).Select(c => c.Id));          // exact-title matches first: a franchise name is a legitimate answer
        Assert.InRange(titles.IndexOf("Call of Duty: Black Ops 7"), 0, 9);                        // ...and Black Ops 7 is now within the first ten, not unreachable
        Assert.Equal(list.Count, list.Select(c => c.Id).Distinct().Count());                      // no game twice (Warzone came from both queries)
        Assert.InRange(list.Count, 1, 30);

        var newestQuery = Assert.Single(bodies, b => b.Contains("sort first_release_date desc", StringComparison.Ordinal));
        Assert.Contains("where name ~ *\"Call of Duty\"*", newestQuery);
        Assert.Contains("parent_game = null & version_parent = null", newestQuery);               // main games only: no seasons, DLC or editions
        Assert.Contains("first_release_date != null", newestQuery);
    }

    [Fact]
    public void IgdbPicker_AnAbbreviationIgdbUnderstands_StaysNearTheTop()
    {
        var provider = Igdb(IgdbApi((_, body) => Ok(body.Contains("sort first_release_date", StringComparison.Ordinal)
            ? "[]"                                                                                // nothing contains "BO7" in its name
            : GamesJson((348220, "Call of Duty: Black Ops 7", 2025)))));

        var list = provider.SearchCandidatesForPicker("BO7", CancellationToken.None);

        Assert.Equal("348220", Assert.Single(list).Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void IgdbPicker_WhenTheNewestQueryFails_TheRelevanceResultsStillStand(HttpStatusCode status)
    {
        var provider = Igdb(IgdbApi((_, body) => body.Contains("sort first_release_date", StringComparison.Ordinal)
            ? new HttpResponseMessage(status)
            : Ok(CodByRelevance)));

        var list = provider.SearchCandidatesForPicker("Call of Duty", CancellationToken.None);

        Assert.Equal(10, list.Count);                                                            // all 10 relevance rows: the failed second query only ever ADDS candidates
        Assert.Contains(list, c => c.Id == "621");
    }

    [Fact]
    public void IgdbPicker_TheContainsQuery_CannotBeWidenedOrBroken_ByTheTypedText()
    {
        var bodies = new List<string>();
        var provider = Igdb(IgdbApi((_, body) => { bodies.Add(body); return Ok("[]"); }));

        provider.SearchCandidatesForPicker("Fo*o \"x\" \\", CancellationToken.None);

        var newest = Assert.Single(bodies, b => b.Contains("sort first_release_date", StringComparison.Ordinal));
        Assert.DoesNotContain("Fo*o", newest);                                                    // `*` is IGDB's wildcard: it is removed, never passed through
        Assert.Contains("\\\"x\\\"", newest);                                                    // quotes and backslashes are escaped
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("  a ")]
    [InlineData("***")]
    public void IgdbPicker_TooLittleTextForAContainsMatch_SkipsTheNewestQuery(string text)
    {
        var bodies = new List<string>();
        var provider = Igdb(IgdbApi((_, body) => { bodies.Add(body); return Ok("[]"); }));

        provider.SearchCandidatesForPicker(text, CancellationToken.None);

        Assert.Single(bodies);                                                                    // only the relevance search was made
    }

    private static CatalogCandidate C(string id, string title) => new(IdentifierNamespace.IgdbGame, id, title, null, null);

    [Fact]
    public void MergePickerCandidates_OrdersExactTitlesThenTopRelevanceThenNewest_DedupedAndCapped()
    {
        var relevance = new[] { C("1", "Foo Legends"), C("2", "Foo"), C("3", "Foo 2"), C("4", "Foo 3"), C("5", "Old Foo") };
        var newest = new[] { C("9", "Foo Reborn"), C("3", "Foo 2"), C("8", "Foo Nova") };

        var merged = IgdbCoverArtProvider.MergePickerCandidates("foo", relevance, newest);

        Assert.Equal(new[] { "2", "1", "3", "9", "8", "4", "5" }, merged.Select(c => c.Id));      // exact "Foo"; IGDB's top 3; newest; the rest
        Assert.Equal(3, IgdbCoverArtProvider.MergePickerCandidates("foo", relevance, newest, cap: 3).Count);
    }

    [Fact]
    public void MergePickerCandidates_WithNothingFromEitherQuery_IsEmpty_AndWithOnlyOne_IsThatOne()
    {
        Assert.Empty(IgdbCoverArtProvider.MergePickerCandidates("foo", [], []));
        Assert.Equal(new[] { "7" }, IgdbCoverArtProvider.MergePickerCandidates("foo", [], [C("7", "Foo")]).Select(c => c.Id));
        Assert.Equal(new[] { "7" }, IgdbCoverArtProvider.MergePickerCandidates("", [C("7", "Foo")], []).Select(c => c.Id));
    }

    [Fact]
    public void IgdbPicker_ListsCoversByGameId_AndSkipsUnreadableEntries()
    {
        var provider = Igdb(IgdbApi((_, _) => Ok("""[{"image_id":"co1"},{"image_id":""},{"x":1},{"image_id":"co2"}]""")));

        var choices = provider.ListCoversForId("7", CancellationToken.None);

        Assert.Equal(new[] { "co1", "co2" }, choices.Select(c => c.Ref));
        Assert.All(choices, c => Assert.StartsWith("https://images.igdb.com/", c.ImageUrl));
        Assert.Empty(provider.ListCoversForId("nope", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://evil.example.com/a.jpg")]
    [InlineData("http://images.igdb.com/a.jpg")]
    [InlineData("https://images.igdb.com.evil.example.com/a.jpg")]
    [InlineData("not a url")]
    public void IgdbPicker_RefusesAnyImageHostOtherThanIgdbs_WithoutARequest(string url)
    {
        var handler = IgdbApi((_, _) => Ok("[]"), _ => Bytes(TestImages.Png()));

        Assert.Null(Igdb(handler).DownloadPickerImage(url, CancellationToken.None));
        Assert.Equal(0, handler.ImageCalls);
    }

    [Fact]
    public void IgdbPicker_DownloadsFromIgdbsOwnHost()
    {
        var handler = IgdbApi((_, _) => Ok("[]"), _ => Bytes(TestImages.Png()));

        Assert.NotNull(Igdb(handler).DownloadPickerImage("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg", CancellationToken.None));
    }

    // ---- SteamGridDB ------------------------------------------------------------------------------------------------------

    private static SteamGridDbCoverArtProvider Sgdb(FuncHttpHandler handler) => new("key") { HttpHandlerOverrideForTest = handler };

    private static FuncHttpHandler SgdbApi(Func<string, HttpResponseMessage> respond) => new((request, _) => respond(request.RequestUri!.ToString()));

    [Fact]
    public void SgdbTitle_OneExactMatch_IsFound_AmbiguousIsAmbiguous_NothingIsNoMatch()
    {
        Assert.Equal(("9", CatalogStatus.Match),
            SgdbResult(Sgdb(SgdbApi(_ => Ok("""{"data":[{"id":9,"name":"Foo"},{"id":10,"name":"Foo 2"}]}""")))));
        Assert.Equal((null, CatalogStatus.Ambiguous),
            SgdbResult(Sgdb(SgdbApi(_ => Ok("""{"data":[{"id":9,"name":"Foo"},{"id":10,"name":"Foo"}]}""")))));
        Assert.Equal((null, CatalogStatus.NoMatch),
            SgdbResult(Sgdb(SgdbApi(_ => Ok("""{"data":[]}""")))));

        static (string?, CatalogStatus) SgdbResult(SteamGridDbCoverArtProvider p)
        {
            var r = p.SearchByTitleForIdentity("Foo", CancellationToken.None);
            return (r.Match?.Id, r.Status);
        }
    }

    [Fact]
    public void SgdbTitle_ServiceFailureAndMalformedResponses_AreUnavailable_AndCancellationEscapes()
    {
        Assert.Equal(CatalogStatus.Unavailable,
            Sgdb(SgdbApi(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))).SearchByTitleForIdentity("Foo", CancellationToken.None).Status);
        Assert.Equal(CatalogStatus.Unavailable,
            Sgdb(SgdbApi(_ => Ok("""["not","an","object"]"""))).SearchByTitleForIdentity("Foo", CancellationToken.None).Status);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Sgdb(SgdbApi(_ => Ok("""{"data":[]}"""))).SearchByTitleForIdentity("Foo", cts.Token));
    }

    [Fact]
    public void SgdbTitle_ACompactedTitle_IsRetriedWithItsWordsSplit_ButAnAmbiguousFirstResultIsNot()
    {
        var urls = new List<string>();
        var provider = Sgdb(SgdbApi(url =>
        {
            urls.Add(url);
            return url.EndsWith("AWayOut", StringComparison.Ordinal) ? Ok("""{"data":[]}""") : Ok("""{"data":[{"id":5,"name":"A Way Out"}]}""");
        }));

        var found = provider.SearchByTitleForIdentity("AWayOut", CancellationToken.None);

        Assert.Equal(("5", "A Way Out"), (found.Match!.Id, found.Match!.Title));
        Assert.Equal(2, urls.Count);

        var ambiguousUrls = new List<string>();
        var ambiguous = Sgdb(SgdbApi(url =>
        {
            ambiguousUrls.Add(url);
            return Ok("""{"data":[{"id":5,"name":"AWayOut"},{"id":6,"name":"AWayOut"}]}""");
        }));
        Assert.Equal(CatalogStatus.Ambiguous, ambiguous.SearchByTitleForIdentity("AWayOut", CancellationToken.None).Status);
        Assert.Single(ambiguousUrls); // a settled fact about the identity, never retried into a "better" answer
    }

    [Fact]
    public void SgdbCover_IsFetchedByIdOnce_ThenServedFromTheCache()
    {
        var cache = Dir();
        var handler = SgdbApi(url => url.Contains("/grids/game/", StringComparison.Ordinal)
            ? Ok("""{"data":[{"id":1,"url":"https://cdn2.steamgriddb.com/grid/a.png"}]}""")
            : Bytes(TestImages.Png(60, 90)));
        var provider = Sgdb(handler);

        var first = provider.FetchCoverForId("9", "Foo", cache, CancellationToken.None);
        var calls = handler.Calls;
        var second = provider.FetchCoverForId("9", "Foo", cache, CancellationToken.None);

        Assert.Equal((CoverLookupStatus.Resolved, false, true), (first.Status, first.FromCache, second.FromCache));
        Assert.Equal(calls, handler.Calls);
        Assert.True(File.Exists(IdKeyedCoverCache.PathFor(cache, "9", SteamGridDbCoverArtProvider.IdCacheVersion)));
        // the id-keyed series is a fresh, separate namespace from the legacy `{gameId}-v13.png` files
        Assert.DoesNotContain("-v13", Path.GetFileName(IdKeyedCoverCache.PathFor(cache, "9", SteamGridDbCoverArtProvider.IdCacheVersion)));
    }

    [Fact]
    public void SgdbCover_NoGrid_IsNoUsableArt_AndAServerErrorIsUnavailable()
    {
        var cache = Dir();
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt,
            Sgdb(SgdbApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).FetchCoverForId("9", "Foo", cache, CancellationToken.None).Status);
        Assert.Equal(CoverLookupStatus.Unavailable,
            Sgdb(SgdbApi(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).FetchCoverForId("9", "Foo", cache, CancellationToken.None).Status);
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public void SgdbPicker_ListsCandidates_WithTheirTags_AndSkipsUnreadableOnes()
    {
        var provider = Sgdb(SgdbApi(_ => Ok("""{"data":[{"id":1,"name":"Foo","types":["steam","gog"]},{"id":"x","name":"Bad"},{"id":3},{"id":4,"name":"Bar"}]}""")));

        var list = provider.SearchCandidatesForPicker("Foo", CancellationToken.None);

        Assert.Equal(new[] { "1", "4" }, list.Select(c => c.Id));
        Assert.Equal("steam, gog", list[0].Detail);
        Assert.Null(list[1].Detail);
    }

    [Fact]
    public void SgdbPicker_OnlyListsGridsHostedBySteamGridDb_OverHttps()
    {
        var provider = Sgdb(SgdbApi(_ => Ok("""
            {"data":[
              {"id":1,"url":"https://cdn2.steamgriddb.com/grid/a.png","thumb":"https://cdn2.steamgriddb.com/thumb/a.png"},
              {"id":2,"url":"https://evil.example.com/b.png"},
              {"id":3,"url":"http://cdn2.steamgriddb.com/grid/c.png"},
              {"id":4,"url":"https://steamgriddb.com.evil.example.com/d.png"},
              {"id":5,"url":"https://cdn2.steamgriddb.com/grid/e.png","thumb":"https://evil.example.com/t.png"}]}
            """)));

        var choices = provider.ListCoversForId("9", CancellationToken.None);

        Assert.Equal(new[] { "1", "5" }, choices.Select(c => c.Ref));
        Assert.Equal("https://cdn2.steamgriddb.com/grid/e.png", choices[1].ThumbnailUrl); // a foreign thumb falls back to the image itself
    }

    [Theory]
    [InlineData("https://evil.example.com/a.png")]
    [InlineData("http://cdn2.steamgriddb.com/a.png")]
    [InlineData("https://steamgriddb.com.evil.example.com/a.png")]
    public void SgdbPicker_RefusesForeignImageHosts_WithoutARequest(string url)
    {
        var handler = SgdbApi(_ => Bytes(TestImages.Png()));

        Assert.Null(Sgdb(handler).DownloadPickerImage(url, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void SgdbPicker_DownloadsFromItsOwnHost_AndAFailureIsNullNotAnException()
    {
        Assert.NotNull(Sgdb(SgdbApi(_ => Bytes(TestImages.Png()))).DownloadPickerImage("https://cdn2.steamgriddb.com/a.png", CancellationToken.None));
        Assert.Null(Sgdb(SgdbApi(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).DownloadPickerImage("https://cdn2.steamgriddb.com/a.png", CancellationToken.None));
    }
}
