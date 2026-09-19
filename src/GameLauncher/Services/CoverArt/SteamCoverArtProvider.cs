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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache");

    public BitmapImage? GetCoverArt(GameEntry game) => GetCoverArt(game, out _);

    /// <summary>Same lookup, but also reports whether the result came from this provider's own on-disk
    /// cache rather than a fresh network fetch just now - CoverArtService.Apply needs this to record
    /// accurate retrieval evidence (ArtworkRetrievalMethod) instead of manufacturing "NetworkDownload"
    /// for what was actually a cache hit. `cacheDirOverride` is test-only (production never passes it,
    /// always resolving under AppPaths.DataDir) - same per-call-override pattern as
    /// ArtworkAssetStore.TryResolvePath, for the same reason (xUnit's default parallel test execution).</summary>
    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, string? cacheDirOverride = null)
    {
        servedFromCache = false;

        var appId = ExtractAppId(game);
        if (appId is null)
            return null;

        var cacheDir = cacheDirOverride ?? CacheDir;
        try
        {
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"steam-{appId}.jpg");
            if (File.Exists(cachePath))
            {
                var cached = LoadBitmap(File.ReadAllBytes(cachePath));
                if (cached is not null)
                {
                    servedFromCache = true;
                    return cached;
                }

                // Corrupt cache file (e.g. an interrupted write from a previous crash) - without
                // deleting it, this would fail identically on every future scan forever. Fall through
                // to re-download instead of returning null for good.
                Logger.Warn($"  art: '{game.Name}' had a corrupt cached Steam cover - deleting and re-fetching.");
                File.Delete(cachePath);
            }

            var bytes = Http.GetByteArrayAsync(
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg").GetAwaiter().GetResult();

            // Decode before caching: writing unvalidated bytes to disk first means a bad response
            // (truncated download, an HTML error page served with a 200) becomes a corrupt cache file
            // that fails identically - and gets deleted and re-fetched - on every future scan.
            var decoded = LoadBitmap(bytes);
            if (decoded is not null)
                File.WriteAllBytes(cachePath, bytes);
            return decoded;
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

    private static BitmapImage? LoadBitmap(byte[] bytes) => CoverArtDecoder.Decode(bytes);
}
