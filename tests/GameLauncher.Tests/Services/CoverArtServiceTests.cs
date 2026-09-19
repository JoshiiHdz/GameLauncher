using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>Covers ApplyStored/ApplyStoredSafely/TryDecodeStored (the stored-user-selection path) and
/// ApplyIconFallbackSafely (the shared crash-isolated exe-icon fallback used everywhere in this codebase
/// that needs one). Apply's own automatic-matcher path is exercised indirectly via
/// GameScannerServiceTests/SteamGridDbCoverArtProviderTests instead, since it needs real network mocking
/// this class doesn't provide.
///
/// Uses an ISOLATED asset-store directory (ArtworkAssetStore's storeDirOverride) rather than the real
/// %AppData%\GameLauncher\CustomCovers - a real, confirmed gap in an earlier version of this suite wrote
/// real files there.</summary>
public class CoverArtServiceTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    private string WriteAsset(byte[] bytes) => ArtworkAssetStore.Write(bytes, "png", _storeDir);

    private static byte[] MakePng(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static GameEntry MakeGame() => new()
    {
        Id = "manual-test",
        Name = "Test Game",
        ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test",
        Source = GameSource.Manual,
    };

    // ---- TryDecodeStored --------------------------------------------------------------------------------

    [Fact]
    public void TryDecodeStored_ValidAsset_ReturnsDecodedBitmap()
    {
        var assetId = WriteAsset(MakePng());
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        var bitmap = CoverArtService.TryDecodeStored(selection, _storeDir);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void TryDecodeStored_MissingAsset_ReturnsNull_DoesNotThrow()
    {
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        Assert.Null(CoverArtService.TryDecodeStored(selection, _storeDir));
    }

    [Fact]
    public void TryDecodeStored_DoesNotTouchTheGivenGameEntry()
    {
        // TryDecodeStored takes no GameEntry at all - proving the signature itself, since this is what
        // makes it safe to call from a background thread for a batch of different games without any of
        // them being the live, UI-bound instance.
        var assetId = WriteAsset(MakePng());
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        CoverArtService.TryDecodeStored(selection, _storeDir);
        // No GameEntry parameter exists to assert against - this test exists purely to document/pin the
        // signature; a future accidental parameter addition would be a deliberate, reviewable API change.
    }

    // ---- ApplyStored ----------------------------------------------------------------------------------

    [Fact]
    public void ApplyStored_ValidAsset_AppliesIconAndReturnsTrue()
    {
        var assetId = WriteAsset(MakePng());
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        var applied = CoverArtService.ApplyStored(game, selection, _storeDir);

        Assert.True(applied);
        Assert.NotNull(game.Icon);
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStored_MissingAsset_ReturnsFalseWithoutThrowing()
    {
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };

        Assert.False(CoverArtService.ApplyStored(game, selection, _storeDir));
    }

    [Fact]
    public void ApplyStored_CorruptAsset_ReturnsFalseWithoutThrowing()
    {
        var assetId = WriteAsset([1, 2, 3, 4, 5]); // not a real image
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        Assert.False(CoverArtService.ApplyStored(game, selection, _storeDir));
    }

    // ---- ApplyStoredSafely: retain the preference, never crash, never guess different artwork --------

    [Fact]
    public void ApplyStoredSafely_ValidAsset_AppliesIcon()
    {
        var assetId = WriteAsset(MakePng());
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        CoverArtService.ApplyStoredSafely(game, selection, storeDirOverride: _storeDir);

        Assert.NotNull(game.Icon);
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStoredSafely_MissingAsset_FallsBackToIcon_SelectionNotSubstituted()
    {
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        var fallbackIcon = new BitmapImage();

        CoverArtService.ApplyStoredSafely(game, selection, getIcon: _ => fallbackIcon, storeDirOverride: _storeDir);

        Assert.Same(fallbackIcon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStoredSafely_MissingAsset_AndIconFallbackAlsoThrows_LeavesIconNull_DoesNotThrow()
    {
        // The exact "both fail" case point 1 requires: loading the stored asset fails (missing), AND
        // the icon fallback itself throws - the method must still return normally, Icon null, IsCoverArt
        // false, never letting the second failure escape.
        var game = MakeGame();
        game.Icon = new BitmapImage(); // pre-existing value must be cleared, not left stale
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };

        var exception = Record.Exception(() => CoverArtService.ApplyStoredSafely(
            game, selection, getIcon: _ => throw new InvalidOperationException("simulated icon extraction failure"), storeDirOverride: _storeDir));

        Assert.Null(exception);
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }

    // ---- ApplyIconFallbackSafely: the shared, never-throws exe-icon fallback -------------------------

    [Fact]
    public void ApplyIconFallbackSafely_NormalIcon_Applies()
    {
        var game = MakeGame();
        var icon = new BitmapImage();

        CoverArtService.ApplyIconFallbackSafely(game, _ => icon);

        Assert.Same(icon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void ApplyIconFallbackSafely_ThrowingGetIcon_LeavesIconNull_DoesNotThrow()
    {
        var game = MakeGame();
        game.Icon = new BitmapImage(); // pre-existing value must be cleared, not left stale

        var exception = Record.Exception(() => CoverArtService.ApplyIconFallbackSafely(
            game, _ => throw new InvalidOperationException("simulated icon extraction failure")));

        Assert.Null(exception);
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }
}
