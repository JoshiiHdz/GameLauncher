using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>B2: through the real coordinator (CoverArtService.Apply) with REAL providers behind their own
/// transport seams -
///  - the scan's cancellation now reaches SteamGridDB (it used to stop at IGDB): a request stalled in that stage
///    ends promptly as a CANCELLATION, and the fallback chain (Steam CDN, the exe icon) never starts;
///  - a stall or an oversized body in an earlier provider is only an outage (Unavailable), and the chain still
///    falls through to the next provider.
///
/// In the IGDB static-state collection: the IGDB cases use IGDB's shared rate-limit clock.</summary>
[Collection(IgdbStaticStateCollection.Name)]
public class CoverArtServiceCancellationTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-ApplyCancel-" + Guid.NewGuid());

    public CoverArtServiceCancellationTests()
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

    private static GameEntry SteamGame() => new()
    {
        Id = "steam-424242", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Steam,
    };

    private void PrepopulateSteamCdnCache()
    {
        // The Steam CDN stage resolves from this isolated cache without any network.
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(Path.Combine(_cacheDir, "steam-424242.jpg"), TestImages.Png(600, 900));
    }

    private const string SearchOk = """{ "data": [ { "id": 5, "name": "Test Game" } ] }""";

    // ---- Cancellation reaches SteamGridDB ------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScansCancellation_DuringTheSteamGridDbSearch_EndsPromptly_AndTheFallbackChainNeverStarts(bool whileReadingTheBody)
    {
        PrepopulateSteamCdnCache();
        var game = SteamGame();
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) =>
            {
                entered.Set();
                if (whileReadingTheBody)
                    return FakeIgdbHandler.StallingBody();

                FakeIgdbHandler.BlockUntilCancelled(ct);
                return new HttpResponseMessage();
            }),
        };

        var apply = Task.Run(() => CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, ct: cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Apply never reached the SteamGridDB request");
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => apply);
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the scan's cancellation must end the stalled request, took {sw.Elapsed}");
        // The Steam CDN stage (which WOULD have resolved from its cache) and the exe-icon fallback never ran:
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public async Task TheScansCancellation_DuringTheIgdbStage_NeverStartsSteamGridDb()
    {
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) =>
            {
                entered.Set();
                FakeIgdbHandler.BlockUntilCancelled(ct);
                return new HttpResponseMessage();
            }),
        };
        var sgdb = new SteamGridDbCoverArtProvider("k") { SearchGameIdOverride = _ => throw new InvalidOperationException("must not start after a cancellation") };
        var game = SteamGame();

        var apply = Task.Run(() => CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, sgdb, _cacheDir, cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => apply);

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Null(game.Icon);
    }

    // ---- ...and the Steam CDN stage: the last network stage before the exe icon ---------------------------------------

    private static SteamGridDbCoverArtProvider NoSteamGridDbMatch() => new("k") { SearchGameIdOverride = _ => null };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScansCancellation_DuringTheSteamCdnDownload_EndsPromptly_NotAsAnExeIconFallback(bool whileReadingTheBody)
    {
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var steam = new SteamCoverArtProvider
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) =>
            {
                entered.Set();
                if (whileReadingTheBody)
                    return FakeIgdbHandler.StallingBody();

                FakeIgdbHandler.BlockUntilCancelled(ct);
                return new HttpResponseMessage();
            }),
        };
        var game = SteamGame();

        var apply = Task.Run(() => CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: NoSteamGridDbMatch(),
            cacheDirOverride: _cacheDir, ct: cts.Token, steamProviderOverride: steam));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "Apply never reached the Steam CDN request");
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => apply);
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the scan's cancellation must end the stalled download, took {sw.Elapsed}");
        Assert.False(game.IsCoverArt); // never recorded as "no Steam cover, use the exe icon"
    }

    [Fact]
    public void AStalledSteamCdnDownload_PastItsDeadline_FallsBackToTheExeIcon_NotAHang()
    {
        var steam = new SteamCoverArtProvider
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return new HttpResponseMessage(); }),
            DownloadTimeoutOverrideForTest = TimeSpan.FromMilliseconds(300),
        };
        var game = SteamGame();

        var sw = Stopwatch.StartNew();
        var selection = CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: NoSteamGridDbMatch(),
            cacheDirOverride: _cacheDir, steamProviderOverride: steam);
        sw.Stop();

        Assert.Null(selection); // no cover from any provider: the exe icon, exactly as for an outright Steam CDN failure
        Assert.False(game.IsCoverArt);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the download's own deadline should end the stall, took {sw.Elapsed}");
    }

    // ---- A stall or an oversized body is only an outage: the chain falls through -------------------------------------

    [Fact]
    public void AStalledSteamGridDbSearch_IsAnOutage_AndASteamGameFallsBackToItsCachedSteamCdnCover()
    {
        PrepopulateSteamCdnCache();
        var game = SteamGame();
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return new HttpResponseMessage(); }),
            RequestTimeoutOverrideForTest = TimeSpan.FromMilliseconds(300),
        };
        var diagnostics = new List<string>();

        var sw = Stopwatch.StartNew();
        var selection = CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);
        sw.Stop();

        Assert.Equal(ArtworkProvider.SteamCdn, selection!.Provider);
        Assert.Equal(ArtworkRetrievalMethod.LocalCache, selection.RetrievedFrom);
        Assert.Contains("unavailable", Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the request's own deadline should end the stall, took {sw.Elapsed}");
    }

    [Fact]
    public void AnOversizedSteamGridDbSearchBody_IsAnOutage_AndASteamGameFallsBackToItsCachedSteamCdnCover()
    {
        PrepopulateSteamCdnCache();
        var game = SteamGame();
        var provider = new SteamGridDbCoverArtProvider("k")
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, _) => FakeIgdbHandler.EndlessBody()),
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", steamGridDbProviderOverride: provider,
            cacheDirOverride: _cacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Equal(ArtworkProvider.SteamCdn, selection!.Provider);
        Assert.Contains("unavailable", Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStalledIgdbSearch_IsAnOutage_AndSteamGridDbStillGetsItsTurn()
    {
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, ct) => { FakeIgdbHandler.BlockUntilCancelled(ct); return new HttpResponseMessage(); }),
            RequestTimeoutOverrideForTest = TimeSpan.FromMilliseconds(300),
        };
        var sgdb = new SteamGridDbCoverArtProvider("k")
        {
            SearchRequestOverride = _ => SearchOk,
            FetchGridImageBytesOverride = _ => TestImages.Png(),
        };
        var game = SteamGame();
        var diagnostics = new List<string>();

        var sw = Stopwatch.StartNew();
        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, sgdb, _cacheDir, diagnosticSinkForTest: diagnostics.Add);
        sw.Stop();

        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        Assert.Contains("unavailable", Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase); // only IGDB's stage emitted one
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
    }
}
