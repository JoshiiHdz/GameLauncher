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

    public BitmapImage? GetCoverArt(GameEntry game)
    {
        var appId = ExtractAppId(game);
        if (appId is null)
            return null;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, $"steam-{appId}.jpg");
            if (File.Exists(cachePath))
                return LoadBitmap(File.ReadAllBytes(cachePath));

            var bytes = Http.GetByteArrayAsync(
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg").GetAwaiter().GetResult();
            File.WriteAllBytes(cachePath, bytes);
            return LoadBitmap(bytes);
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

    private static BitmapImage LoadBitmap(byte[] bytes) => CoverArtDecoder.Decode(bytes);
}
