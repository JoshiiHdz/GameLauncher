using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;

namespace GameLauncher.Tests.Services;

/// <summary>Fortnite's own cover (the splash screen in its install folder), and sorting a game found in a watched folder under the
/// launcher its folder says it belongs to.</summary>
[Collection(WpfStaCollection.Name)]
public sealed class InstallFolderArtAndMarkersTests(WpfStaFixture sta) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-FolderArt-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static GameEntry Game(string name, string installDir, string exe, GameSource source = GameSource.Xbox) => new()
    {
        Id = "g-" + name, Name = name, ExecutablePath = exe, InstallDir = installDir, Source = source,
    };

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string WritePng(params string[] parts)
    {
        var path = Path.Combine([_directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[64 * 36 * 4];
        var bitmap = BitmapSource.Create(64, 36, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    // ---- Fortnite ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"XboxGames\Fortnite", @"XboxGames\Fortnite\Content\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    [InlineData(@"XboxGames\Fortnite\Content", @"XboxGames\Fortnite\Content\FortniteLauncher.exe")]
    [InlineData(@"XboxGames\Fortnite\Content\FortniteGame", @"XboxGames\Fortnite\Content\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    public void FortnitesSplashScreen_IsFound_WhicheverFolderTheInstallIsRecordedAs(string installDir, string exe)
    {
        var splash = Touch("XboxGames", "Fortnite", "Content", "Resources", "SplashScreen.png");
        var game = Game("Fortnite", Path.Combine(_directory, installDir), Path.Combine(_directory, exe));

        Assert.Equal(splash, InstallFolderArt.FindFile(game));
    }

    [Fact]
    public void OnlyFortnite_UsesItsInstallFolderForItsCover()
    {
        Touch("XboxGames", "Halo", "Content", "Resources", "SplashScreen.png");
        var halo = Game("Halo", Path.Combine(_directory, "XboxGames", "Halo"), Path.Combine(_directory, "XboxGames", "Halo", "Content", "halo.exe"));

        Assert.False(InstallFolderArt.UsesInstallFolderArt(halo));
        Assert.Null(InstallFolderArt.FindFile(halo));
    }

    [Fact]
    public void WithoutTheFile_NothingIsFound()
    {
        var game = Game("Fortnite", Path.Combine(_directory, "XboxGames", "Fortnite"), Path.Combine(_directory, "XboxGames", "Fortnite", "FortniteLauncher.exe"));

        Assert.Null(InstallFolderArt.FindFile(game));
    }

    [Fact]
    public void ItIsAppliedAsTheCover_ButNeverOverAChoiceTheUserMade() => sta.RunAsync(async () =>
    {
        var splash = WritePng("XboxGames", "Fortnite", "Content", "Resources", "SplashScreen.png");
        var installDir = Path.Combine(_directory, "XboxGames", "Fortnite");
        var mine = Game("Fortnite", installDir, Path.Combine(installDir, "FortniteLauncher.exe"));
        var pinned = Game("Fortnite", installDir, Path.Combine(installDir, "FortniteLauncher.exe"));
        var other = Game("Halo", installDir, Path.Combine(installDir, "halo.exe"));

        var changed = InstallFolderArt.Apply([mine, pinned, other], g => ReferenceEquals(g, pinned));

        Assert.Equal(1, changed);
        Assert.NotNull(mine.Icon);
        Assert.True(mine.IsCoverArt);
        Assert.True(mine.Icon!.IsFrozen);

        // The splash is landscape (the test image is 64x36): it is set on a portrait card-shaped plate, whole, not cropped to its middle.
        Assert.Equal(mine.Icon.PixelWidth * 3, mine.Icon.PixelHeight * 2);
        Assert.Null(pinned.Icon); // the user's own cover is left to the library
        Assert.Null(other.Icon);
        Assert.True(File.Exists(splash));
        await Task.CompletedTask;
    });

    [Fact]
    public void AnUnreadableImage_IsIgnored_AndTheAutomaticCoverStays() => sta.RunAsync(async () =>
    {
        Touch("XboxGames", "Fortnite", "Content", "Resources", "SplashScreen.png"); // not an image
        var installDir = Path.Combine(_directory, "XboxGames", "Fortnite");
        var game = Game("Fortnite", installDir, Path.Combine(installDir, "FortniteLauncher.exe"));
        var automatic = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, new byte[4], 2);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(automatic));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        game.Icon = image;

        Assert.Equal(0, InstallFolderArt.Apply([game], _ => false));
        Assert.Same(image, game.Icon);
        await Task.CompletedTask;
    });

    // ---- launcher markers --------------------------------------------------------------------------------

    [Fact]
    public void EachLaunchersMarker_NamesThatLauncher()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "epic", ".egstore"));
        Touch("gog", "goggame-1207658930.info");
        Touch("ea", "__Installer", "installerdata.xml");
        Touch("amazon", "fuel.json");
        Touch("xbox", "MicrosoftGame.config");
        Touch("none", "game.exe");

        Assert.Equal(GameSource.Epic, LauncherMarkers.Infer(Path.Combine(_directory, "epic")));
        Assert.Equal(GameSource.Gog, LauncherMarkers.Infer(Path.Combine(_directory, "gog")));
        Assert.Equal(GameSource.Ea, LauncherMarkers.Infer(Path.Combine(_directory, "ea")));
        Assert.Equal(GameSource.AmazonGames, LauncherMarkers.Infer(Path.Combine(_directory, "amazon")));
        Assert.Equal(GameSource.Xbox, LauncherMarkers.Infer(Path.Combine(_directory, "xbox")));
        Assert.Null(LauncherMarkers.Infer(Path.Combine(_directory, "none")));
    }

    [Fact]
    public void TheMarkerAtTheGamesRoot_IsFoundFromTheFolderTheExeIsIn()
    {
        Touch("ea", "__Installer", "installerdata.xml");
        Touch("ea", "Game", "Binaries", "Win64", "game.exe");

        Assert.Equal(GameSource.Ea, LauncherMarkers.Infer(Path.Combine(_directory, "ea", "Game", "Binaries", "Win64")));
        // Further than three folders from the marker is somebody else's folder: not claimed.
        Assert.Null(LauncherMarkers.Infer(Path.Combine(_directory, "ea", "Game", "Binaries", "Win64", "deeper")));
    }

    [Fact]
    public void ApplyingMarkers_MovesOnlyManualGames_AndLeavesTheRestAlone()
    {
        Touch("ea", "__Installer", "installerdata.xml");
        Touch("plain", "game.exe");
        var eaByHand = Game("Sims", Path.Combine(_directory, "ea"), Path.Combine(_directory, "ea", "sims.exe"), GameSource.Manual);
        var plain = Game("Indie", Path.Combine(_directory, "plain"), Path.Combine(_directory, "plain", "game.exe"), GameSource.Manual);
        var steam = Game("Hades", Path.Combine(_directory, "ea"), Path.Combine(_directory, "ea", "hades.exe"), GameSource.Steam);

        var changed = LauncherMarkers.Apply([eaByHand, plain, steam]);

        Assert.Equal(1, changed);
        Assert.Equal(GameSource.Ea, eaByHand.Source);
        Assert.Equal(GameSource.Manual, plain.Source); // no marker: stays "No launcher"
        Assert.Equal(GameSource.Steam, steam.Source);
    }
}
