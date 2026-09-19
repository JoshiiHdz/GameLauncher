using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services.CoverArt;

/// <summary>Validates a candidate cover image - a user-picked local file today, a provider download in
/// Phase 1b - BEFORE it's ever staged into ArtworkAssetStore. CoverArtDecoder.Decode hardcodes
/// DecodePixelWidth = 320 for display purposes, so checking ITS output against a dimension limit would
/// only ever see an already-downsampled image and could never actually reject an oversized original -
/// this reads the ORIGINAL file's real dimensions separately, before that decode ever runs.</summary>
public static class ArtworkImageValidator
{
    public const int MaxFileBytes = 20 * 1024 * 1024; // 20MB - a generous cap for a cover image specifically
    public const int MaxDimensionPixels = 8000;
    public const long MaxTotalPixels = 64_000_000; // 64 megapixels

    /// <summary>Extension is DETECTED FROM CONTENT (see TryReadOriginalImageInfo), never trusted from a
    /// file's own name - a real .jpg renamed to .png must be stored and later re-decoded as what it
    /// actually is, or ArtworkAssetStore would end up holding JPEG bytes under a ".png" name.
    ///
    /// DecodedImage is the SAME frozen bitmap the full-decode check below already had to produce to
    /// prove this file is displayable - handed back instead of discarded, so a caller that goes on to
    /// stage and commit this exact selection (LibraryViewModel.ApplyLocalCoverImageAsync) can apply it
    /// directly instead of re-reading the just-written asset file and re-decoding it a second time,
    /// synchronously, on the UI thread, in CommitArtworkChange.</summary>
    public sealed record ValidatedImage(byte[] Bytes, string Extension, BitmapImage DecodedImage);

    /// <summary>Reads `path` under a hard byte cap enforced DURING the read (not only via an upfront
    /// FileInfo.Length check a lying or concurrently-changing file could defeat), then validates it the
    /// same way ValidateBytes does. Never throws - every rejection reason is logged and returns null.
    /// The file's own extension is not consulted at all - see ValidateBytes.</summary>
    public static async Task<ValidatedImage?> ValidateLocalFileAsync(string path, CancellationToken ct = default,
        Func<byte[], BitmapImage?>? decodeOverride = null)
    {
        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(path, MaxFileBytes, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Rejected cover image '{path}': couldn't read it (or it exceeds the {MaxFileBytes}-byte limit).", ex);
            return null;
        }

        return ValidateBytes(bytes, path, decodeOverride);
    }

    /// <summary>Same content validation for bytes already in memory (a provider download, Phase 1b).
    /// Real codec work (dimension probe AND full decode, see their own remarks) - callers on the UI
    /// thread must run this via Task.Run themselves; it is not internally offloaded, so a caller that
    /// already knows it's off-thread (a background scan) isn't forced through an extra hop.
    ///
    /// `decodeOverride` is test-only (production never passes it, always using the real
    /// CoverArtDecoder.Decode) - a per-call seam rather than a shared mutable field, safe under xUnit's
    /// default parallel test execution. It exists because WIC's real decoders proved, empirically, far
    /// too fault-tolerant to reliably construct a byte-level fixture that passes the dimension probe but
    /// fails a full decode (truncation and interior bit-flipping were both silently absorbed rather than
    /// rejected) - this seam tests the REQUIREMENT (a null full decode is rejected) directly and
    /// deterministically instead of depending on a specific decoder's exact fault tolerance.</summary>
    public static ValidatedImage? ValidateBytes(byte[] bytes, string sourceForLogging,
        Func<byte[], BitmapImage?>? decodeOverride = null)
    {
        if (bytes.Length == 0)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': empty.");
            return null;
        }

        if (bytes.Length > MaxFileBytes)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': {bytes.Length} bytes exceeds the {MaxFileBytes}-byte limit.");
            return null;
        }

        var info = TryReadOriginalImageInfo(bytes);
        if (info is null)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': couldn't read its dimensions (corrupt or unsupported format).");
            return null;
        }

        var (width, height, detectedExtension) = info.Value;
        if (width <= 0 || height <= 0 || width > MaxDimensionPixels || height > MaxDimensionPixels)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': dimensions {width}x{height} exceed the {MaxDimensionPixels}px limit.");
            return null;
        }

        if ((long)width * height > MaxTotalPixels)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': {width}x{height} ({(long)width * height} px) exceeds the {MaxTotalPixels}-pixel total limit.");
            return null;
        }

        // Dimensions alone aren't proof the image is actually displayable - BitmapCreateOptions.
        // DelayCreation can successfully read frame metadata while the pixel data itself is damaged, and
        // that failure would only otherwise surface later, invisibly, the first time something tries to
        // actually show this stored selection (see CoverArtService.ApplyStored's own null-return
        // contract). Requiring a full decode to succeed here means a bad file is rejected at selection
        // time, with a clear reason, instead of silently staging a selection that can never display.
        var decode = decodeOverride ?? CoverArtDecoder.Decode;
        var decoded = decode(bytes);
        if (decoded is null)
        {
            Logger.Warn($"Rejected cover image '{sourceForLogging}': dimensions were readable but the image failed to decode (damaged pixel data or unsupported variant).");
            return null;
        }

        return new ValidatedImage(bytes, detectedExtension, decoded);
    }

    /// <summary>Reads the image's own, original dimensions AND its actual container format (sniffed from
    /// content by WIC - never the caller-supplied file name/extension, which can lie). Deliberately NOT
    /// via CoverArtDecoder.Decode for the dimension read, which always downsamples to 320px for display
    /// and so can never be used to validate an original's real size. BitmapCreateOptions.DelayCreation is
    /// NOT a guaranteed cheap header-only peek - Microsoft's own documentation describes it as deferred
    /// initialization, meaning real codec work still happens here. Every caller in this file runs this
    /// off the UI thread accordingly.
    ///
    /// Only the five extensions ArtworkAssetStore actually accepts are recognized - anything else (an
    /// exotic WIC codec that happens to be registered on this machine, say) is treated as unsupported
    /// rather than staged under a made-up extension.</summary>
    private static (int Width, int Height, string Extension)? TryReadOriginalImageInfo(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            var extension = decoder switch
            {
                PngBitmapDecoder => "png",
                JpegBitmapDecoder => "jpg",
                BmpBitmapDecoder => "bmp",
                GifBitmapDecoder => "gif",
                TiffBitmapDecoder => "tiff",
                _ => null,
            };
            if (extension is null)
                return null;

            var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            return frame is null ? null : (frame.PixelWidth, frame.PixelHeight, extension);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                        or OverflowException or COMException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            // Enforced against bytes actually read, not just the file's reported Length - a file that
            // changes size mid-read, or a misreported length, can't bypass this the way an upfront-only
            // FileInfo.Length check could.
            if (buffer.Length + read > maxBytes)
                throw new IOException($"File exceeds the {maxBytes}-byte limit while reading.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
