using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>Covers the corrupt-cover-art-crashes-the-scan bug: BitmapImage.EndInit() throws a mix of
/// exception types (NotSupportedException, FileFormatException, ...) on bad image bytes, none of
/// which the callers used to catch, crashing the whole scan (and, for a corrupt cache file, every
/// future scan) instead of just falling back to the exe icon for that one game.</summary>
public class CoverArtDecoderTests
{
    // A real, minimal 1x1 transparent PNG - valid image bytes, not a placeholder string.
    private const string ValidOnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void ValidImageBytes_DecodesSuccessfully()
    {
        var bytes = Convert.FromBase64String(ValidOnePixelPngBase64);

        var result = CoverArtDecoder.Decode(bytes);

        Assert.NotNull(result);
        Assert.True(result.IsFrozen); // must be freezable to hand off across threads safely
    }

    [Fact]
    public void GarbageBytes_ReturnsNullInsteadOfThrowing()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };

        var result = CoverArtDecoder.Decode(bytes);

        Assert.Null(result);
    }

    [Fact]
    public void EmptyBytes_ReturnsNullInsteadOfThrowing()
    {
        var result = CoverArtDecoder.Decode([]);

        Assert.Null(result);
    }

    [Fact]
    public void TruncatedValidImage_ReturnsNullInsteadOfThrowing()
    {
        // Simulates an interrupted download or a write cut short by a crash - the leading bytes look
        // like a real PNG, but the file is cut off partway through.
        var fullBytes = Convert.FromBase64String(ValidOnePixelPngBase64);
        var truncated = fullBytes[..(fullBytes.Length / 2)];

        var result = CoverArtDecoder.Decode(truncated);

        Assert.Null(result);
    }
}
