using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;

namespace GameLauncher.Tests.Services;

[Collection(WpfStaCollection.Name)]
public sealed class BackdropArtServiceTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Backdrop-" + Guid.NewGuid());

    public void Dispose()
    {
        BackdropArtService.FetchOverrideForTest = null;
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }

    private static GameEntry Game(string id, GameSource source) =>
        new() { Id = id, Name = "Game", Source = source, ExecutablePath = @"C:\G\g.exe", InstallDir = @"C:\G" };

    private static byte[] WideImage(int width = 800, int height = 400)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void OnlySteamGamesHaveAHeroToAskFor()
    {
        Assert.Equal("570", BackdropArtService.SteamAppId(Game("steam-570", GameSource.Steam)));
        Assert.Null(BackdropArtService.SteamAppId(Game("epic-abc", GameSource.Epic)));
        Assert.Null(BackdropArtService.SteamAppId(Game("steam-570", GameSource.Manual)));
    }

    [Fact]
    public void ADownloadedHero_IsDecoded_Cached_AndNotDownloadedAgain() => sta.RunAsync(async () =>
    {
        var calls = 0;
        BackdropArtService.FetchOverrideForTest = id => { calls++; return WideImage(); };

        var first = BackdropArtService.Load("570", _cache);
        var second = BackdropArtService.Load("570", _cache);

        Assert.NotNull(first);
        Assert.True(first!.IsFrozen);
        Assert.Equal(800, first.PixelWidth);
        Assert.NotNull(second);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(_cache, "steam-570-hero.jpg")));
        await Task.CompletedTask;
    });

    [Fact]
    public void AnAppWithNoHero_IsRemembered_SoSteamIsNotAskedAgain() => sta.RunAsync(async () =>
    {
        var calls = 0;
        BackdropArtService.FetchOverrideForTest = id => { calls++; return null; };

        Assert.Null(BackdropArtService.Load("999", _cache));
        Assert.Null(BackdropArtService.Load("999", _cache));

        Assert.Equal(1, calls);
        Assert.True(File.Exists(Path.Combine(_cache, "steam-999-hero.none")));
        await Task.CompletedTask;
    });

    [Fact]
    public void BytesThatAreNotAnImage_GiveNothing_AndCacheNothing() => sta.RunAsync(async () =>
    {
        BackdropArtService.FetchOverrideForTest = id => "<html>not found</html>"u8.ToArray();

        Assert.Null(BackdropArtService.Load("570", _cache));

        Assert.False(File.Exists(Path.Combine(_cache, "steam-570-hero.jpg")));
        await Task.CompletedTask;
    });

    [Fact]
    public void ATinyImage_IsNotUsedAsABackground() => sta.RunAsync(async () =>
    {
        Assert.Null(BackdropArtService.Decode(WideImage(64, 64)));
        await Task.CompletedTask;
    });
}
