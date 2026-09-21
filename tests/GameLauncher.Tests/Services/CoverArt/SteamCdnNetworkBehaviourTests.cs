using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>B2 provider hardening, Steam CDN half: the same cancellation and deadline contract the searching
/// providers have (see ProviderNetworkBehaviourTests), for the one request Steam CDN makes. Steam CDN differs in
/// one deliberate way: it is not a status-returning provider, so every failure that is NOT the caller's
/// cancellation is a plain null ("no Steam cover"); the caller's cancellation must still escape - converting it
/// into null would let a cancelled scan carry on down the fallback chain.</summary>
public class SteamCdnNetworkBehaviourTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-SteamNet-" + Guid.NewGuid());
        _dirs.Add(dir);
        return dir;
    }

    private static GameEntry SteamGame() => new()
    {
        Id = "steam-424242", Name = "Steam Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Steam,
    };

    private static string CachePath(string dir) => Path.Combine(dir, "steam-424242.jpg");

    [Fact]
    public void ACallerTokenCancelledBeforeTheLookup_Throws_AndMakesNoRequest()
    {
        var dir = NewCacheDir();
        var handler = new FuncHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) });
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = handler };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => provider.GetCoverArt(SteamGame(), out _, dir, cts.Token));

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(CachePath(dir)));
    }

    [Fact]
    public void ACancelledToken_WinsOverAValidCachedCover()
    {
        // A scan that has been superseded must stop, not quietly keep serving covers.
        var dir = NewCacheDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(CachePath(dir), TestImages.Png(600, 900));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new SteamCoverArtProvider().GetCoverArt(SteamGame(), out _, dir, cts.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCallersCancellation_WhileWaitingForHeadersOrReadingTheBody_PropagatesAsCancellation_NotNull(bool whileReadingTheBody)
    {
        var dir = NewCacheDir();
        using var entered = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var handler = new FuncHttpHandler((_, ct) =>
        {
            entered.Set();
            if (whileReadingTheBody)
                return FakeIgdbHandler.StallingBody();

            FakeIgdbHandler.BlockUntilCancelled(ct);
            return new HttpResponseMessage();
        });
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = handler };

        var lookup = Task.Run(() => provider.GetCoverArt(SteamGame(), out _, dir, cts.Token));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the lookup never reached the download");
        var sw = Stopwatch.StartNew();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => lookup);
        sw.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation must end the blocked request promptly, took {sw.Elapsed}");
        Assert.False(File.Exists(CachePath(dir)));
    }

    [Fact]
    public void ACancellationBetweenDownloadingAndCaching_CachesNothing()
    {
        var dir = NewCacheDir();
        using var cts = new CancellationTokenSource();
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => { cts.Cancel(); return TestImages.Png(); } };

        Assert.ThrowsAny<OperationCanceledException>(() => provider.GetCoverArt(SteamGame(), out _, dir, cts.Token));

        Assert.False(File.Exists(CachePath(dir)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStall_PastTheDeadline_IsNoCover_NotACancellation_AndCachesNothing(bool whileReadingTheBody)
    {
        var dir = NewCacheDir();
        var handler = new FuncHttpHandler((_, ct) =>
        {
            if (whileReadingTheBody)
                return FakeIgdbHandler.StallingBody();

            FakeIgdbHandler.BlockUntilCancelled(ct);
            return new HttpResponseMessage();
        });
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = handler, DownloadTimeoutOverrideForTest = TimeSpan.FromMilliseconds(300) };

        var sw = Stopwatch.StartNew();
        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);
        sw.Stop();

        Assert.Null(art);
        Assert.False(fromCache);
        Assert.False(File.Exists(CachePath(dir)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"the download's own deadline should end this, took {sw.Elapsed}");
    }

    [Fact]
    public void ACallerCancellation_IsNeverSwallowedByTheBroadFailureCatch_EvenWhenItSurfacesAsATaskCanceledException()
    {
        // The catch that turns network failures into "no Steam cover" lists TaskCanceledException (HttpClient's own
        // timeout surfaces as one). The caller's cancellation is the same exception type - it must still escape.
        var dir = NewCacheDir();
        using var cts = new CancellationTokenSource();
        var provider = new SteamCoverArtProvider
        {
            FetchImageBytesOverride = _ => { cts.Cancel(); throw new TaskCanceledException("cancelled by the caller", null, cts.Token); },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => provider.GetCoverArt(SteamGame(), out _, dir, cts.Token));
    }
}
