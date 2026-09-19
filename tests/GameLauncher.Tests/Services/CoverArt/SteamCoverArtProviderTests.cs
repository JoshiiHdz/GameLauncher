using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>Covers only the parts of SteamCoverArtProvider reachable without a real network round-trip -
/// its cache-hit path (via `cacheDirOverride`, isolated from the real %AppData%\GameLauncher\
/// CoverArtCache) and the retrieval-evidence (`servedFromCache`) it reports. The download path itself
/// isn't unit-testable here (a static HttpClient with no seam), matching this class's existing scope.</summary>
public class SteamCoverArtProviderTests
{
    private static byte[] MakeValidPngBytes()
    {
        var pixels = new byte[8 * 8];
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Gray8, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static GameEntry MakeSteamGame(string appId) => new()
    {
        Id = $"steam-{appId}",
        Name = "Test Game",
        ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test",
        Source = GameSource.Steam,
    };

    [Fact]
    public void GetCoverArt_ValidPreExistingCacheFile_ServedFromCache_ReturnsTrue()
    {
        // CoverArtService.Apply needs to know a result came from THIS provider's own on-disk cache
        // rather than a fresh network fetch just now, so it can record accurate ArtworkRetrievalMethod
        // evidence instead of manufacturing "NetworkDownload" for what was actually a cache hit.
        var cacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-CoverArtCache-" + Guid.NewGuid());
        try
        {
            var game = MakeSteamGame("999999999999"); // implausible as a real Steam app id
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "steam-999999999999.jpg"), MakeValidPngBytes());

            var provider = new SteamCoverArtProvider();
            var result = provider.GetCoverArt(game, out var servedFromCache, cacheDir);

            Assert.NotNull(result);
            Assert.True(servedFromCache);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void GetCoverArt_NonSteamGame_ReturnsNull_ServedFromCacheIsFalse()
    {
        var game = new GameEntry
        {
            Id = "manual-test", Name = "Test", ExecutablePath = @"C:\g.exe", InstallDir = @"C:\",
            Source = GameSource.Manual,
        };

        var result = new SteamCoverArtProvider().GetCoverArt(game, out var servedFromCache);

        Assert.Null(result);
        Assert.False(servedFromCache);
    }
}
