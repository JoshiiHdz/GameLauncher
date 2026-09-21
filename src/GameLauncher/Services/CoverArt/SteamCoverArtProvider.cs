using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services.CoverArt;

/// <summary>
/// Fetches real Steam box art from Steam's public CDN, keyed by app ID - no API key needed, and
/// guaranteed to match exactly (unlike a name-based search) since Steam entries carry their own appid.
/// </summary>
public sealed class SteamCoverArtProvider : ICoverArtProvider
{
    private static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Test-only, per-INSTANCE transport seam (see SteamGridDbCoverArtProvider.
    /// HttpHandlerOverrideForTest). Production never sets this.</summary>
    internal HttpMessageHandler? HttpHandlerOverrideForTest { get; init; }

    /// <summary>Test-only: shortens the download's own time budget. Production never sets this.</summary>
    internal TimeSpan? DownloadTimeoutOverrideForTest { get; init; }

    private HttpClient? _testHttp;

    private HttpClient Http => HttpHandlerOverrideForTest is null
        ? RealHttp
        : _testHttp ??= new HttpClient(HttpHandlerOverrideForTest, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache");

    /// <summary>Test-only, per-INSTANCE seam (deliberately not static - see IgdbCoverArtProvider.
    /// HttpHandlerOverrideForTest for why): given the Steam app id, returns the raw image bytes the CDN would
    /// have, or null for "no image". Replaces only the network read; the shared validation, the cache write
    /// and everything after it still run for real. Production never sets this.</summary>
    internal Func<string, byte[]?>? FetchImageBytesOverride { get; init; }

    public BitmapImage? GetCoverArt(GameEntry game) => GetCoverArt(game, out _);

    /// <summary>Same lookup, but also reports whether the result came from this provider's own on-disk
    /// cache rather than a fresh network fetch just now - CoverArtService.Apply needs this to record
    /// accurate retrieval evidence (ArtworkRetrievalMethod) instead of manufacturing "NetworkDownload"
    /// for what was actually a cache hit. `cacheDirOverride` is test-only (production never passes it,
    /// always resolving under AppPaths.DataDir) - same per-call-override pattern as
    /// ArtworkAssetStore.TryResolvePath, for the same reason (xUnit's default parallel test execution).
    ///
    /// `ct` is the CALLER's cancellation (a superseded scan): it bounds the download (see ProviderHttp) and is
    /// checked before anything is written. Unlike every other failure here - which this swallows into a null,
    /// "no Steam cover" - the caller's cancellation PROPAGATES as OperationCanceledException: converting it
    /// into "no cover" would let a cancelled scan carry on down the fallback chain and record a result the
    /// scan no longer wants.</summary>
    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, string? cacheDirOverride = null,
        CancellationToken ct = default)
    {
        servedFromCache = false;

        var appId = ExtractAppId(game);
        if (appId is null)
            return null;

        var cacheDir = cacheDirOverride ?? CacheDir;
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"steam-{appId}.jpg");
            if (File.Exists(cachePath))
            {
                // Bounded read + the shared ArtworkImageValidator (original size/dimension/decode bounds) -
                // not File.ReadAllBytes + CoverArtDecoder.Decode, which loads any size of file and only
                // ever decodes a downsampled copy.
                var cachedBytes = ProviderImageIo.ReadBoundedCacheFile(cachePath, "Steam CDN");
                var cached = cachedBytes is null ? null : LoadBitmap(cachedBytes, game.Name);
                if (cached is not null)
                {
                    servedFromCache = true;
                    return cached;
                }

                // Corrupt or out-of-bounds cache file (e.g. an interrupted write from a previous crash) -
                // without deleting it, this would fail identically on every future scan forever. Fall
                // through to re-download instead of returning null for good.
                Logger.Warn($"  art: '{game.Name}' had a corrupt or invalid cached Steam cover - deleting and re-fetching.");
                File.Delete(cachePath);
            }

            // Bounded (hard byte cap + hard time budget + the caller's cancellation, shared with the other
            // providers) instead of HttpClient.GetByteArrayAsync, which reads an unbounded body into memory.
            // A 404 (no library art for this app) is a null, not an exception; any other HTTP failure throws
            // and is caught below, exactly as before.
            ct.ThrowIfCancellationRequested();
            var bytes = FetchImageBytesOverride is not null
                ? FetchImageBytesOverride(appId)
                : ProviderImageIo.FetchBoundedImageBytes(Http,
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                    "Steam CDN", DownloadTimeoutOverrideForTest ?? ProviderImageIo.DefaultDownloadTimeout, ct);
            if (bytes is null)
                return null;

            // Validate before caching: writing unvalidated bytes to disk first means a bad response
            // (truncated download, an HTML error page served with a 200, an oversized image) becomes a
            // cache file that fails identically - and gets deleted and re-fetched - on every future scan.
            var decoded = LoadBitmap(bytes, game.Name);
            if (decoded is not null)
            {
                ct.ThrowIfCancellationRequested(); // a cancelled lookup caches nothing
                File.WriteAllBytes(cachePath, bytes);
            }

            return decoded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                        or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ExtractAppId(GameEntry game)
    {
        // GameEntry.Id is always "steam-{appid}" for Steam-sourced entries (see SteamScanner).
        const string prefix = "steam-";
        return game.Source == GameSource.Steam && game.Id.StartsWith(prefix)
            ? game.Id[prefix.Length..]
            : null;
    }

    /// <summary>Shared validation for every image this provider displays or caches - see
    /// ArtworkImageValidator.ValidateProviderBytes.</summary>
    private static BitmapImage? LoadBitmap(byte[] bytes, string sourceForLogging) =>
        ArtworkImageValidator.ValidateProviderBytes(bytes, sourceForLogging);
}
