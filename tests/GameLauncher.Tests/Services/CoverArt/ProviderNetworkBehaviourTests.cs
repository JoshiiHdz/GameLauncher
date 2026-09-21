using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>The three requests a name-searching provider makes for one game.</summary>
public enum ProviderStage { Search, Listing, Image }

/// <summary>B2 provider hardening, name-searching half: ONE contract for what IGDB and SteamGridDB do when a
/// request is cancelled, stalls, or returns too much - asserted against BOTH through the real HttpClient pipeline
/// (a real HttpMessageHandler, never a bypassing seam), so neither can drift from the other.
///
/// The rules under test:
///  - the CALLER's cancellation, at ANY stage (waiting for headers, reading a body), propagates as
///    OperationCanceledException - never a status, never Unavailable - stops the later stages from starting and
///    caches nothing;
///  - a stall at any stage, past that request's own deadline, is Unavailable (an outage-shaped failure that says
///    nothing about the catalog) and likewise starts nothing further and caches nothing;
///  - a JSON body that is endless, over the cap, or DECLARES it is over the cap is Unavailable, promptly, and is
///    never buffered to the end; a large but legal body still resolves (the cap is headroom, not a truncation).
///
/// Every test uses its own temp cache directory and per-instance seams - no static state beyond IGDB's rate-limit
/// clock (reset by the IGDB subclass).</summary>
public abstract class ProviderNetworkBehaviourTests : IDisposable
{
    private readonly List<string> _dirs = new();

    protected abstract ProviderStage StageOf(HttpRequestMessage request);
    protected abstract string Wrap(params string[] rawCandidates);
    protected abstract string ListingOk { get; }
    protected abstract string LargeListing(int extraEntries);
    protected abstract string CacheImagePath(GameEntry game, string cacheDir);

    /// <summary>Runs the provider's real lookup through `handler`; throws whatever escapes it.</summary>
    protected abstract (BitmapImage? Image, CoverLookupStatus Status, (int Id, string Title)? Matched) Call(
        GameEntry game, string cacheDir, HttpMessageHandler handler, CancellationToken ct,
        TimeSpan? requestTimeout = null, TimeSpan? imageTimeout = null);

    /// <summary>Runs the provider with a valid search and an image SEAM (the download itself bypassed), invoking
    /// `onImage` from inside it and then returning a valid image - to cancel exactly between "downloaded" and
    /// "cached", which no transport-level stall can reach.</summary>
    protected abstract void CallWithImageSeam(GameEntry game, string cacheDir, CancellationToken ct, Action onImage);

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    protected string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-NetBehaviour-" + Guid.NewGuid());
        _dirs.Add(dir);
        return dir;
    }

    protected static GameEntry Game() => new()
    {
        Id = "manual-net-behaviour", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    protected string SearchOk => Wrap("""{"id": 5, "name": "Test Game"}""");

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>A handler serving valid responses for every stage, except where `custom` supplies one.</summary>
    internal FuncHttpHandler Handler(Func<ProviderStage, HttpRequestMessage, CancellationToken, HttpResponseMessage?>? custom = null) =>
        new((request, ct) =>
        {
            var stage = StageOf(request);
            var response = custom?.Invoke(stage, request, ct);
            if (response is not null)
                return response;

            return stage switch
            {
                ProviderStage.Search => Json(SearchOk),
                ProviderStage.Listing => Json(ListingOk),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) },
            };
        });

    internal int CallsAtOrAfter(FuncHttpHandler handler, ProviderStage stage) =>
        handler.Urls.Count(u => (int)StageOf(new HttpRequestMessage(HttpMethod.Get, u)) >= (int)stage);

    private void AssertNothingCached(GameEntry game, string dir)
    {
        Assert.False(File.Exists(CacheImagePath(game, dir)), "nothing may be cached");
        Assert.False(File.Exists(CacheImagePath(game, dir) + ".meta.json"), "no sidecar may be written");
    }

    // ---- The caller's cancellation -----------------------------------------------------------------------------

    [Fact]
    public void ACallerTokenCancelledBeforeTheLookup_Throws_AndMakesNoRequest()
    {
        var dir = NewCacheDir();
        var handler = Handler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Call(Game(), dir, handler, cts.Token));

        Assert.Equal(0, handler.Calls);
        AssertNothingCached(Game(), dir);
    }

    [Theory]
    [InlineData(ProviderStage.Search, false)]
    [InlineData(ProviderStage.Search, true)]
    [InlineData(ProviderStage.Listing, false)]
    [InlineData(ProviderStage.Listing, true)]
    [InlineData(ProviderStage.Image, false)]
    [InlineData(ProviderStage.Image, true)]
    public async Task TheCallersCancellation_AtEveryStage_PropagatesAsCancellation_NotAsAnOutcome(ProviderStage stage, bool whileReadingTheBody)
    {
        // `stage` is where the request hangs: waiting for headers, or (whileReadingTheBody) after headers, in the
        // body. In both cases the token must reach the blocked call and end it as a CANCELLATION.
        var dir = NewCacheDir();
        var game = Game();
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var handler = Handler((s, _, ct) =>
        {
            if (s != stage)
                return null;

            entered.Set();
            if (whileReadingTheBody)
                return FakeIgdbHandler.StallingBody();

            FakeIgdbHandler.BlockUntilCancelled(ct);
            return null;
        });

        var lookup = Task.Run(() => Call(game, dir, handler, cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the lookup never reached the stage under test");
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => lookup);
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation must end the blocked request promptly, took {sw.Elapsed}");
        Assert.Equal(0, CallsAtOrAfter(handler, stage + 1)); // a cancelled lookup starts no later stage
        AssertNothingCached(game, dir);
    }

    [Fact]
    public void ACancellationBetweenDownloadingAndCaching_CachesNothing()
    {
        // The image seam cancels the token and still returns a perfectly valid image: the provider must notice
        // BEFORE it writes, not after.
        var dir = NewCacheDir();
        var game = Game();
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => CallWithImageSeam(game, dir, cts.Token, cts.Cancel));

        AssertNothingCached(game, dir);
    }

    // ---- Request deadlines ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProviderStage.Search, false)]
    [InlineData(ProviderStage.Search, true)]
    [InlineData(ProviderStage.Listing, false)]
    [InlineData(ProviderStage.Listing, true)]
    [InlineData(ProviderStage.Image, false)]
    [InlineData(ProviderStage.Image, true)]
    public void AStallAtAnyStage_PastItsDeadline_IsUnavailable_AndStartsNothingFurther(ProviderStage stage, bool whileReadingTheBody)
    {
        var dir = NewCacheDir();
        var game = Game();
        var handler = Handler((s, _, ct) =>
        {
            if (s != stage)
                return null;

            if (whileReadingTheBody)
                return FakeIgdbHandler.StallingBody();

            FakeIgdbHandler.BlockUntilCancelled(ct);
            return null;
        });

        var sw = Stopwatch.StartNew();
        var result = Call(game, dir, handler, CancellationToken.None,
            requestTimeout: TimeSpan.FromMilliseconds(300), imageTimeout: TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.Equal(CoverLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Image);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the request's own deadline should end this, took {sw.Elapsed}");
        Assert.Equal(0, CallsAtOrAfter(handler, stage + 1));
        AssertNothingCached(game, dir);
    }

    // ---- JSON bodies are bounded ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> JsonStages()
    {
        yield return new object[] { ProviderStage.Search };
        yield return new object[] { ProviderStage.Listing };
    }

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AnEndlessJsonBody_IsCapped_ReportedUnavailable_AndNeverBufferedToTheEnd(ProviderStage stage)
    {
        var dir = NewCacheDir();
        var game = Game();
        var handler = Handler((s, _, _) => s == stage ? FakeIgdbHandler.EndlessBody() : null);

        var sw = Stopwatch.StartNew();
        var result = Call(game, dir, handler, CancellationToken.None);
        sw.Stop();

        Assert.Equal(CoverLookupStatus.Unavailable, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"the cap should end this promptly, took {sw.Elapsed}");
        Assert.Equal(0, CallsAtOrAfter(handler, stage + 1));
        AssertNothingCached(game, dir);
    }

    private string ValidBodyFor(ProviderStage stage) => stage == ProviderStage.Search ? SearchOk : ListingOk;

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AJsonBodyOneByteOverTheCap_IsUnreadable_EvenWhenItWouldOtherwiseResolve(ProviderStage stage)
    {
        // A VALID response padded with trailing whitespace: parsed uncapped it would resolve, so ONLY the cap can be
        // what makes this Unavailable. (A whitespace-only body would prove nothing - it is invalid JSON either way.)
        var dir = NewCacheDir();
        var game = Game();
        var valid = ValidBodyFor(stage);
        var padded = valid + new string(' ', ProviderHttp.MaxJsonResponseBytes + 1 - valid.Length);
        Assert.Equal(ProviderHttp.MaxJsonResponseBytes + 1, padded.Length);
        var handler = Handler((s, _, _) => s == stage ? Json(padded) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Unavailable, result.Status);
        Assert.Equal(0, CallsAtOrAfter(handler, stage + 1));
        AssertNothingCached(game, dir);
    }

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AJsonBodyExactlyAtTheCap_StillResolves_TheBoundIsNotOffByOne(ProviderStage stage)
    {
        var dir = NewCacheDir();
        var game = Game();
        var valid = ValidBodyFor(stage);
        var padded = valid + new string(' ', ProviderHttp.MaxJsonResponseBytes - valid.Length);
        Assert.Equal(ProviderHttp.MaxJsonResponseBytes, padded.Length);
        var handler = Handler((s, _, _) => s == stage ? Json(padded) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
    }

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AJsonBodyThatDeclaresItIsOverTheCap_IsRefusedBeforeItIsRead(ProviderStage stage)
    {
        var dir = NewCacheDir();
        var game = Game();
        var handler = Handler((s, _, _) =>
        {
            if (s != stage)
                return null;

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("[]"));
            content.Headers.ContentLength = ProviderHttp.MaxJsonResponseBytes + 1L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Unavailable, result.Status);
        AssertNothingCached(game, dir);
    }

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AChunkedJsonBody_OneByteOverTheCap_IsUnreadable_EvenWhenItWouldOtherwiseResolve(ProviderStage stage)
    {
        // The realistic shape of a JSON API response: chunked, so it declares no length and only the STREAMING cap
        // can stop it (the declared-length check never fires).
        var dir = NewCacheDir();
        var game = Game();
        var valid = ValidBodyFor(stage);
        var padded = valid + new string(' ', ProviderHttp.MaxJsonResponseBytes + 1 - valid.Length);
        var handler = Handler((s, _, _) => s == stage ? UnknownLengthBody.Response(padded) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Unavailable, result.Status);
        Assert.Equal(0, CallsAtOrAfter(handler, stage + 1));
        AssertNothingCached(game, dir);
    }

    [Theory]
    [MemberData(nameof(JsonStages))]
    public void AChunkedJsonBody_ExactlyAtTheCap_StillResolves(ProviderStage stage)
    {
        var dir = NewCacheDir();
        var game = Game();
        var valid = ValidBodyFor(stage);
        var padded = valid + new string(' ', ProviderHttp.MaxJsonResponseBytes - valid.Length);
        var handler = Handler((s, _, _) => s == stage ? UnknownLengthBody.Response(padded) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
    }

    [Fact]
    public void ALargeButLegalSearchBody_StillResolves_TheCapIsHeadroomNotATruncation()
    {
        // ~1.5 MiB of non-matching candidates with the one real match LAST: a cap that truncated (or was too
        // tight) would lose it.
        var dir = NewCacheDir();
        var game = Game();
        var sb = new StringBuilder();
        var target = ProviderHttp.MaxJsonResponseBytes * 3 / 4;
        var filler = new List<string>();
        var size = 0;
        for (var i = 1; size < target; i++)
        {
            var candidate = $$"""{"id": {{i + 100}}, "name": "Some Other Title Number {{i}}"}""";
            filler.Add(candidate);
            size += candidate.Length + 1;
        }
        filler.Add("""{"id": 5, "name": "Test Game"}""");
        var body = Wrap(filler.ToArray());
        Assert.InRange(body.Length, ProviderHttp.MaxJsonResponseBytes / 2, ProviderHttp.MaxJsonResponseBytes - 1);
        var handler = Handler((s, _, _) => s == ProviderStage.Search ? Json(body) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
        Assert.Equal((5, "Test Game"), result.Matched);
    }

    [Fact]
    public void ALargeButLegalListing_StillResolves()
    {
        var dir = NewCacheDir();
        var game = Game();
        var body = LargeListing(extraEntries: 20_000);
        Assert.InRange(body.Length, ProviderHttp.MaxJsonResponseBytes / 8, ProviderHttp.MaxJsonResponseBytes - 1);
        var handler = Handler((s, _, _) => s == ProviderStage.Listing ? Json(body) : null);

        var result = Call(game, dir, handler, CancellationToken.None);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
    }
}

/// <summary>The network-behaviour contract against IgdbCoverArtProvider. In the IGDB static-state collection: its
/// api.igdb.com calls go through IGDB's shared rate-limit clock, which the constructor and Dispose reset.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public sealed class IgdbNetworkBehaviourTests : ProviderNetworkBehaviourTests
{
    public IgdbNetworkBehaviourTests()
    {
        IgdbCoverArtProvider.ResetTokenCacheForTest();
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    protected override ProviderStage StageOf(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host == "api.igdb.com")
            return request.RequestUri.AbsolutePath.EndsWith("/covers", StringComparison.Ordinal) ? ProviderStage.Listing : ProviderStage.Search;
        return ProviderStage.Image;
    }

    protected override string Wrap(params string[] rawCandidates) => "[" + string.Join(",", rawCandidates) + "]";

    protected override string ListingOk => """[{"id": 1, "image_id": "co1abc"}]""";

    protected override string LargeListing(int extraEntries) =>
        """[{"id": 1, "image_id": "co1abc"}"""
        + string.Concat(Enumerable.Range(0, extraEntries).Select(i => $$""" ,{"id": {{i + 10}}, "image_id": "co{{i}}"}""")) + "]";

    protected override string CacheImagePath(GameEntry game, string cacheDir) =>
        Path.Combine(cacheDir, $"{game.Id}-v{IgdbCoverArtProvider.CacheVersionForTest}.png");

    protected override (BitmapImage? Image, CoverLookupStatus Status, (int Id, string Title)? Matched) Call(
        GameEntry game, string cacheDir, HttpMessageHandler handler, CancellationToken ct, TimeSpan? requestTimeout = null, TimeSpan? imageTimeout = null)
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            RequestTimeoutOverrideForTest = requestTimeout,
            ImageDownloadTimeoutOverrideForTest = imageTimeout,
        };
        var bitmap = provider.GetCoverArt(game, out _, out var matched, out var status, cacheDir, ct);
        return (bitmap, status, matched is { } m ? (m.Id, m.Title) : null);
    }

    protected override void CallWithImageSeam(GameEntry game, string cacheDir, CancellationToken ct, Action onImage)
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => SearchOk,
            FetchCoverImageBytesOverride = _ => { onImage(); return TestImages.Png(); },
        };
        provider.GetCoverArt(game, out _, out _, out _, cacheDir, ct);
    }

    // ---- The token request is bounded and has a deadline too ------------------------------------------------------

    private (CoverLookupStatus Status, FuncHttpHandler Handler) LookupWithToken(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> tokenResponse, TimeSpan? requestTimeout = null)
    {
        var handler = new FuncHttpHandler((request, ct) =>
            request.RequestUri!.Host == "id.twitch.tv"
                ? tokenResponse(request, ct)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var provider = new IgdbCoverArtProvider("token-id", "token-secret")
        {
            HttpHandlerOverrideForTest = handler,
            RequestTimeoutOverrideForTest = requestTimeout,
        };
        provider.GetCoverArt(Game(), out _, out _, out var status, NewCacheDir());
        return (status, handler);
    }

    [Fact]
    public void AnEndlessTokenBody_IsCapped_ReportedUnavailable_AndNoSearchIsSent()
    {
        var sw = Stopwatch.StartNew();
        var (status, handler) = LookupWithToken((_, _) => FakeIgdbHandler.EndlessBody());
        sw.Stop();

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(1, handler.Calls); // the token request only - no search with a token that was never obtained
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"took {sw.Elapsed}");
    }

    private const string ValidTokenJson = """{"access_token":"good","expires_in":5000000}""";

    [Fact]
    public void AValidTokenBodyOneByteOverItsCap_IsUnavailable_TheCapIsWhatRejectsIt()
    {
        // Valid token JSON padded past the cap: uncapped it would yield a working token.
        var (status, handler) = LookupWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidTokenJson + new string(' ', ProviderHttp.MaxTokenResponseBytes + 1 - ValidTokenJson.Length)),
        });

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(1, handler.Calls); // no search was sent with a token that should not have been accepted
    }

    [Fact]
    public void AValidTokenBodyExactlyAtItsCap_IsAccepted()
    {
        var (status, handler) = LookupWithToken((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidTokenJson + new string(' ', ProviderHttp.MaxTokenResponseBytes - ValidTokenJson.Length)),
        });

        Assert.Equal(CoverLookupStatus.NoMatch, status); // the token worked, and the (empty) search then ran
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void AStalledTokenRequest_PastItsDeadline_IsUnavailable()
    {
        var sw = Stopwatch.StartNew();
        var (status, handler) = LookupWithToken((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return new HttpResponseMessage(); },
            requestTimeout: TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.Equal(CoverLookupStatus.Unavailable, status);
        Assert.Equal(1, handler.Calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the token request's own deadline should end this, took {sw.Elapsed}");
    }

    [Fact]
    public void AnOverCapTokenBody_IsNeverCached_SoTheNextLookupAsksAgain()
    {
        var calls = 0;
        var handler = new FuncHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host != "id.twitch.tv")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };

            return ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', ProviderHttp.MaxTokenResponseBytes + 1)) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"good","expires_in":5000000}""") };
        });

        var first = new IgdbCoverArtProvider("cap-id", "cap-secret") { HttpHandlerOverrideForTest = handler };
        first.GetCoverArt(Game(), out _, out _, out var firstStatus, NewCacheDir());
        var second = new IgdbCoverArtProvider("cap-id", "cap-secret") { HttpHandlerOverrideForTest = handler };
        second.GetCoverArt(Game(), out _, out _, out var secondStatus, NewCacheDir());

        Assert.Equal(CoverLookupStatus.Unavailable, firstStatus);
        Assert.Equal(CoverLookupStatus.NoMatch, secondStatus); // a fresh token was requested and worked; "[]" is a real empty search
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TheCallersCancellation_WhileTheTokenIsBeingRequested_PropagatesAsCancellation()
    {
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var handler = new FuncHttpHandler((request, ct) =>
        {
            entered.Set();
            FakeIgdbHandler.BlockUntilCancelled(ct);
            return new HttpResponseMessage();
        });
        var provider = new IgdbCoverArtProvider("cancel-id", "cancel-secret") { HttpHandlerOverrideForTest = handler };
        var dir = NewCacheDir();

        var lookup = Task.Run(() => provider.GetCoverArt(Game(), out _, out _, out _, dir, cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => lookup);

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }
}

/// <summary>The network-behaviour contract against SteamGridDbCoverArtProvider.</summary>
public sealed class SteamGridDbNetworkBehaviourTests : ProviderNetworkBehaviourTests
{
    protected override ProviderStage StageOf(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.Contains("/search/autocomplete/", StringComparison.Ordinal))
            return ProviderStage.Search;
        return path.Contains("/grids/game/", StringComparison.Ordinal) ? ProviderStage.Listing : ProviderStage.Image;
    }

    protected override string Wrap(params string[] rawCandidates) => """{"data": [""" + string.Join(",", rawCandidates) + "]}";

    protected override string ListingOk => """{"data": [{"url": "https://cdn2.steamgriddb.com/grid/cover.png"}]}""";

    protected override string LargeListing(int extraEntries) =>
        """{"data": [{"url": "https://cdn2.steamgriddb.com/grid/cover.png"}"""
        + string.Concat(Enumerable.Range(0, extraEntries).Select(i => $$""" ,{"id": {{i}}, "url": "https://cdn2.steamgriddb.com/grid/{{i}}.png"}""")) + "]}";

    protected override string CacheImagePath(GameEntry game, string cacheDir) =>
        Path.Combine(cacheDir, $"{game.Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");

    protected override (BitmapImage? Image, CoverLookupStatus Status, (int Id, string Title)? Matched) Call(
        GameEntry game, string cacheDir, HttpMessageHandler handler, CancellationToken ct, TimeSpan? requestTimeout = null, TimeSpan? imageTimeout = null)
    {
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            HttpHandlerOverrideForTest = handler,
            RequestTimeoutOverrideForTest = requestTimeout,
            ImageDownloadTimeoutOverrideForTest = imageTimeout,
        };
        var bitmap = provider.GetCoverArt(game, out _, out var matched, out var status, cacheDir, ct);
        return (bitmap, status, matched is { } m ? (m.Id, m.Title) : null);
    }

    protected override void CallWithImageSeam(GameEntry game, string cacheDir, CancellationToken ct, Action onImage)
    {
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = _ => SearchOk,
            FetchGridImageBytesOverride = _ => { onImage(); return TestImages.Png(); },
        };
        provider.GetCoverArt(game, out _, out _, out _, cacheDir, ct);
    }

    [Fact]
    public async Task TheCallersCancellation_BetweenThePrimarySearchAndTheCompactedWordsRetry_StopsBeforeTheRetry()
    {
        // "AWayOut" retries as "A Way Out" after a no-match; a superseded scan must not start that second search.
        using var cts = new CancellationTokenSource();
        var searches = new List<string>();
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = q =>
            {
                searches.Add(q);
                cts.Cancel(); // cancelled while the FIRST search's response is being handed back
                return """{ "data": [] }""";
            },
        };
        var game = Game();
        game.Name = "AWayOut";

        var lookup = Task.Run(() => provider.GetCoverArt(game, out _, out _, out _, NewCacheDir(), cts.Token));
        var ex = await Record.ExceptionAsync(() => lookup);

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(new[] { "AWayOut" }, searches);
    }
}
