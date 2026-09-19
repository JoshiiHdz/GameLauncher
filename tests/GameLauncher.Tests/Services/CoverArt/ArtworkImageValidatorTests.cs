using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

public class ArtworkImageValidatorTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static byte[] MakePng(int width, int height)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] MakeJpeg(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private string MakeTempFile(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Image-{Guid.NewGuid()}.{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    // ---- ValidateBytes ------------------------------------------------------------------------------

    [Fact]
    public void ValidateBytes_ValidSmallImage_Accepted()
    {
        var bytes = MakePng(64, 96);
        var result = ArtworkImageValidator.ValidateBytes(bytes, "test");

        Assert.NotNull(result);
        Assert.Equal("png", result!.Extension);
        Assert.Equal(bytes, result.Bytes);
    }

    [Fact]
    public void ValidateBytes_Empty_Rejected()
    {
        Assert.Null(ArtworkImageValidator.ValidateBytes([], "test"));
    }

    [Fact]
    public void ValidateBytes_ExceedsByteLimit_Rejected()
    {
        var tooBig = new byte[ArtworkImageValidator.MaxFileBytes + 1];
        Assert.Null(ArtworkImageValidator.ValidateBytes(tooBig, "test"));
    }

    [Fact]
    public void ValidateBytes_CorruptImageData_Rejected_NotThrown()
    {
        // Syntactically a file (non-empty, under the size cap) but not a real, decodable image at all -
        // must be rejected cleanly, not throw.
        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Null(ArtworkImageValidator.ValidateBytes(garbage, "test"));
    }

    [Fact]
    public void ValidateBytes_DimensionsReadableButFullDecodeFails_Rejected()
    {
        // Exactly the gap a dimensions-only check would miss: BitmapDecoder.Create's DelayCreation probe
        // can succeed (it only reads frame metadata) while the pixel data itself is unusable - a full
        // decode must be required too, or this would be staged as a selection that can never display.
        // decodeOverride simulates that outcome directly and deterministically: real-world WIC decoders
        // proved, empirically, far too fault-tolerant (silently absorbing both interior bit-flips and
        // heavy truncation rather than throwing) to reliably construct a genuine byte-level fixture for
        // this specific combination - this tests the REQUIREMENT itself instead.
        var bytes = MakePng(64, 64); // passes the dimension probe on its own
        var result = ArtworkImageValidator.ValidateBytes(bytes, "test", decodeOverride: _ => null);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateBytes_FullDecodeSucceeds_UsesTheDecodedResult_NotJustDimensions()
    {
        // Companion to the test above: proves decodeOverride is actually wired into the normal path
        // (not just silently unused), so a null result above is meaningful evidence of a rejection, not
        // an artifact of a seam that never fires.
        var bytes = MakePng(64, 64);
        var decodeCalls = 0;
        var result = ArtworkImageValidator.ValidateBytes(bytes, "test", decodeOverride: b => { decodeCalls++; return CoverArtDecoder.Decode(b); });

        Assert.NotNull(result);
        Assert.Equal(1, decodeCalls);
    }

    [Fact]
    public void ValidateBytes_WidthExceedsDimensionLimit_Rejected()
    {
        // A real, valid, tiny-in-BYTES image (1px tall) whose WIDTH alone exceeds the cap - this is
        // exactly the case CoverArtDecoder.Decode's 320px-wide downsampling could never have caught,
        // since it would only ever see the ALREADY-DOWNSAMPLED result, not this original.
        var bytes = MakePng(ArtworkImageValidator.MaxDimensionPixels + 1, 1);
        Assert.Null(ArtworkImageValidator.ValidateBytes(bytes, "test"));
    }

    [Fact]
    public void ValidateBytes_HeightExceedsDimensionLimit_Rejected()
    {
        var bytes = MakePng(1, ArtworkImageValidator.MaxDimensionPixels + 1);
        Assert.Null(ArtworkImageValidator.ValidateBytes(bytes, "test"));
    }

    [Fact]
    public void ValidateBytes_DimensionsWithinLimit_Accepted()
    {
        var bytes = MakePng(ArtworkImageValidator.MaxDimensionPixels, 1);
        Assert.NotNull(ArtworkImageValidator.ValidateBytes(bytes, "test"));
    }

    [Fact]
    public void ValidateBytes_JpegContent_DetectedAsJpegRegardlessOfSourceLabel()
    {
        // "sourceForLogging" (a path/name) plays no part in the returned Extension - content alone
        // decides it, proven here by passing a label that doesn't even look like a filename.
        var bytes = MakeJpeg(64, 64);
        var result = ArtworkImageValidator.ValidateBytes(bytes, "not-a-path-at-all");

        Assert.NotNull(result);
        Assert.Equal("jpg", result!.Extension);
    }

    // ---- ValidateLocalFileAsync ----------------------------------------------------------------------

    [Fact]
    public async Task ValidateLocalFileAsync_ValidFile_Accepted()
    {
        var path = MakeTempFile(MakePng(64, 96), "png");
        var result = await ArtworkImageValidator.ValidateLocalFileAsync(path);

        Assert.NotNull(result);
        Assert.Equal("png", result!.Extension);
    }

    [Fact]
    public async Task ValidateLocalFileAsync_JpegMisnamedAsPng_AcceptedAndCorrectlyLabeledFromContent()
    {
        // The exact "JPEG renamed .png" scenario: the file's own name/extension is never trusted - only
        // what WIC actually sniffs from the bytes decides the stored Extension. Previously this content
        // would have been silently mislabeled "png" (trusting the file name) instead of being correctly
        // detected and stored as "jpg".
        var path = MakeTempFile(MakeJpeg(64, 96), "png");
        var result = await ArtworkImageValidator.ValidateLocalFileAsync(path);

        Assert.NotNull(result);
        Assert.Equal("jpg", result!.Extension);
    }

    [Fact]
    public async Task ValidateLocalFileAsync_ExceedsByteLimitDuringRead_Rejected()
    {
        // Enforced against actual bytes read during the read loop itself, not only an upfront
        // FileInfo.Length check - this file's reported length already exceeds the cap, proving the read
        // is bounded rather than merely gated by a pre-check.
        var path = MakeTempFile(new byte[ArtworkImageValidator.MaxFileBytes + 1024], "png");
        Assert.Null(await ArtworkImageValidator.ValidateLocalFileAsync(path));
    }

    [Fact]
    public async Task ValidateLocalFileAsync_MissingFile_RejectedWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Missing-{Guid.NewGuid()}.png");
        Assert.Null(await ArtworkImageValidator.ValidateLocalFileAsync(path));
    }

    [Fact]
    public async Task ValidateLocalFileAsync_CorruptFile_RejectedWithoutThrowing()
    {
        var path = MakeTempFile([1, 2, 3, 4, 5], "png");
        Assert.Null(await ArtworkImageValidator.ValidateLocalFileAsync(path));
    }

    [Fact]
    public async Task ValidateLocalFileAsync_DimensionsReadableButFullDecodeFails_Rejected()
    {
        var path = MakeTempFile(MakePng(64, 64), "png");
        Assert.Null(await ArtworkImageValidator.ValidateLocalFileAsync(path, decodeOverride: _ => null));
    }
}
