using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>B1 correction: a response the provider cannot READ is Unavailable - never a catalog conclusion - and
/// that distinction has to survive all the way to the coordinator's diagnostics and its fallback decision.
/// Through the real coordinator (CoverArtService.Apply) with REAL providers behind their own transport seams:
///  - an exact-title candidate with an unreadable id, an unreadable search shape, or an unreadable cover/grid
///    listing is worded as "unavailable" (not "no confident match", not "no usable cover"), applies no cover,
///    downloads nothing and caches nothing;
///  - a valid EMPTY search and a valid EMPTY listing keep their own, different wording.
///
/// In the IGDB static-state collection: the IGDB cases go through the real request pipeline, which uses IGDB's
/// shared rate-limit clock.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class CoverArtServiceMalformedResponseTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-ApplyMalformed-" + Guid.NewGuid());

    public CoverArtServiceMalformedResponseTests()
    {
        IgdbCoverArtProvider.ResetTokenCacheForTest();
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
        IgdbCoverArtProvider.ResetTokenCacheForTest();
        IgdbCoverArtProvider.ResetRateLimitForTest();
    }

    private static GameEntry Game() => new()
    {
        Id = "manual-apply-malformed", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    private void AssertNothingCached() =>
        Assert.True(!Directory.Exists(_cacheDir) || Directory.GetFiles(_cacheDir, "*", SearchOption.AllDirectories).Length == 0,
            "an unreadable response must never leave a cache entry behind");

    // ---- SteamGridDB stage ------------------------------------------------------------------------------------

    private const string ExactBesideUnreadable =
        """{ "data": [ { "id": 111, "name": "Test Game" }, { "id": "invalid", "name": "Test Game" } ] }""";

    private string ApplySgdb(SteamGridDbCoverArtProvider provider, out ArtworkSelection? selection)
    {
        var diagnostics = new List<string>();
        selection = CoverArtService.Apply(Game(), "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);
        return Assert.Single(diagnostics);
    }

    [Fact]
    public void SgdbAnExactMatchBesideAnUnreadableOne_IsReportedUnavailable_NoCoverIsApplied_NothingDownloaded()
    {
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => ExactBesideUnreadable,
            FetchGridImageBytesOverride = _ => throw new InvalidOperationException("uniqueness was never established - must never download"),
        };

        var diagnostic = ApplySgdb(provider, out var selection);

        Assert.Null(selection); // 111 was NOT taken as the match
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
        AssertNothingCached();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "data": "x" }""")]
    public void SgdbASearchOfTheWrongShape_IsReportedUnavailable_NotNoConfidentMatch(string body)
    {
        var provider = new SteamGridDbCoverArtProvider("k") { SearchRequestOverride = _ => body };

        var diagnostic = ApplySgdb(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static SteamGridDbCoverArtProvider SgdbWithListing(string listing) => new("k")
    {
        HttpHandlerOverrideForTest = new FuncHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/search/autocomplete/", StringComparison.Ordinal))
                return FakeIgdbHandler.Json("""{ "data": [ { "id": 5, "name": "Test Game" } ] }""");
            if (path.Contains("/grids/game/", StringComparison.Ordinal))
                return FakeIgdbHandler.Json(listing);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) };
        }),
    };

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": [ {} ] }""")]
    [InlineData("""{ "data": [ { "url": 5 } ] }""")]
    public void SgdbAMalformedGridListing_IsReportedUnavailable_NotAsNoUsableCover(string listing)
    {
        var diagnostic = ApplySgdb(SgdbWithListing(listing), out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
        AssertNothingCached();
    }

    [Fact]
    public void SgdbAValidEmptyGridListing_IsStillReportedAsNoUsableCover()
    {
        var diagnostic = ApplySgdb(SgdbWithListing("""{ "data": [] }"""), out var selection);

        Assert.Null(selection);
        Assert.Contains("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    // ---- IGDB stage --------------------------------------------------------------------------------------------
    // The SteamGridDB stage that follows an IGDB miss is given a provider that finds nothing (offline), so the
    // diagnostics are [IGDB's, SteamGridDB's]; every assertion is on the FIRST.

    private static SteamGridDbCoverArtProvider OfflineSgdb() => new("k") { SearchGameIdOverride = _ => null };

    private string ApplyIgdb(IgdbCoverArtProvider provider, out ArtworkSelection? selection)
    {
        var diagnostics = new List<string>();
        selection = CoverArtService.Apply(Game(), "gridkey", "id", "secret", provider, OfflineSgdb(),
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);
        Assert.Equal(2, diagnostics.Count);
        return diagnostics[0];
    }

    [Fact]
    public void IgdbAnExactMatchBesideAnUnreadableOne_IsReportedUnavailable_NotAUniqueMatch()
    {
        var provider = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => """[ { "id": 111, "name": "Test Game" }, { "id": "invalid", "name": "Test Game" } ]""",
            FetchCoverImageBytesOverride = _ => throw new InvalidOperationException("uniqueness was never established - must never download"),
        };

        var diagnostic = ApplyIgdb(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
        AssertNothingCached();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": [] }""")]
    public void IgdbASearchOfTheWrongShape_IsReportedUnavailable_NotNoConfidentMatch(string body)
    {
        var provider = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", SearchRequestOverride = _ => body };

        var diagnostic = ApplyIgdb(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static IgdbCoverArtProvider IgdbWithListing(string listing) => new("id", "secret")
    {
        AccessTokenOverrideForTest = "token",
        HttpHandlerOverrideForTest = new FakeIgdbHandler
        {
            OnApi = (request, _, _) => request.RequestUri!.AbsolutePath.EndsWith("/covers", StringComparison.Ordinal)
                ? FakeIgdbHandler.Json(listing)
                : FakeIgdbHandler.Json("""[ { "id": 5, "name": "Test Game" } ]"""),
            OnImage = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) },
        },
    };

    [Theory]
    [InlineData("{}")]
    [InlineData("[ {} ]")]
    [InlineData("""[ { "image_id": 7 } ]""")]
    [InlineData("""[ { "image_id": "" } ]""")]
    public void IgdbAMalformedCoverListing_IsReportedUnavailable_NotAsNoUsableCover(string listing)
    {
        var diagnostic = ApplyIgdb(IgdbWithListing(listing), out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
        AssertNothingCached();
    }

    [Fact]
    public void IgdbAValidEmptyCoverListing_IsStillReportedAsNoUsableCover()
    {
        var diagnostic = ApplyIgdb(IgdbWithListing("[]"), out var selection);

        Assert.Null(selection);
        Assert.Contains("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
