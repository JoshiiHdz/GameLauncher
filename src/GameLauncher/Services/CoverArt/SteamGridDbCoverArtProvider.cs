using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services.CoverArt;

/// <summary>
/// Fetches box art from SteamGridDB (steamgriddb.com) for games that aren't on Steam (Epic/GOG/manual),
/// using their public REST API. Requires a free API key from steamgriddb.com/profile/preferences/api,
/// set via AppSettings.SteamGridDbApiKey. Matches by name search, so results depend on how closely the
/// detected game name matches SteamGridDB's catalog.
///
/// Bump CacheVersion when changing match/selection logic here, so games that were mis-cached under
/// the old logic get re-fetched automatically instead of keeping a wrong cover forever.
/// </summary>
public sealed class SteamGridDbCoverArtProvider : ICoverArtProvider
{
    // v3: Xbox games now search using the real Start Menu title instead of a possibly-generic
    // franchise folder name (e.g. "Call of Duty" holding this year's actual release), so a
    // previously-cached cover fetched under the wrong name needs to be re-fetched, not reused.
    private const int CacheVersion = 3;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache");

    private readonly string _apiKey;

    public SteamGridDbCoverArtProvider(string apiKey)
    {
        _apiKey = apiKey;
    }

    public BitmapImage? GetCoverArt(GameEntry game)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cachePath = Path.Combine(CacheDir, $"{game.Id}-v{CacheVersion}.png");
            if (File.Exists(cachePath))
                return LoadBitmap(File.ReadAllBytes(cachePath));

            var gameId = SearchGameId(game);
            if (gameId is null)
            {
                Logger.Warn($"SteamGridDB: no match for '{game.Name}'.");
                return null;
            }

            var imageUrl = GetGridImageUrl(gameId.Value);
            if (imageUrl is null)
            {
                Logger.Warn($"SteamGridDB: matched '{game.Name}' but it has no grid art available.");
                return null;
            }

            var bytes = Http.GetByteArrayAsync(imageUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(cachePath, bytes);
            return LoadBitmap(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                        or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"SteamGridDB: request failed for '{game.Name}'.", ex);
            return null;
        }
    }

    /// <summary>
    /// A bare name search ("Apex" for a folder called just "Apex") can match multiple unrelated
    /// games - live check found "Apex" (an obscure title) ranked above "Apex Legends" for that exact
    /// query. SteamGridDB tags each result with which storefronts carry it (steam/egs/origin/gog), so
    /// when we know the game's source, a result actually listed under that storefront is strong
    /// evidence it's the right one - "Apex Legends" is the only "Apex"-prefixed result tagged
    /// "origin", which is exactly our signal for an EA-sourced game. Falls back to the top result
    /// when nothing carries a matching tag, which is the previous (naive) behaviour.
    /// </summary>
    private int? SearchGameId(GameEntry game)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(game.Name)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = Http.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"SteamGridDB: search request returned {(int)response.StatusCode} "
                + $"{response.StatusCode}{(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? " - check the API key in Settings" : "")}.");
            return null;
        }

        using var stream = response.Content.ReadAsStream();
        using var doc = JsonDocument.Parse(stream);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return null;

        var storefrontTag = StorefrontTagFor(game.Source);
        if (storefrontTag is not null)
        {
            foreach (var candidate in data.EnumerateArray())
            {
                if (!candidate.TryGetProperty("types", out var types))
                    continue;

                if (types.EnumerateArray().Any(t => string.Equals(t.GetString(), storefrontTag, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Info($"  SteamGridDB: '{game.Name}' matched '{candidate.GetProperty("name").GetString()}' "
                                + $"(tagged '{storefrontTag}').");
                    return candidate.GetProperty("id").GetInt32();
                }
            }
        }

        var first = data[0];
        Logger.Info($"  SteamGridDB: '{game.Name}' matched '{first.GetProperty("name").GetString()}' (top result, no storefront tag matched).");
        return first.GetProperty("id").GetInt32();
    }

    private static string? StorefrontTagFor(GameSource source) => source switch
    {
        GameSource.Steam => "steam",
        GameSource.Epic => "egs",
        GameSource.Gog => "gog",
        GameSource.Ea => "origin",
        GameSource.Ubisoft => "uplay",
        // Xbox/BattleNet/Rockstar/AmazonGames/Manual: no storefront tag to filter on (or unverified) -
        // use the top result.
        _ => null,
    };

    private string? GetGridImageUrl(int gameId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = Http.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"SteamGridDB: grid request returned {(int)response.StatusCode} {response.StatusCode}.");
            return null;
        }

        using var stream = response.Content.ReadAsStream();
        using var doc = JsonDocument.Parse(stream);
        var data = doc.RootElement.GetProperty("data");
        return data.GetArrayLength() == 0 ? null : data[0].GetProperty("url").GetString();
    }

    private static BitmapImage LoadBitmap(byte[] bytes) => CoverArtDecoder.Decode(bytes);
}
