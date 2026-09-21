using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>Names the xUnit collection that serializes every test class touching IgdbCoverArtProvider's
/// intentionally-static state (the shared token cache and the shared rate-limit clock). Classes in one
/// collection never run in parallel with each other; without it, two classes could interleave resets and
/// assertions on that shared state.</summary>
public static class IgdbStaticStateCollection
{
    public const string Name = "IgdbProviderStaticState";
}

/// <summary>Fixtures below are SANITIZED excerpts of real IGDB /v4/games responses captured during this
/// integration's proof-of-concept investigation (no tokens/secrets - those never appear in a response
/// body anyway) - kept as provider-independent regression tests, per explicit review instruction, because
/// they demonstrate real, confirmed search-ranking behavior, not hypothetical edge cases:
///  - EA Sports FC 27: the base game plus its Ultimate/Ultimate Plus editions as SEPARATE entries.
///  - Call of Duty: Black Ops 7: the base game plus its own Season 1 DLC plus the unrelated 2010 original.
///  - Minecraft: "Minecraft" (id 135400) vs "Minecraft: Java Edition" (id 121) - a REAL parent_game
///    relationship (135400 -> 121, exactly as IGDB actually returns it) that must NOT be treated as
///    "unsuitable DLC" (see SelectMatchedGame's own remarks, and the dedicated fixture-pinning test below).
///  - Apex Legends: the base game plus several seasonal update entries.
///  - A Way Out: the REAL, confirmed finding that its correct result ranked EIGHTH in IGDB's own
///    search, behind seven unrelated "Way ..." titles - proof IGDB's ranking cannot be trusted over
///    SteamGridDbCoverArtProvider.IsConfidentMatch's exact-match discipline, applied here identically.
///
/// Every test here builds its OWN provider instance with its OWN per-instance transport fake (no static
/// handler switch), its own temp cache directory, and starts from a reset of the two shared static
/// caches - and the class is in IgdbStaticStateCollection so no other class runs concurrently with it.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class IgdbCoverArtProviderTests : IDisposable
{
    public IgdbCoverArtProviderTests()
    {
        IgdbCoverArtProvider.ResetTokenCacheForTest();
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    private readonly List<string> _cacheDirs = new();

    public void Dispose()
    {
        IgdbCoverArtProvider.ResetTokenCacheForTest();
        IgdbCoverArtProvider.ResetRateLimitForTest();
        foreach (var dir in _cacheDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IgdbCache-" + Guid.NewGuid());
        _cacheDirs.Add(dir);
        return dir;
    }

    // ---- Fixtures (sanitized real IGDB responses) --------------------------------------------------

    private const string Fc27Response = """
        [
          {"id":408819,"name":"EA Sports FC 27"},
          {"id":410902,"name":"EA Sports FC 27: Ultimate Edition"},
          {"id":411107,"name":"EA Sports FC 27: Ultimate Plus Edition"}
        ]
        """;

    private const string Bo7Response = """
        [
          {"id":348220,"name":"Call of Duty: Black Ops 7"},
          {"id":378032,"name":"Call of Duty: Black Ops 7 - Season 1"},
          {"id":545,"name":"Call of Duty: Black Ops"}
        ]
        """;

    // Real relationship, exactly as IGDB returns it: id 135400 "Minecraft" carries "parent_game":121,
    // pointing at "Minecraft: Java Edition".
    private const string MinecraftResponse = """
        [
          {"id":135400,"name":"Minecraft","parent_game":121},
          {"id":121,"name":"Minecraft: Java Edition"},
          {"id":254125,"name":"Minecraft: Frozen","parent_game":135400},
          {"id":240149,"name":"Minecraft Tower Defence"}
        ]
        """;

    private const string ApexLegendsResponse = """
        [
          {"id":114795,"name":"Apex Legends"},
          {"id":146328,"name":"Apex Legends: Legacy","parent_game":114795},
          {"id":176896,"name":"Apex Legends: Escape","parent_game":114795},
          {"id":118210,"name":"Apex Legends Mobile"},
          {"id":210618,"name":"Apex Legends: Hunted","parent_game":114795}
        ]
        """;

    // The real, confirmed A Way Out result set - correct answer (id 36897) at position 8 of 9.
    private const string AWayOutResponse = """
        [
          {"id":136577,"name":"Milky Way Prince: The Vampire Star"},
          {"id":132672,"name":"Way of Boy: Another Way"},
          {"id":277702,"name":"Way Out"},
          {"id":84080,"name":"Way Out"},
          {"id":95624,"name":"Way Home"},
          {"id":58744,"name":"Way of Redemption"},
          {"id":193961,"name":"The Way Home"},
          {"id":36897,"name":"A Way Out"},
          {"id":13045,"name":"The Way of the Exploding Fist"}
        ]
        """;

    private const string AWayOutSingleMatch = """[{"id":36897,"name":"A Way Out"}]""";

    // ---- SelectMatchedGame: the five real proof-of-concept examples ---------------------------------

    [Fact]
    public void SelectMatchedGame_Fc27_MatchesTheBaseGame_NotEitherEdition()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(Fc27Response, "EA Sports FC 27", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(408819, matched?.Id);
        Assert.Equal("EA Sports FC 27", matched?.Title);
    }

    [Fact]
    public void SelectMatchedGame_Bo7_MatchesTheBaseGame_NotTheSeasonDlcOrTheOriginal()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(Bo7Response, "Call of Duty: Black Ops 7", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(348220, matched?.Id);
        Assert.Equal("Call of Duty: Black Ops 7", matched?.Title);
    }

    [Fact]
    public void SelectMatchedGame_Minecraft_MatchesTheBedrockEntry_DespiteItHavingAParentGame()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(MinecraftResponse, "Minecraft", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(135400, matched?.Id);
        Assert.Equal("Minecraft", matched?.Title);
    }

    [Fact]
    public void MinecraftFixture_ContainsTheRealParentGameField()
    {
        // The fixture must actually carry the relationship it claims to guard: without "parent_game" in
        // the JSON, a filter rejecting any candidate that HAS a parent_game could never fail against it.
        using var doc = JsonDocument.Parse(MinecraftResponse);
        var bedrockEntry = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == 135400);

        Assert.True(bedrockEntry.TryGetProperty("parent_game", out var parentGame));
        Assert.Equal(121, parentGame.GetInt32());
    }

    [Fact]
    public void SelectMatchedGame_ApexLegends_MatchesTheBaseGame_NotAnySeasonalUpdate()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(ApexLegendsResponse, "Apex Legends", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(114795, matched?.Id);
        Assert.Equal("Apex Legends", matched?.Title);
    }

    [Fact]
    public void SelectMatchedGame_AWayOut_FindsTheCorrectGame_DespiteRankingEighthOfNine()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(AWayOutResponse, "A Way Out", out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(36897, matched?.Id);
        Assert.Equal("A Way Out", matched?.Title);
    }

    [Fact]
    public void SelectMatchedGame_TwoCandidatesBothExactMatch_IsAmbiguous_NeitherIsPicked()
    {
        const string json = """[{"id":1,"name":"Kingdom"},{"id":2,"name":"Kingdom"}]""";

        var matched = IgdbCoverArtProvider.SelectMatchedGame(json, "Kingdom", out var ambiguous);

        Assert.Null(matched);
        Assert.True(ambiguous); // ambiguous, not "no match" - a caller must not treat these the same way
    }

    [Fact]
    public void SelectMatchedGame_NoCandidatesMatch_ReturnsNull_NotAmbiguous()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame(Fc27Response, "Some Entirely Different Game", out var ambiguous);
        Assert.Null(matched);
        Assert.False(ambiguous);
    }

    [Fact]
    public void SelectMatchedGame_MalformedJson_ThrowsJsonException() // the provider boundary converts this to Unavailable
    {
        Assert.ThrowsAny<JsonException>(() => IgdbCoverArtProvider.SelectMatchedGame("not json", "Anything", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data": []}""")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    public void SelectMatchedGame_ValidJsonOfTheWrongShape_Throws_NeverReadAsNoMatch(string body)
    {
        // Valid JSON that is not an array says nothing about the catalog. It used to return null (= "no match").
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.SelectMatchedGame(body, "Anything", out _));
    }

    [Fact]
    public void SelectMatchedGame_AValidEmptyArray_IsNoMatch_NotAnError()
    {
        var matched = IgdbCoverArtProvider.SelectMatchedGame("[]", "Anything", out var ambiguous);

        Assert.Null(matched);
        Assert.False(ambiguous);
    }

    [Theory]
    [InlineData("""{"id":"invalid","name":"Apex Legends"}""")]
    [InlineData("""{"id":0,"name":"Apex Legends"}""")]
    [InlineData("""{"id":-9,"name":"Apex Legends"}""")]
    [InlineData("""{"name":"Apex Legends"}""")]
    [InlineData("""{"id":2}""")]
    [InlineData("123")]
    public void SelectMatchedGame_AnUnreadableCandidateThatCouldBeASecondMatch_FailsClosed(string unreadable)
    {
        // The audited counterexample: a readable exact match beside an unreadable candidate must not resolve.
        var json = $$"""[{"id":111,"name":"Apex Legends"},{{unreadable}}]""";

        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.SelectMatchedGame(json, "Apex Legends", out _));
    }

    [Fact]
    public void SelectMatchedGame_AnUnreadableCandidateWithAProvablyDifferentName_DoesNotBlockAMatch()
    {
        var json = """[{"id":"invalid","name":"Something Else"},{"id":111,"name":"Apex Legends"}]""";

        var matched = IgdbCoverArtProvider.SelectMatchedGame(json, "Apex Legends", out var ambiguous);

        Assert.Equal(111, matched?.Id);
        Assert.False(ambiguous);
    }

    [Fact]
    public void SelectCoverImageUrl_AValidCover_BuildsTheCdnUrl()
    {
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg",
            IgdbCoverArtProvider.SelectCoverImageUrl("""[{"id":1,"image_id":"co1abc"}]"""));
    }

    [Fact]
    public void SelectCoverImageUrl_AValidEmptyListing_IsNull_NotAnError()
    {
        Assert.Null(IgdbCoverArtProvider.SelectCoverImageUrl("[]"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data": []}""")]
    [InlineData("null")]
    [InlineData("[123]")]
    [InlineData("[null]")]
    [InlineData("""[{"id":1}]""")]
    [InlineData("""[{"id":1,"image_id":null}]""")]
    [InlineData("""[{"id":1,"image_id":7}]""")]
    [InlineData("""[{"id":1,"image_id":""}]""")]
    [InlineData("""[{"id":1,"image_id":"  "}]""")]
    public void SelectCoverImageUrl_AnUnreadableListing_Throws_NeverReadAsNoCover(string body)
    {
        Assert.Throws<InvalidDataException>(() => IgdbCoverArtProvider.SelectCoverImageUrl(body));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static GameEntry MakeGame(string name = "A Way Out", GameSource source = GameSource.Manual, string id = "manual-igdb-test") => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test",
        Source = source,
    };

    private static byte[] MakeValidPngBytes(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static (BitmapImage? Image, CoverLookupStatus Status, IgdbCoverArtProvider.MatchedGame? Matched, bool FromCache) Lookup(
        IgdbCoverArtProvider provider, GameEntry game, string cacheDir, CancellationToken ct = default)
    {
        var image = provider.GetCoverArt(game, out var fromCache, out var matched, out var status, cacheDir, ct);
        return (image, status, matched, fromCache);
    }

    private static HttpResponseMessage RateLimited(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    // ---- GetCoverArt: every distinct outcome -----------------------------------------------------

    [Fact]
    public void GetCoverArt_NoConfidentMatch_IsNoMatch()
    {
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", SearchGameIdOverride = _ => null };

        var (image, status, matched, fromCache) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.NoMatch, status);
        Assert.Null(matched);
        Assert.False(fromCache);
    }

    [Fact]
    public void GetCoverArt_KnownUmbrellaProductName_SkipsEntirely_IsAmbiguous()
    {
        // A bare "Call of Duty" (the real detected name, trademark symbol and all) must never reach a
        // search. SearchGameIdOverride throws to prove the guard short-circuits BEFORE any search runs.
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => throw new InvalidOperationException("must not search at all for an umbrella name"),
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame("Call of Duty®", GameSource.Xbox), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.Ambiguous, status);
        Assert.Null(matched);
    }

    [Fact]
    public void GetCoverArt_AmbiguousSearchResult_IsAmbiguous_NotNoMatch()
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => """[{"id":1,"name":"Kingdom"},{"id":2,"name":"Kingdom"}]""",
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame("Kingdom"), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.Ambiguous, status);
        Assert.Null(matched);
    }

    [Fact]
    public void GetCoverArt_IdentityMatchedButNoCoverAvailable_IsIdentifiedWithoutUsableArt()
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(999, "A Way Out"),
            FetchCoverImageBytesOverride = _ => null,
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Null(matched);
    }

    [Fact]
    public void GetCoverArt_GenuineFetchThenSecondCall_IsARealCacheHit_ReportingTheSameEvidence()
    {
        var cacheDir = NewCacheDir();
        var pngBytes = MakeValidPngBytes();
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(36897, "A Way Out"),
            FetchCoverImageBytesOverride = _ => pngBytes,
        };
        var game = MakeGame();

        var first = Lookup(provider, game, cacheDir);
        var second = Lookup(provider, game, cacheDir);

        Assert.NotNull(first.Image);
        Assert.Equal(CoverLookupStatus.Resolved, first.Status);
        Assert.False(first.FromCache);
        Assert.Equal(36897, first.Matched?.Id);

        Assert.NotNull(second.Image);
        Assert.Equal(CoverLookupStatus.Resolved, second.Status);
        Assert.True(second.FromCache);
        Assert.Equal(36897, second.Matched?.Id);
        Assert.Equal("A Way Out", second.Matched?.Title);
    }

    [Fact]
    public void GetCoverArt_CacheHit_SidecarResolvedForADifferentIdentity_TreatedAsStale_CacheIsInvalidated()
    {
        var cacheDir = NewCacheDir();
        var game = MakeGame("A Way Out");
        var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{IgdbCoverArtProvider.CacheVersionForTest}.png");
        var metaPath = cachePath + ".meta.json";
        var pngBytes = MakeValidPngBytes();

        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(cachePath, pngBytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pngBytes));
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new { Id = 1, Title = "Something Else", SearchedName = "Something Else", ImageSha256 = hash }));

        var searchCalls = 0;
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => { searchCalls++; return null; },
        };

        var (image, status, matched, fromCache) = Lookup(provider, game, cacheDir);

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.NoMatch, status);
        Assert.Null(matched);
        Assert.False(fromCache);
        Assert.Equal(1, searchCalls);
        Assert.False(File.Exists(cachePath));
        Assert.False(File.Exists(metaPath));
    }

    [Fact]
    public void GetCoverArt_ImageDimensionsExceedTheLimit_IsRejected_AsIdentifiedWithoutUsableArt_NotCached()
    {
        // A 9000x1 PNG is tiny in bytes (far under the 20MB cap) but exceeds MaxDimensionPixels (8000):
        // only a DIMENSION-aware check catches it. The game WAS identified, so the status is
        // IdentifiedWithoutUsableArt - not NoMatch.
        var cacheDir = NewCacheDir();
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(1, "Oversized"),
            FetchCoverImageBytesOverride = _ => MakeValidPngBytes(width: 9000, height: 1),
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame(), cacheDir);

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.Null(matched);
        Assert.False(File.Exists(Path.Combine(cacheDir, $"manual-igdb-test-v{IgdbCoverArtProvider.CacheVersionForTest}.png")));
    }

    [Fact]
    public void GetCoverArt_DownloadedBytesAreNotAnImage_IsIdentifiedWithoutUsableArt()
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(1, "A Way Out"),
            FetchCoverImageBytesOverride = _ => [1, 2, 3, 4, 5],
        };

        var (image, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
    }

    // ---- Unavailable: failures are NOT "no confident match" ----------------------------------------

    [Fact]
    public void GetCoverArt_TokenRequestFails_IsUnavailable_NotNoMatch_AndDoesNotThrow()
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            TokenRequestOverride = (_, _) => throw new HttpRequestException("token endpoint returned 400"),
            SearchRequestOverride = _ => "[]",
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Null(matched);
    }

    [Fact]
    public void GetCoverArt_ApiReturnsServerError_IsUnavailable()
    {
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
    }

    [Fact]
    public void GetCoverArt_UnexpectedExceptionAnywhereInTheLookup_IsUnavailable_NotAnEscapingException()
    {
        // The provider boundary: an exception type nobody enumerated (here KeyNotFoundException) must not
        // escape to SafeApplyCoverArt's outer net, which would skip every fallback provider.
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => throw new KeyNotFoundException("unexpected shape"),
        };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
    }

    [Fact]
    public void GetCoverArt_ImageDownloadStallsAfterHeaders_TimesOutPromptly_AsUnavailable()
    {
        // Proves the cancellable, timeout-bound body read (a synchronous Read() could never be
        // interrupted) AND that a stall is Unavailable - it says nothing about whether the cover exists,
        // so it must not be reported as IdentifiedWithoutUsableArt.
        var handler = new FakeIgdbHandler { OnImage = (_, _) => FakeIgdbHandler.StallingBody() };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => AWayOutSingleMatch,
            ImageDownloadTimeoutOverrideForTest = TimeSpan.FromMilliseconds(200),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (image, status, matched, _) = Lookup(provider, MakeGame(), NewCacheDir());
        sw.Stop();

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Null(matched);
        Assert.Equal(1, handler.ImageCalls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected a prompt timeout, took {sw.Elapsed}.");
    }

    [Fact]
    public void GetCoverArt_NeverEndingOversizedBody_AbortsAtTheByteCap_AsIdentifiedWithoutUsableArt()
    {
        var handler = new FakeIgdbHandler { OnImage = (_, _) => FakeIgdbHandler.EndlessBody() };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => AWayOutSingleMatch,
            ImageDownloadTimeoutOverrideForTest = TimeSpan.FromSeconds(30), // must abort on the BYTE cap, not this
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (image, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());
        sw.Stop();

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Expected the byte cap to abort promptly, took {sw.Elapsed}.");
    }

    [Fact]
    public void GetCoverArt_ImageMissingOnTheCdn_IsIdentifiedWithoutUsableArt()
    {
        var handler = new FakeIgdbHandler(); // default OnImage: 404
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => AWayOutSingleMatch,
        };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, status);
    }

    [Fact]
    public void GetCoverArt_ImageServerError_IsUnavailable_NotNoCover()
    {
        var handler = new FakeIgdbHandler { OnImage = (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => AWayOutSingleMatch,
        };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
    }

    // ---- Caller cancellation: propagated from every stage, never converted to "no artwork" ---------

    [Fact]
    public void GetCoverArt_AlreadyCancelled_Throws_AndSendsNothing()
    {
        var handler = new FakeIgdbHandler();
        var provider = new IgdbCoverArtProvider("id", "secret") { HttpHandlerOverrideForTest = handler };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));

        Assert.Equal(0, handler.TokenCalls);
        Assert.Equal(0, handler.ApiCalls);
        Assert.Equal(0, handler.ImageCalls);
    }

    [Fact]
    public void GetCoverArt_CancelledDuringImageDownload_ThrowsInsteadOfReturningNull()
    {
        var handler = new FakeIgdbHandler { OnImage = (_, _) => FakeIgdbHandler.StallingBody() };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => AWayOutSingleMatch,
            ImageDownloadTimeoutOverrideForTest = TimeSpan.FromSeconds(30), // the CALLER's cancel must be what ends it
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation should end the download promptly, took {sw.Elapsed}.");
    }

    [Fact]
    public void GetCoverArt_CancelledDuringTokenRequest_Throws()
    {
        // Proves the caller's token actually reaches the token POST (it used to receive none at all).
        var handler = new FakeIgdbHandler { OnToken = (_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return FakeIgdbHandler.Json("{}"); } };
        var provider = new IgdbCoverArtProvider("id", "secret") { HttpHandlerOverrideForTest = handler };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));
        sw.Stop();

        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal(0, handler.ApiCalls);
        // Ended by OUR token, not by HttpClient's own 8s timeout (by which point the token would be
        // cancelled anyway and the exception rethrown - so without this bound the test could not tell
        // "the token reached the request" from "the request timed out on its own").
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Cancellation should end the token request promptly, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task GetCoverArt_CancelledWhileWaitingBehindAnotherCallersTokenRequest_EndsWhileThatRequestIsStillBlocked()
    {
        // The single-request cancellation tests can't see this: here the cancelled caller never SENDS a
        // request at all - it is parked waiting for the shared token-fetch gate that another caller (A)
        // holds inside its own in-flight token request (a Reset overlapping a scan). With a plain lock the
        // wait ignored B's token, so B stayed blocked until A finished.
        using var aEnteredTokenRequest = new ManualResetEventSlim(false);
        using var releaseA = new ManualResetEventSlim(false);
        var bTokenRequests = 0;

        var providerA = new IgdbCoverArtProvider("id", "secret")
        {
            TokenRequestOverride = (_, _) =>
            {
                aEnteredTokenRequest.Set();
                releaseA.Wait(TimeSpan.FromSeconds(30)); // hard cap so a failing test can't hang the run
                return ValidTokenJson("tok-a");
            },
            SearchRequestOverride = _ => "[]",
        };
        var providerB = new IgdbCoverArtProvider("id", "secret")
        {
            TokenRequestOverride = (_, _) => { Interlocked.Increment(ref bTokenRequests); return ValidTokenJson("tok-b"); },
            SearchRequestOverride = _ => "[]",
        };

        var lookupA = Task.Run(() => Lookup(providerA, MakeGame(), NewCacheDir()));
        try
        {
            Assert.True(aEnteredTokenRequest.Wait(TimeSpan.FromSeconds(10)), "A never reached its token request.");

            using var cts = new CancellationTokenSource();
            var lookupB = Task.Run(() => Lookup(providerB, MakeGame(), NewCacheDir(), cts.Token));
            Thread.Sleep(200); // let B park on the gate
            Assert.False(lookupB.IsCompleted, "B should be waiting behind A's token request.");

            cts.Cancel();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => lookupB);
            sw.Stop();

            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.False(lookupA.IsCompleted, "A must still be blocked when B ends - otherwise this proves nothing.");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"B's cancellation should not wait for A, took {sw.Elapsed}.");
            Assert.Equal(0, bTokenRequests);
        }
        finally
        {
            releaseA.Set();
            await lookupA; // A completes normally; also proves the gate was not left broken
        }

        // The gate is usable again after B's aborted wait and A's release: a fresh caller isn't locked out.
        var after = new IgdbCoverArtProvider("other-id", "other-secret")
        {
            TokenRequestOverride = (_, _) => ValidTokenJson("tok-after"),
            SearchRequestOverride = _ => "[]",
        };
        Assert.Equal(CoverLookupStatus.NoMatch, Lookup(after, MakeGame(), NewCacheDir()).Status);
    }

    [Fact]
    public async Task GetCoverArt_ConcurrentCallersWithTheSameCredentials_ShareOneTokenRequest()
    {
        // Guards the re-check after gate acquisition: the second caller must reuse the token the first
        // just cached rather than send its own request.
        using var aEntered = new ManualResetEventSlim(false);
        using var releaseA = new ManualResetEventSlim(false);
        var requests = 0;

        Func<string, string, string> seam = (_, _) =>
        {
            var n = Interlocked.Increment(ref requests);
            if (n == 1)
            {
                aEntered.Set();
                releaseA.Wait(TimeSpan.FromSeconds(30));
            }
            return ValidTokenJson("shared");
        };

        var lookupA = Task.Run(() => Lookup(new IgdbCoverArtProvider("id", "secret") { TokenRequestOverride = seam, SearchRequestOverride = _ => "[]" }, MakeGame(), NewCacheDir()));
        try
        {
            Assert.True(aEntered.Wait(TimeSpan.FromSeconds(10)));
            var lookupB = Task.Run(() => Lookup(new IgdbCoverArtProvider("id", "secret") { TokenRequestOverride = seam, SearchRequestOverride = _ => "[]" }, MakeGame(), NewCacheDir()));
            Thread.Sleep(200);
            Assert.False(lookupB.IsCompleted);

            releaseA.Set();
            Assert.Equal(CoverLookupStatus.NoMatch, (await lookupB).Status);
        }
        finally
        {
            releaseA.Set();
            await lookupA;
        }

        Assert.Equal(1, requests);
    }

    [Fact]
    public void GetCoverArt_CancelledDuringApiRequest_Throws()
    {
        var handler = new FakeIgdbHandler { OnApi = (_, _, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return FakeIgdbHandler.Json("[]"); } };
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));
        sw.Stop();

        Assert.Equal(1, handler.ApiCalls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Cancellation should end the API request promptly, took {sw.Elapsed}.");
    }

    [Fact]
    public void GetCoverArt_CancelledWhileWaitingForTheLocalRateLimitSlot_ThrowsWithoutSendingTheRequest()
    {
        // The throttle used to Thread.Sleep: it neither observed cancellation nor stopped the request that
        // followed it. Primed to a 30s wait, so a non-cancellable wait would make this test take 30s and
        // then SEND the request - both visible.
        var handler = new FakeIgdbHandler();
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };
        IgdbCoverArtProvider.PrimeRateLimitForTest(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));
        sw.Stop();

        Assert.Equal(0, handler.ApiCalls);
        // Cancelled at 50ms against a 30s wait. The request that follows a non-cancellable wait would
        // still be refused (Send observes the cancelled token), so the wait's DURATION is what tells the
        // two implementations apart - hence a tight bound, well under any sleep-based wait.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"The wait should end on cancellation, took {sw.Elapsed}.");
    }

    // ---- Query generation --------------------------------------------------------------------------

    [Fact]
    public void SearchRequestOverride_ReceivesTheApicalypseQueryText_ContainingTheGameName()
    {
        string? seenQuery = null;
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = q => { seenQuery = q; return Fc27Response; },
            FetchCoverImageBytesOverride = _ => MakeValidPngBytes(),
        };

        var (_, status, matched, _) = Lookup(provider, MakeGame("EA Sports FC 27"), NewCacheDir());

        Assert.NotNull(seenQuery);
        Assert.Contains("EA Sports FC 27", seenQuery);
        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.Equal(408819, matched?.Id);
    }

    [Fact]
    public void SearchRequestOverride_UsesCatalogNameOverRawName_WhenBothAreSet()
    {
        string? seenQuery = null;
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = q => { seenQuery = q; return "[]"; },
        };
        var game = new GameEntry
        {
            Id = "manual-igdb-catalogname-test", Name = "Apex", CatalogName = "Apex Legends",
            ExecutablePath = @"C:\Games\Test\game.exe", InstallDir = @"C:\Games\Test", Source = GameSource.Ea,
        };

        Lookup(provider, game, NewCacheDir());

        Assert.Contains("Apex Legends", seenQuery);
        Assert.DoesNotContain("\"Apex\"", seenQuery);
    }

    // ---- Token caching -------------------------------------------------------------------------------

    private static string ValidTokenJson(string token) => $$"""{"access_token":"{{token}}","expires_in":5000000}""";

    [Fact]
    public void GetAccessToken_CachesAcrossCalls_DoesNotReauthenticateForEveryRequest()
    {
        var tokenRequests = 0;
        Func<string, string, string> tokenSeam = (_, _) => { tokenRequests++; return ValidTokenJson("tok-a"); };
        var cacheDir = NewCacheDir();

        Lookup(new IgdbCoverArtProvider("client-a", "secret-a") { TokenRequestOverride = tokenSeam, SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);
        // A second, distinct instance for the SAME credentials - the cache is static, not per-instance,
        // because CoverArtService.Apply constructs a new provider per game per scan.
        Lookup(new IgdbCoverArtProvider("client-a", "secret-a") { TokenRequestOverride = tokenSeam, SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);

        Assert.Equal(1, tokenRequests);
    }

    [Fact]
    public void GetAccessToken_DifferentClientId_ReauthenticatesRatherThanReusingTheOldToken()
    {
        var cacheDir = NewCacheDir();
        Lookup(new IgdbCoverArtProvider("client-a", "secret-a") { TokenRequestOverride = (_, _) => ValidTokenJson("tok-a"), SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);

        var requestsForB = 0;
        Lookup(new IgdbCoverArtProvider("client-b", "secret-b") { TokenRequestOverride = (_, _) => { requestsForB++; return ValidTokenJson("tok-b"); }, SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);

        Assert.Equal(1, requestsForB);
    }

    [Fact]
    public void GetAccessToken_SameClientId_DifferentSecret_ReauthenticatesRatherThanReusingTheOldTokensCacheEntry()
    {
        var cacheDir = NewCacheDir();
        Lookup(new IgdbCoverArtProvider("client-a", "secret-1") { TokenRequestOverride = (_, _) => ValidTokenJson("tok-1"), SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);

        var requestsForNewSecret = 0;
        Lookup(new IgdbCoverArtProvider("client-a", "secret-2") { TokenRequestOverride = (_, _) => { requestsForNewSecret++; return ValidTokenJson("tok-2"); }, SearchRequestOverride = _ => "[]" }, MakeGame(), cacheDir);

        Assert.Equal(1, requestsForNewSecret);
    }

    // ---- Token response validation ---------------------------------------------------------------------

    [Theory]
    [InlineData("{}")]                                                       // no access_token at all
    [InlineData("[]")]                                                       // not an object
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"access_token":123,"expires_in":5000000}""")]            // wrong type: token
    [InlineData("""{"access_token":"","expires_in":5000000}""")]             // empty token
    [InlineData("""{"access_token":"   ","expires_in":5000000}""")]          // blank token
    [InlineData("""{"access_token":"t"}""")]                                 // no expires_in
    [InlineData("""{"access_token":"t","expires_in":"soon"}""")]             // wrong type: expiry
    [InlineData("""{"access_token":"t","expires_in":null}""")]
    [InlineData("""{"access_token":"t","expires_in":1.5}""")]                // not an integer
    [InlineData("""{"access_token":"t","expires_in":0}""")]
    [InlineData("""{"access_token":"t","expires_in":-5}""")]
    [InlineData("""{"access_token":"t","expires_in":99999999999999999999}""")] // overflows 64 bits
    public void ParseTokenResponse_MalformedResponse_IsRejectedAsUnavailable(string json)
    {
        Assert.Throws<IgdbCoverArtProvider.IgdbUnavailableException>(() => IgdbCoverArtProvider.ParseTokenResponse(json));
    }

    [Fact]
    public void ParseTokenResponse_ValidResponse_ReturnsTokenAndLifetime()
    {
        var (token, lifetime) = IgdbCoverArtProvider.ParseTokenResponse("""{"access_token":"abc","expires_in":3600,"token_type":"bearer"}""");

        Assert.Equal("abc", token);
        Assert.Equal(TimeSpan.FromSeconds(3600), lifetime);
    }

    [Fact]
    public void ParseTokenResponse_RepresentableButAbsurdlyLargeExpiry_IsClampedNotTrusted()
    {
        var (_, lifetime) = IgdbCoverArtProvider.ParseTokenResponse("""{"access_token":"abc","expires_in":3000000000}""");

        Assert.Equal(TimeSpan.FromDays(90), lifetime);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"access_token":123,"expires_in":5}""")]
    [InlineData("""{"access_token":"t","expires_in":99999999999999999999}""")]
    public void GetCoverArt_MalformedTokenResponse_IsUnavailable_DoesNotThrow_AndIsNotCached(string malformed)
    {
        var provider = new IgdbCoverArtProvider("malformed-id", "malformed-secret")
        {
            TokenRequestOverride = (_, _) => malformed,
            SearchRequestOverride = _ => "[]",
        };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());
        Assert.Equal(CoverLookupStatus.Unavailable, status);

        // Nothing malformed may have been cached: the very next lookup must ask Twitch again, and succeed.
        var requests = 0;
        var healthy = new IgdbCoverArtProvider("malformed-id", "malformed-secret")
        {
            TokenRequestOverride = (_, _) => { requests++; return ValidTokenJson("good"); },
            SearchRequestOverride = _ => "[]",
        };
        var (_, healthyStatus, _, _) = Lookup(healthy, MakeGame(), NewCacheDir());

        Assert.Equal(1, requests);
        Assert.Equal(CoverLookupStatus.NoMatch, healthyStatus); // "[]" is a real, empty search result now
    }

    // ---- 401: exactly one token refresh + retry ---------------------------------------------------------

    [Fact]
    public void Api401_RefreshesTheTokenOnce_RetriesOnceWithTheNewToken_AndSucceeds()
    {
        var tokenIssued = 0;
        var bearerTokensSeen = new List<string?>();
        var handler = new FakeIgdbHandler
        {
            OnToken = (_, _) => FakeIgdbHandler.Json(ValidTokenJson($"tok-{Interlocked.Increment(ref tokenIssued)}")),
            OnApi = (request, n, _) =>
            {
                bearerTokensSeen.Add(request.Headers.Authorization?.Parameter);
                return n == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : FakeIgdbHandler.Json(AWayOutSingleMatch);
            },
        };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            HttpHandlerOverrideForTest = handler,
            FetchCoverImageBytesOverride = _ => MakeValidPngBytes(),
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.NotNull(image);
        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.Equal(36897, matched?.Id);
        Assert.Equal(2, handler.TokenCalls);
        Assert.Equal(new[] { "tok-1", "tok-2" }, bearerTokensSeen); // the retry really used the FRESH token
    }

    [Fact]
    public void Api401Twice_IsBounded_ExactlyOneRetry_ThenUnavailable()
    {
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var provider = new IgdbCoverArtProvider("id", "secret") { HttpHandlerOverrideForTest = handler };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(2, handler.ApiCalls); // the original + exactly one retry, never a loop
    }

    // ---- 429: bounded, Retry-After-aware, cancellable -----------------------------------------------------

    [Fact]
    public void ComputeRateLimitBackoff_UsesRetryAfterDelta_WhenSupplied()
    {
        var delay = IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)), DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void ComputeRateLimitBackoff_UsesRetryAfterHttpDate_WhenSupplied()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var delay = IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(now.AddSeconds(3)), now);
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void ComputeRateLimitBackoff_RetryAfterDateAlreadyPast_IsZero_NotNegative()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var delay = IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(now.AddSeconds(-30)), now);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void ComputeRateLimitBackoff_NoHeader_BacksOffExponentially()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), IgdbCoverArtProvider.ComputeRateLimitBackoff(0, null, DateTimeOffset.UtcNow));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), IgdbCoverArtProvider.ComputeRateLimitBackoff(1, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ComputeRateLimitBackoff_ServerAsksForLongerThanTheBudget_IsRefused()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)), DateTimeOffset.UtcNow));
        Assert.Null(IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(TimeSpan.FromSeconds(10.001)), DateTimeOffset.UtcNow));
        Assert.Null(IgdbCoverArtProvider.ComputeRateLimitBackoff(0, new RetryConditionHeaderValue(TimeSpan.FromHours(1)), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Api429_ThenSuccess_WaitsTheServersRetryAfter_AndRecovers()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeIgdbHandler
        {
            OnApi = (_, n, _) => n == 1 ? RateLimited(TimeSpan.FromSeconds(2)) : FakeIgdbHandler.Json(AWayOutSingleMatch),
        };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            BackoffDelayOverrideForTest = delays.Add,
            FetchCoverImageBytesOverride = _ => MakeValidPngBytes(),
        };

        var (image, status, matched, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.NotNull(image);
        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.Equal(36897, matched?.Id);
        Assert.Equal(2, handler.ApiCalls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays); // honored the server's Retry-After exactly
    }

    [Fact]
    public void Api429Repeatedly_IsBounded_ThenUnavailable_NeverRetriesIndefinitely()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => RateLimited() };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            BackoffDelayOverrideForTest = delays.Add,
        };

        var (image, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Null(image);
        Assert.Equal(CoverLookupStatus.Unavailable, status); // not NoMatch: 429 says nothing about the catalog
        Assert.Equal(1 + IgdbCoverArtProvider.MaxRateLimitRetries, handler.ApiCalls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, delays);
    }

    [Fact]
    public void Api429_WithRetryAfterBeyondTheBudget_IsNotRetriedAtAll()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => RateLimited(TimeSpan.FromHours(1)) };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            BackoffDelayOverrideForTest = delays.Add,
        };

        var (_, status, _, _) = Lookup(provider, MakeGame(), NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(1, handler.ApiCalls);
        Assert.Empty(delays);
    }

    [Fact]
    public void Api429_CancelledDuringTheBackoff_ThrowsAndSendsNoFurtherRequest()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeIgdbHandler { OnApi = (_, _, _) => RateLimited(TimeSpan.FromSeconds(1)) };
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            BackoffDelayOverrideForTest = _ => cts.Cancel(), // the scan is superseded while we're backing off
        };

        Assert.ThrowsAny<OperationCanceledException>(() => Lookup(provider, MakeGame(), NewCacheDir(), cts.Token));

        Assert.Equal(1, handler.ApiCalls);
    }

    // ---- Local rate limiting ---------------------------------------------------------------------------

    [Fact]
    public void TwoApiCallsInQuickSuccession_AreThrottledToTheDocumentedRateLimit()
    {
        // Real proof: two real (fake-transport) API calls back to back take at least roughly the spacing
        // IGDB's documented 4-requests-per-second limit implies - exercising the actual shared gate.
        var handler = new FakeIgdbHandler();
        var provider1 = new IgdbCoverArtProvider("rate-limit-id", "rate-limit-secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };
        var provider2 = new IgdbCoverArtProvider("rate-limit-id", "rate-limit-secret") { AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = handler };
        var cacheDir = NewCacheDir();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Lookup(provider1, MakeGame("Throttle Test A"), cacheDir);
        Lookup(provider2, MakeGame("Throttle Test B", id: "manual-igdb-test-b"), cacheDir);
        sw.Stop();

        Assert.Equal(2, handler.ApiCalls);
        Assert.True(sw.ElapsedMilliseconds >= 200, $"Expected the second API call to be throttled by roughly 260ms, took {sw.ElapsedMilliseconds}ms in total.");
    }
}
