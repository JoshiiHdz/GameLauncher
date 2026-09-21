using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>A [Fact] that runs ONLY on a machine that HAS a WebP decoder (Windows' "WebP Image Extensions"). Where it
/// does not, the test is reported as SKIPPED with this reason - never passed vacuously.</summary>
public sealed class FactRequiresWebpCodecAttribute : FactAttribute
{
    public FactRequiresWebpCodecAttribute()
    {
        if (!WebpCodec.IsDecoderInstalled)
            Skip = "This machine has no WebP decoder (Windows WebP Image Extensions): WebP support cannot be verified here. See the no-codec tests instead.";
    }
}

/// <summary>A [Fact] that runs ONLY on a machine WITHOUT a WebP decoder - the counterpart of
/// FactRequiresWebpCodecAttribute. On a machine that has the codec it is SKIPPED, because the unsupported-codec
/// behaviour it pins cannot occur there.</summary>
public sealed class FactRequiresNoWebpCodecAttribute : FactAttribute
{
    public FactRequiresNoWebpCodecAttribute()
    {
        if (WebpCodec.IsDecoderInstalled)
            Skip = "This machine HAS a WebP decoder, so the unsupported-codec behaviour cannot be exercised here. See the codec-present tests instead.";
    }
}

/// <summary>B2: WebP. SteamGridDB can serve WebP grids and WPF has no built-in WebP decoder - it works only through
/// Windows' WIC codec, which may or may not be installed. These tests use REAL WebP files (Fixtures\WebP, generated
/// by libwebp) and keep two things strictly apart:
///
///   WebP SUPPORT (where the codec exists): real WebP covers are decoded, validated against the same bounds as any
///   other provider image, cached and served - the tests marked [FactRequiresWebpCodec], SKIPPED where it is absent.
///
///   UNSUPPORTED CODEC (where it does not): a perfectly good WebP cannot be shown. That is "identified, no usable
///   art" (never a crash, never Unavailable, never cached) and is reported as an unsupported codec, not a damaged
///   file - the tests marked [FactRequiresNoWebpCodec], SKIPPED where the codec exists.
///
/// Everything whose OUTCOME is the same either way (damaged and oversized WebP are rejected, the Change Cover path
/// is unchanged) is asserted unconditionally.</summary>
public class ProviderWebpTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string NewCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Webp-" + Guid.NewGuid());
        _dirs.Add(dir);
        return dir;
    }

    private static GameEntry Game() => new()
    {
        Id = "manual-webp", Name = "Test Game", ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    // The cache file is always named .png by this provider - the extension is a label, not the format.
    private static string CachePath(string dir) =>
        Path.Combine(dir, $"{Game().Id}-v{SteamGridDbCoverArtProvider.CacheVersionForTest}.png");

    private sealed record Lookup(BitmapImage? Image, CoverLookupStatus Status, bool FromCache, int ImageRequests);

    /// <summary>One real SteamGridDB lookup whose "download" is `imageBytes` (search stubbed; validation, cache write
    /// and sidecar are real).</summary>
    private static Lookup Fetch(byte[] imageBytes, string dir)
    {
        var images = 0;
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = _ => """{"data": [{"id": 5, "name": "Test Game"}]}""",
            FetchGridImageBytesOverride = _ => { images++; return imageBytes; },
        };
        var bitmap = provider.GetCoverArt(Game(), out var fromCache, out _, out var status, dir);
        return new Lookup(bitmap, status, fromCache, images);
    }

    /// <summary>A second lookup of the same game that may neither search nor download - only the cache can answer.</summary>
    private static Lookup CacheOnly(string dir)
    {
        var provider = new SteamGridDbCoverArtProvider("key")
        {
            SearchRequestOverride = _ => throw new InvalidOperationException("must not search"),
            FetchGridImageBytesOverride = _ => throw new InvalidOperationException("must not download"),
        };
        var bitmap = provider.GetCoverArt(Game(), out var fromCache, out _, out var status, dir);
        return new Lookup(bitmap, status, fromCache, 0);
    }

    private static void WriteSidecar(string dir, byte[] imageBytes) =>
        File.WriteAllText(CachePath(dir) + ".meta.json", JsonSerializer.Serialize(new
        {
            Id = 5, Title = "Test Game", SearchedName = "Test Game", ImageSha256 = Convert.ToHexString(SHA256.HashData(imageBytes)),
        }));

    private static bool CanDecodeHeader(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ==== Unconditional: what does not depend on the codec ================================================================

    [Fact]
    public void TheFixtures_AreRealWebpContainers_AndOtherFormatsAreNot()
    {
        foreach (var name in new[] { WebpFixtures.Lossy, WebpFixtures.Lossless, WebpFixtures.Alpha, WebpFixtures.Tiny, WebpFixtures.Animated,
                                     WebpFixtures.TooWide, WebpFixtures.Truncated, WebpFixtures.HeaderOnly })
        {
            Assert.True(WebpCodec.IsWebpContainer(WebpFixtures.Load(name)), name);
        }

        Assert.False(WebpCodec.IsWebpContainer(TestImages.Png()));
        Assert.False(WebpCodec.IsWebpContainer(TestImages.Icon()));
        Assert.False(WebpCodec.IsWebpContainer(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' })); // a WAV
        Assert.False(WebpCodec.IsWebpContainer(new byte[11]));
        Assert.False(WebpCodec.IsWebpContainer(Array.Empty<byte>()));
    }

    [Fact]
    public void TheCodecProbe_AgreesWithActuallyDecodingARealWebp()
    {
        // The probe carries its own tiny WebP; if that constant were wrong, "no codec" would be reported on a
        // machine that has one. Decoding the independently generated 1x1 fixture is the cross-check.
        Assert.Equal(CanDecodeHeader(WebpFixtures.Load(WebpFixtures.Tiny)), WebpCodec.IsDecoderInstalled);
    }

    [Fact]
    public void ADamagedWebp_IsRejected_WhateverTheCodec()
    {
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.Truncated), "truncated"));
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.HeaderOnly), "header-only"));
    }

    [Theory]
    [InlineData(WebpFixtures.TooWide)]
    [InlineData(WebpFixtures.TooTall)]
    public void AWebpOverTheDimensionBound_IsRejected_WhateverTheCodec(string fixture)
    {
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(fixture), fixture));
    }

    [Theory]
    [InlineData(WebpFixtures.Lossy)]
    [InlineData(WebpFixtures.Lossless)]
    public void TheChangeCoverPath_StillRejectsWebp_ItsFormatAllowlistIsUnchanged(string fixture)
    {
        // Provider images relax only the container allowlist (ValidateProviderBytes). A user-picked file goes
        // through ValidateBytes' default, which stages into ArtworkAssetStore under one of five known extensions -
        // WebP is not among them, with or without a codec.
        Assert.Null(ArtworkImageValidator.ValidateBytes(WebpFixtures.Load(fixture), fixture));
    }

    [Theory]
    [InlineData(WebpFixtures.Truncated)]
    [InlineData(WebpFixtures.HeaderOnly)]
    [InlineData(WebpFixtures.TooWide)]
    public void ADamagedOrOversizedWebpDownload_IsIdentifiedWithoutUsableArt_AndNeverCached(string fixture)
    {
        var dir = NewCacheDir();

        var result = Fetch(WebpFixtures.Load(fixture), dir);

        Assert.Null(result.Image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, result.Status);
        Assert.False(File.Exists(CachePath(dir)));
    }

    [Fact]
    public void ADamagedWebpInTheCache_EvenWithAValidSidecar_IsNotServed_AndIsRefetched()
    {
        var dir = NewCacheDir();
        Directory.CreateDirectory(dir);
        var damaged = WebpFixtures.Load(WebpFixtures.Truncated);
        File.WriteAllBytes(CachePath(dir), damaged);
        WriteSidecar(dir, damaged); // the sidecar honestly describes THESE bytes - only image validation can reject them
        var replacement = TestImages.Png();

        var result = Fetch(replacement, dir);

        Assert.NotNull(result.Image);
        Assert.False(result.FromCache);
        Assert.Equal(1, result.ImageRequests);
        Assert.Equal(replacement, File.ReadAllBytes(CachePath(dir)));
    }

    // ---- The faithful-decode check, pinned as a pure function ---------------------------------------------------------

    [Theory]
    [InlineData(320, 480, 600, 900, true)]    // 600x900 scaled to the 320px decode width
    [InlineData(320, 320, 1, 1, true)]        // a 1x1 source is scaled UP to the decode width
    [InlineData(320, 2, 8000, 50, true)]      // an extreme aspect ratio
    [InlineData(320, 481, 600, 900, true)]    // within the 2px rounding tolerance
    [InlineData(320, 478, 600, 900, true)]
    [InlineData(320, 477, 600, 900, false)]   // just outside it
    [InlineData(1, 1, 600, 900, false)]       // THE finding: a truncated WebP "decoded" to 1x1
    [InlineData(100, 150, 600, 900, false)]   // right aspect, wrong size
    [InlineData(100, 480, 600, 900, false)]   // the expected HEIGHT for a 600x900 but the wrong width: only the width check rejects it
    [InlineData(320, 480, 0, 900, false)]     // an unusable declared size
    [InlineData(320, 480, 600, 0, false)]
    public void IsFaithfulDecode_AcceptsOnlyTheShapeTheHeaderPromised(int decodedW, int decodedH, int originalW, int originalH, bool expected)
    {
        var decoded = BitmapSource.Create(decodedW, decodedH, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, new byte[decodedW * decodedH], decodedW);

        Assert.Equal(expected, CoverArtDecoder.IsFaithfulDecode(decoded, originalW, originalH));
    }

    // ==== Codec PRESENT: WebP SUPPORT is verified ===========================================================================

    [FactRequiresWebpCodec]
    public void CodecPresent_EveryRealCoverFixture_IsDecoded_ToTheExpectedSize()
    {
        // Real lossy, lossless and alpha WebP covers: reported as SKIPPED - never silently passed - without a codec.
        foreach (var fixture in new[] { WebpFixtures.Lossy, WebpFixtures.Lossless, WebpFixtures.Alpha })
        {
            var image = ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(fixture), fixture);
            Assert.NotNull(image);
            Assert.Equal((320, 480), (image!.PixelWidth, image.PixelHeight));
        }
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_TheDimensionBoundIsWhatRejectsAnOversizedWebp_NotAnInabilityToDecodeIt()
    {
        // Same encoder, same codec: 8000x50 (exactly AT the limit) is accepted while 9000x100 is rejected - so it
        // is the bound, not a decode failure, that turns the oversized one away.
        Assert.NotNull(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.AtLimit), "at-limit"));
        Assert.True(CanDecodeHeader(WebpFixtures.Load(WebpFixtures.TooWide)), "the oversized fixture's header must be readable for this test to mean anything");
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.TooWide), "too-wide"));
        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.TooTall), "too-tall"));
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_ATinyWebp_IsAccepted()
    {
        var image = ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(WebpFixtures.Tiny), "tiny");

        Assert.NotNull(image);
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_AnAnimatedWebp_ShowsItsFirstFrame()
    {
        // Two frames; the decoder exposes both, the cover shows frame 0 (there is no animation on a card).
        var bytes = WebpFixtures.Load(WebpFixtures.Animated);
        using (var stream = new MemoryStream(bytes))
            Assert.Equal(2, BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Count);

        var image = ArtworkImageValidator.ValidateProviderBytes(bytes, "animated");

        Assert.NotNull(image);
        Assert.Equal((320, 480), (image!.PixelWidth, image.PixelHeight)); // 120x180 frame 0
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_ATruncatedWebp_ThatTheDecoderAcceptsAs1x1_IsRejectedByTheFaithfulDecodeCheck()
    {
        // The finding this batch's real fixtures exposed: the Windows decoder reads the FULL 600x900 header of a
        // truncated file, then "decodes" the damaged data to a 1x1 image without throwing - which used to be
        // accepted, cached, and shown as a blank cover. (Without a codec the header is unreadable and the file is
        // rejected earlier, for a different reason - see ADamagedWebp_IsRejected_WhateverTheCodec.)
        var bytes = WebpFixtures.Load(WebpFixtures.Truncated);
        using (var stream = new MemoryStream(bytes))
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            Assert.Equal((600, 900), (frame.PixelWidth, frame.PixelHeight)); // header intact: it DOES reach the decode stage
        }
        Assert.NotNull(CoverArtDecoder.Decode(bytes)); // and the raw decode really does "succeed"

        Assert.Null(ArtworkImageValidator.ValidateProviderBytes(bytes, "truncated"));
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_ARealWebpCover_IsResolved_Cached_AndServedFromTheCache()
    {
        var dir = NewCacheDir();
        var webp = WebpFixtures.Load(WebpFixtures.Lossy);

        var first = Fetch(webp, dir);
        var second = CacheOnly(dir);

        Assert.Equal(CoverLookupStatus.Resolved, first.Status);
        Assert.NotNull(first.Image);
        Assert.False(first.FromCache);
        Assert.Equal(webp, File.ReadAllBytes(CachePath(dir))); // the WebP bytes themselves are what is cached
        Assert.Equal(CoverLookupStatus.Resolved, second.Status);
        Assert.True(second.FromCache); // and the cache-hit path decodes and validates WebP too
        Assert.Equal((320, 480), (second.Image!.PixelWidth, second.Image.PixelHeight));
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_ARealWebpThroughTheWholeTransport_IsResolved()
    {
        // The full path: search, grid listing (a .webp url), then a real HTTP download served as image/webp.
        var dir = NewCacheDir();
        var webp = WebpFixtures.Load(WebpFixtures.Lossless);
        var handler = new FuncHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/search/autocomplete/", StringComparison.Ordinal))
                return FakeIgdbHandler.Json("""{"data": [{"id": 5, "name": "Test Game"}]}""");
            if (path.Contains("/grids/game/", StringComparison.Ordinal))
                return FakeIgdbHandler.Json("""{"data": [{"url": "https://cdn2.steamgriddb.com/grid/cover.webp"}]}""");

            var content = new ByteArrayContent(webp);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var provider = new SteamGridDbCoverArtProvider("key") { HttpHandlerOverrideForTest = handler };

        var image = provider.GetCoverArt(Game(), out _, out var matched, out var status, dir);

        Assert.Equal(CoverLookupStatus.Resolved, status);
        Assert.NotNull(image);
        Assert.Equal(5, matched?.Id);
        Assert.EndsWith(".webp", handler.Urls[^1]);
    }

    [FactRequiresWebpCodec]
    public void CodecPresent_ATruncatedWebpInTheCache_IsRejectedByTheFaithfulDecodeCheck_NotJustByAMissingCodec()
    {
        // With the codec present the truncated file's header IS readable, so what rejects a cache hit is the
        // faithful-decode check on the cache path - and the entry is replaced by a fresh download.
        var dir = NewCacheDir();
        Directory.CreateDirectory(dir);
        var truncated = WebpFixtures.Load(WebpFixtures.Truncated);
        File.WriteAllBytes(CachePath(dir), truncated);
        WriteSidecar(dir, truncated);
        var good = WebpFixtures.Load(WebpFixtures.Lossy);

        var result = Fetch(good, dir);

        Assert.Equal(CoverLookupStatus.Resolved, result.Status);
        Assert.False(result.FromCache);
        Assert.Equal(good, File.ReadAllBytes(CachePath(dir)));
    }

    // ==== Codec ABSENT: UNSUPPORTED CODEC is verified (skipped on a machine that has the codec) ==========================

    [FactRequiresNoWebpCodec]
    public void CodecAbsent_AGoodWebpCover_IsIdentifiedWithoutUsableArt_NotACrash_NotUnavailable_NotCached()
    {
        var dir = NewCacheDir();

        var result = Fetch(WebpFixtures.Load(WebpFixtures.Lossy), dir);

        Assert.Null(result.Image);
        Assert.Equal(CoverLookupStatus.IdentifiedWithoutUsableArt, result.Status); // the cover's bytes are fine; this machine cannot show them
        Assert.False(File.Exists(CachePath(dir)));
    }

    [FactRequiresNoWebpCodec]
    public void CodecAbsent_EveryRealWebp_IsUnsupported_ThroughTheValidator()
    {
        foreach (var fixture in new[] { WebpFixtures.Lossy, WebpFixtures.Lossless, WebpFixtures.Alpha, WebpFixtures.Tiny, WebpFixtures.Animated })
            Assert.Null(ArtworkImageValidator.ValidateProviderBytes(WebpFixtures.Load(fixture), fixture));

        Assert.False(WebpCodec.IsDecoderInstalled);
    }
}
