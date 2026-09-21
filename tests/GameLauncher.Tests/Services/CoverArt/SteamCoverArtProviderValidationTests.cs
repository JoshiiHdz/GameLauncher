using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>B1: Steam CDN shares the same bounded download and the same ArtworkImageValidator as the other two
/// providers, on the download path AND the cache-hit path. Its download path previously had no test seam at
/// all (a static HttpClient) - the per-instance seams added for B1 are what make these possible.</summary>
public class SteamCoverArtProviderValidationTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-SteamValidation-" + Guid.NewGuid());
        _dirs.Add(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GameEntry SteamGame(string appId = "424242") => new()
    {
        Id = $"steam-{appId}", Name = "Steam Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Steam,
    };

    private static string CachePath(string dir, string appId = "424242") => Path.Combine(dir, $"steam-{appId}.jpg");

    // ---- Cache hits ----------------------------------------------------------------------------------------

    [Fact]
    public void ACacheHit_WithAnOversizedImage_IsNotServed_AndIsRefetched()
    {
        var dir = NewCacheDir();
        File.WriteAllBytes(CachePath(dir), TestImages.Png(9000, 100));
        var fresh = TestImages.Png(60, 90);
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => fresh };

        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);

        Assert.NotNull(art);
        Assert.False(fromCache);
        Assert.Equal(fresh, File.ReadAllBytes(CachePath(dir)));
    }

    [Fact]
    public void ACacheHit_WithAnOversizedImage_AndNothingToReplaceIt_ReturnsNull_AndDeletesIt()
    {
        var dir = NewCacheDir();
        File.WriteAllBytes(CachePath(dir), TestImages.Png(9000, 100));
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => null };

        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);

        Assert.Null(art);
        Assert.False(fromCache);
        Assert.False(File.Exists(CachePath(dir)));
    }

    [Fact]
    public void ACacheFile_OverTheByteCap_IsNotLoadedInFull_AndIsRefetched()
    {
        var dir = NewCacheDir();
        using (var f = File.Create(CachePath(dir)))
            f.SetLength(ArtworkImageValidator.MaxFileBytes + 1L);
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => TestImages.Png() };

        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);

        Assert.NotNull(art);
        Assert.False(fromCache);
        Assert.True(new FileInfo(CachePath(dir)).Length < ArtworkImageValidator.MaxFileBytes);
    }

    [Fact]
    public void ACacheFile_TooLargeToEverLoadIntoMemory_IsAnInvalidEntry_NotAFailure()
    {
        // File.ReadAllBytes cannot read a file over 2GB (it throws, which this provider would swallow into a
        // null); the bounded read treats it as an invalid entry to delete and re-fetch. SetLength writes no data.
        var dir = NewCacheDir();
        using (var f = File.Create(CachePath(dir)))
            f.SetLength(int.MaxValue + 1L);
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => TestImages.Png() };

        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);

        Assert.NotNull(art);
        Assert.False(fromCache);
    }

    [Fact]
    public void AValidCacheHit_IsStillServed_WithoutDownloading()
    {
        var dir = NewCacheDir();
        File.WriteAllBytes(CachePath(dir), TestImages.Png(600, 900));
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => throw new InvalidOperationException("must not download") };

        var art = provider.GetCoverArt(SteamGame(), out var fromCache, dir);

        Assert.NotNull(art);
        Assert.True(fromCache);
    }

    // ---- Downloads -------------------------------------------------------------------------------------------

    [Fact]
    public void AValidDownload_IsCached_AndTheNextLookupIsServedFromIt()
    {
        var dir = NewCacheDir();
        var downloads = 0;
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => { downloads++; return TestImages.Png(); } };

        var first = provider.GetCoverArt(SteamGame(), out var firstFromCache, dir);
        var second = provider.GetCoverArt(SteamGame(), out var secondFromCache, dir);

        Assert.NotNull(first);
        Assert.False(firstFromCache);
        Assert.NotNull(second);
        Assert.True(secondFromCache);
        Assert.Equal(1, downloads);
    }

    public static IEnumerable<object[]> UnusableImages()
    {
        yield return new object[] { "over the width limit", TestImages.Png(9000, 100) };
        yield return new object[] { "over the height limit", TestImages.Png(100, 9000) };
        yield return new object[] { "not an image", new byte[] { 1, 2, 3, 4 } };
        yield return new object[] { "empty", Array.Empty<byte>() };
    }

    [Theory]
    [MemberData(nameof(UnusableImages))]
    public void AnUnusableDownload_ReturnsNull_AndIsNeverCached(string why, byte[] bytes)
    {
        var dir = NewCacheDir();
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => bytes };

        var art = provider.GetCoverArt(SteamGame(), out _, dir);

        Assert.Null(art);
        Assert.False(File.Exists(CachePath(dir)), $"{why}: must never reach the cache");
    }

    [Fact]
    public void ANonSteamEntry_IsUnchanged_ReturnsNullWithoutTouchingTheSeam()
    {
        var game = new GameEntry
        {
            Id = "manual-x", Name = "X", ExecutablePath = @"C:\x.exe", InstallDir = @"C:\", Source = GameSource.Manual,
        };
        var provider = new SteamCoverArtProvider { FetchImageBytesOverride = _ => throw new InvalidOperationException("must not download") };

        Assert.Null(provider.GetCoverArt(game, out _, NewCacheDir()));
    }

    // ---- The real transport ------------------------------------------------------------------------------------

    [Fact]
    public void ARealDownloadThroughTheHandler_IsValidatedAndCached()
    {
        var dir = NewCacheDir();
        var handler = new FuncHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png()) });
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = handler };

        var art = provider.GetCoverArt(SteamGame(), out _, dir);

        Assert.NotNull(art);
        Assert.Contains("424242", handler.Urls.Single());
        Assert.True(File.Exists(CachePath(dir)));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void ANonSuccessCdnStatus_ReturnsNull_AndNothingIsCached(HttpStatusCode code)
    {
        var dir = NewCacheDir();
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = new FuncHttpHandler((_, _) => new HttpResponseMessage(code)) };

        Assert.Null(provider.GetCoverArt(SteamGame(), out _, dir));
        Assert.False(File.Exists(CachePath(dir)));
    }

    [Fact]
    public void AnEndlessCdnBody_IsCapped_AndNothingIsCached()
    {
        var dir = NewCacheDir();
        var provider = new SteamCoverArtProvider { HttpHandlerOverrideForTest = new FuncHttpHandler((_, _) => FakeIgdbHandler.EndlessBody()) };

        var sw = Stopwatch.StartNew();
        var art = provider.GetCoverArt(SteamGame(), out _, dir);
        sw.Stop();

        Assert.Null(art);
        Assert.False(File.Exists(CachePath(dir)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"the cap should end this promptly, took {sw.Elapsed}");
    }

    [Fact]
    public void AStalledCdnBody_ReturnsNull_WithinItsTimeBudget()
    {
        var dir = NewCacheDir();
        var provider = new SteamCoverArtProvider
        {
            HttpHandlerOverrideForTest = new FuncHttpHandler((_, _) => FakeIgdbHandler.StallingBody()),
            DownloadTimeoutOverrideForTest = TimeSpan.FromMilliseconds(300),
        };

        var sw = Stopwatch.StartNew();
        var art = provider.GetCoverArt(SteamGame(), out _, dir);
        sw.Stop();

        Assert.Null(art);
        Assert.False(File.Exists(CachePath(dir)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"the download's own budget should end this, took {sw.Elapsed}");
    }
}
