using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.Identity;

namespace GameLauncher.Services.CoverArt;

/// <summary>The identity-facing half of the SteamGridDB provider - see IgdbCoverArtProvider.Identity.cs. SteamGridDB
/// is a FALLBACK catalog: it identifies by title only (exact and unique) and supplies art by its own game id.</summary>
public sealed partial class SteamGridDbCoverArtProvider
{
    /// <summary>The id-keyed cache version. Deliberately a fresh series in its own directory: the name-keyed
    /// `{gameId}-v13.png` entries are the LEGACY format the migration reads (design 9.1), never mixed with these.</summary>
    internal const int IdCacheVersion = 1;

    private static readonly string IdCacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache", "SteamGridDb");

    internal CatalogSearchResult SearchByTitleForIdentity(string title, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsAmbiguousUmbrellaProduct(title))
                return CatalogSearchResult.Ambiguous("known multi-title umbrella product name");

            var found = SearchQuery(null, title, ct);
            // An ambiguous primary search is a settled fact about the identity, not a failure: never retried.
            if (found.Matched is null && !found.Ambiguous)
            {
                var expanded = SplitCompactedWords(title);
                if (!string.Equals(expanded, title, StringComparison.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    found = SearchQuery(null, expanded, ct);
                }
            }

            if (found.Ambiguous)
                return CatalogSearchResult.Ambiguous("more than one equally-confident exact title");

            return found.Matched is { } matched
                ? CatalogSearchResult.Found(matched.Id.ToString(), matched.Title)
                : CatalogSearchResult.NoMatch();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SteamGridDB: identity lookup for '{title}' failed - unavailable, not a non-match.", ex);
            return CatalogSearchResult.Unavailable(ex.Message);
        }
    }

    /// <summary>The validated cached cover for `id`, or null: the cache read FetchCoverForId starts with, on its own (no network).</summary>
    internal BitmapImage? ReadCachedCoverForId(string id, string? cacheDirOverride)
    {
        if (!int.TryParse(id, out var gameId) || gameId <= 0)
            return null;

        return IdKeyedCoverCache.TryRead(IdKeyedCoverCache.PathFor(cacheDirOverride ?? IdCacheDir, id, IdCacheVersion), id, "SteamGridDB");
    }

    internal CatalogCoverResult FetchCoverForId(string id, string title, string? cacheDirOverride, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!int.TryParse(id, out var gameId) || gameId <= 0)
                return new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);

            var cacheDir = cacheDirOverride ?? IdCacheDir;
            var path = IdKeyedCoverCache.PathFor(cacheDir, id, IdCacheVersion);
            if (IdKeyedCoverCache.TryRead(path, id, "SteamGridDB") is { } cached)
                return new CatalogCoverResult(CoverLookupStatus.Resolved, cached, true);

            var imageUrl = GetGridImageUrl(gameId, ct);
            var bytes = imageUrl is null
                ? null
                : ProviderImageIo.FetchBoundedImageBytes(Http, imageUrl, "SteamGridDB",
                    ImageDownloadTimeoutOverrideForTest ?? ProviderImageIo.DefaultDownloadTimeout, ct);
            var decoded = bytes is null ? null : LoadBitmap(bytes, title);
            if (bytes is null || decoded is null)
                return new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);

            ct.ThrowIfCancellationRequested(); // a cancelled lookup caches nothing
            IdKeyedCoverCache.Write(path, id, title, bytes);
            return new CatalogCoverResult(CoverLookupStatus.Resolved, decoded, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SteamGridDB: cover fetch for game {id} failed - unavailable.", ex);
            return new CatalogCoverResult(CoverLookupStatus.Unavailable, null, false);
        }
    }

    private string GetJson(string url, string label, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return ProviderHttp.Send(Http, request, label, RequestTimeoutOverrideForTest ?? ProviderHttp.DefaultRequestTimeout, ct,
            (response, token) =>
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"{label} returned {(int)response.StatusCode} {response.StatusCode}.");

                return ProviderHttp.ReadBoundedText(response, label, ProviderHttp.MaxJsonResponseBytes, token);
            });
    }

    /// <summary>Free-text candidates for the picker (SteamGridDB's own autocomplete). The user decides; nothing here is
    /// identity evidence unless they choose it.</summary>
    internal IReadOnlyList<CatalogCandidate> SearchCandidatesForPicker(string text, CancellationToken ct)
    {
        var json = GetJson($"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(text)}", "SteamGridDB search", ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SteamGridDB search response is not {\"data\": [...]}.");

        var list = new List<CatalogCandidate>();
        foreach (var entry in data.EnumerateArray())
        {
            if (!CatalogResponseReader.TryReadId(entry, out var id) || !CatalogResponseReader.TryReadName(entry, out var name))
                continue;

            var tags = entry.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array
                ? string.Join(", ", types.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()))
                : null;
            list.Add(new CatalogCandidate(IdentifierNamespace.SteamGridDbGame, id.ToString(), name, string.IsNullOrWhiteSpace(tags) ? null : tags, null));
            if (list.Count >= 12)
                break;
        }

        return list;
    }

    /// <summary>The 600x900 grids SteamGridDB lists for `id`, for Choose Cover.</summary>
    internal IReadOnlyList<CoverChoice> ListCoversForId(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var gameId) || gameId <= 0)
            return Array.Empty<CoverChoice>();

        var json = GetJson($"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900", "SteamGridDB grid listing", ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SteamGridDB grid response is not {\"data\": [...]}.");

        var choices = new List<CoverChoice>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !IsSteamGridDbUrl(StringProperty(entry, "url"), out var url))
                continue;

            var thumb = IsSteamGridDbUrl(StringProperty(entry, "thumb"), out var thumbUrl) ? thumbUrl : url;
            var reference = entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetRawText() : url;
            choices.Add(new CoverChoice(reference, url, thumb));
            if (choices.Count >= 12)
                break;
        }

        return choices;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static bool IsSteamGridDbUrl(string? text, out string url)
    {
        url = "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("steamgriddb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steamgriddb.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        url = uri.ToString();
        return true;
    }

    /// <summary>Bounded download of an image URL SteamGridDB itself listed. Refuses any other host.</summary>
    internal byte[]? DownloadPickerImage(string url, CancellationToken ct)
    {
        if (!IsSteamGridDbUrl(url, out var checkedUrl))
            return null;

        try
        {
            return ProviderImageIo.FetchBoundedImageBytes(Http, checkedUrl, "SteamGridDB",
                ImageDownloadTimeoutOverrideForTest ?? ProviderImageIo.DefaultDownloadTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn("SteamGridDB: image download failed.", ex);
            return null;
        }
    }
}
