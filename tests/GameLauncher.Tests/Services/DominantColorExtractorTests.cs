using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// DominantColorExtractor backs LibraryViewModel.HeroAccentColor, computed inside ApplyFilter on every
/// publish - a real, confirmed regression (caught by this project's own test suite, not inspection) had
/// it throw InvalidOperationException for an uninitialized BitmapImage and take the ENTIRE ApplyFilter
/// pass down with it, failing three unrelated artwork/legacy-id tests whose Icon stub happened to be
/// exactly that shape. These tests exist so that specific failure mode can never come back silently.
/// </summary>
public class DominantColorExtractorTests
{
    [Fact]
    public void NullSource_ReturnsNull()
    {
        Assert.Null(DominantColorExtractor.Extract(null));
    }

    [Fact]
    public void UninitializedBitmapImage_ReturnsNull_RatherThanThrowing()
    {
        // A bare `new BitmapImage()` with no BeginInit/EndInit ever called throws
        // InvalidOperationException from reading PixelWidth alone - the exact shape of the regression
        // this guards against. A cosmetic ambient-colour effect must never be able to crash a publish.
        var uninitialized = new BitmapImage();

        var result = DominantColorExtractor.Extract(uninitialized);

        Assert.Null(result);
    }

    [Fact]
    public void SolidRedBitmap_ExtractsAColorInTheRedFamily()
    {
        var pixels = new byte[4 * 8 * 8];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;       // B
            pixels[i + 1] = 0;   // G
            pixels[i + 2] = 220; // R
            pixels[i + 3] = 255; // A
        }
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);

        var result = DominantColorExtractor.Extract(bitmap);

        Assert.NotNull(result);
        Assert.True(result!.Value.R > result.Value.G, "a red source should extract a red-dominant colour");
        Assert.True(result.Value.R > result.Value.B, "a red source should extract a red-dominant colour");
    }

    [Fact]
    public void FullyTransparentBitmap_ReturnsNull_RatherThanAnArbitraryColor()
    {
        var pixels = new byte[4 * 8 * 8]; // every pixel alpha=0
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);

        var result = DominantColorExtractor.Extract(bitmap);

        Assert.Null(result);
    }

    [Fact]
    public void ResultIsAlwaysVisibleEnoughToTintWith_EvenFromANearBlackSource()
    {
        // Real box art is often dominated by a large dark background - a raw pixel average of that
        // would tint the hero an unreadable near-black. BoostForAmbientUse exists specifically to stop
        // that; this proves the boosted lightness never collapses back toward it.
        var pixels = new byte[4 * 8 * 8];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 8; pixels[i + 1] = 6; pixels[i + 2] = 10; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);

        var result = DominantColorExtractor.Extract(bitmap);

        Assert.NotNull(result);
        var maxChannel = Math.Max(result!.Value.R, Math.Max(result.Value.G, result.Value.B));
        Assert.True(maxChannel > 60, $"expected a visible tint, got R={result.Value.R} G={result.Value.G} B={result.Value.B}");
    }
}
