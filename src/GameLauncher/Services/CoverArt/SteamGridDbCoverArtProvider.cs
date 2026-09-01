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
/// </summary>
public sealed class SteamGridDbCoverArtProvider : ICoverArtProvider
{
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
            var cachePath = Path.Combine(CacheDir, $"{game.Id}.png");
            if (File.Exists(cachePath))
                return LoadBitmap(File.ReadAllBytes(cachePath));

            var gameId = SearchGameId(game.Name);
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

    private int? SearchGameId(string gameName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(gameName)}");
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
        return data.GetArrayLength() == 0 ? null : data[0].GetProperty("id").GetInt32();
    }

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

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
