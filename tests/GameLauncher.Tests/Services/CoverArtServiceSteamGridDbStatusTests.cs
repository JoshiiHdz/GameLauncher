using System.IO;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>B1: through the real coordinator (CoverArtService.Apply), the SteamGridDB stage now reports an
/// explicit status the way the IGDB stage does - an ambiguous identity, an outage and a genuine non-match are
/// worded (and logged) differently, and none of them stops the cascade to Steam CDN / the exe icon. Uses a
/// REAL SteamGridDbCoverArtProvider with its own transport seams, not a stub, so the statuses asserted here are
/// the ones the provider actually produces.
///
/// Steam CDN is not injectable into Apply (it constructs SteamCoverArtProvider inline), but it does not need to
/// be: its cache is keyed by the app id, so a pre-populated isolated steam-{appid}.jpg makes the Steam CDN stage
/// resolve WITHOUT any network - which is how "the cascade continues to Steam CDN after a SteamGridDB
/// ambiguity/outage" is asserted below. For a non-Steam game the next stage is the exe icon.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class CoverArtServiceSteamGridDbStatusTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-ApplySgdb-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    private static GameEntry Game() => new()
    {
        Id = "manual-apply-sgdb", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    private string Apply(SteamGridDbCoverArtProvider provider, out ArtworkSelection? selection)
    {
        var diagnostics = new List<string>();
        selection = CoverArtService.Apply(Game(), "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);
        return Assert.Single(diagnostics);
    }

    [Fact]
    public void AnAmbiguousSteamGridDbIdentity_IsReportedAsAmbiguous_AndNothingIsGuessed()
    {
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => """{ "data": [ { "id": 1, "name": "Test Game" }, { "id": 2, "name": "Test Game" } ] }""",
            FetchGridImageBytesOverride = _ => throw new InvalidOperationException("an ambiguous identity must never download"),
        };

        var diagnostic = Apply(provider, out var selection);

        Assert.Null(selection); // no cover was guessed; the cascade ended at the exe icon
        Assert.Contains("ambiguous", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASteamGridDbOutage_IsReportedAsUnavailable_NotAsNoConfidentMatch()
    {
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => throw new HttpRequestException("401"),
        };

        var diagnostic = Apply(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASteamGridDbNonMatch_IsReportedAsNoConfidentMatch()
    {
        var provider = new SteamGridDbCoverArtProvider("k") { SearchRequestOverride = _ => """{ "data": [] }""" };

        var diagnostic = Apply(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnIdentifiedGameWithAnInvalidCover_IsReportedAsIdentifiedWithoutUsableArt()
    {
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => """{ "data": [ { "id": 5, "name": "Test Game" } ] }""",
            FetchGridImageBytesOverride = _ => TestImages.Png(9000, 100),
        };

        var diagnostic = Apply(provider, out var selection);

        Assert.Null(selection);
        Assert.Contains("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AResolvedSteamGridDbStage_EmitsNoFallbackDiagnostic_AndRecordsTheMatch()
    {
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => """{ "data": [ { "id": 5, "name": "Test Game" } ] }""",
            FetchGridImageBytesOverride = _ => TestImages.Png(),
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(Game(), "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        Assert.Equal("5", selection.ProviderGameId);
        Assert.Empty(diagnostics);
    }

    private const string AmbiguousBody = """{ "data": [ { "id": 1, "name": "Test Game" }, { "id": 2, "name": "Test Game" } ] }""";
    private const string UnreadableBody = "{}"; // valid JSON, wrong shape

    [Theory]
    [InlineData(AmbiguousBody, "ambiguous")]
    [InlineData(UnreadableBody, "unavailable")]
    public void AfterASteamGridDbAmbiguityOrOutage_ASteamGameStillFallsBackToItsCachedSteamCdnCover(string searchBody, string expectedWording)
    {
        // The cascade after a failed SteamGridDB stage: Steam CDN is keyed by the launcher's own app id and never
        // guesses by name, so neither an ambiguous identity nor an unreadable response may stop it.
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(Path.Combine(_cacheDir, "steam-424242.jpg"), TestImages.Png(600, 900));
        var game = new GameEntry
        {
            Id = "steam-424242", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
            InstallDir = @"C:\Games\Test", Source = GameSource.Steam,
        };
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => searchBody,
            FetchGridImageBytesOverride = _ => throw new InvalidOperationException("must never download"),
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.SteamCdn, selection!.Provider);
        Assert.Equal(ArtworkRetrievalMethod.LocalCache, selection.RetrievedFrom);
        Assert.Equal("steam-424242", selection.ProviderGameId);
        Assert.True(game.IsCoverArt);
        Assert.Contains(expectedWording, Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFourSteamGridDbFallbackDiagnostics_AreDistinct_AndOnlyOneSaysNoConfidentMatch()
    {
        var texts = new[]
        {
            CoverArtService.DescribeSteamGridDbFallback("G", CoverLookupStatus.Ambiguous),
            CoverArtService.DescribeSteamGridDbFallback("G", CoverLookupStatus.NoMatch),
            CoverArtService.DescribeSteamGridDbFallback("G", CoverLookupStatus.IdentifiedWithoutUsableArt),
            CoverArtService.DescribeSteamGridDbFallback("G", CoverLookupStatus.Unavailable),
        };

        Assert.Equal(4, texts.Distinct().Count());
        Assert.Single(texts, t => t.Contains("no confident match", StringComparison.OrdinalIgnoreCase));
    }
}
