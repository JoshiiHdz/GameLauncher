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
    // v5: StartAppsResolver now reads Get-StartApps output as UTF-8 instead of the system ANSI
    // codepage - real Start Menu titles with special characters (e.g. "Call of Duty®") were coming
    // back corrupted ("Call of Dutyr"), which broke cover-art matching downstream since the mangled
    // name never matches the real game on SteamGridDB. Covers cached under a corrupted name need to
    // re-fetch under the real one.
    // v6: StartAppsResolver's Base64 encoding fix means Xbox titles with special characters (Call of
    // Duty(R), etc.) resolve to their real, correctly-decoded name instead of a mangled one - old
    // cache entries keyed by the mangled name would otherwise never get re-fetched under the right one.
    // v7: SelectGameId now rejects a low-confidence match (see IsConfidentMatch) instead of blindly
    // trusting the top search result or any storefront-tagged one - two real, confirmed false positives
    // ("Minecraft for Windows" -> "ModFoundry", "EA SPORTS FC 27" -> "EA Sports FC 24") mean whatever
    // got cached under the old, unvalidated logic needs to be forgotten and re-evaluated, not kept
    // forever just because a download for it once succeeded.
    // v8: IsConfidentMatch's word-overlap rule alone did NOT actually catch the real logged
    // "Minecraft for Windows" -> "ModFoundry" false positive - the real candidate name is "ModFoundry -
    // Mod Maker for Minecraft", which shares the word "Minecraft" and passed v7's check. Now rejects a
    // candidate that marks itself as a mod/addon/DLC of something the query doesn't, and separately
    // recognizes "AWayOut" vs "A Way Out" as the same title via a collapsed (spacing/punctuation-
    // independent) comparison - see IsConfidentMatch's own remarks for both.
    private const int CacheVersion = 8;

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
            {
                var cached = LoadBitmap(File.ReadAllBytes(cachePath));
                if (cached is not null)
                    return cached;

                // Corrupt cache file (e.g. an interrupted write from a previous crash) - without
                // deleting it, this would fail identically on every future scan forever. Fall through
                // to re-search/re-download instead of returning null for good.
                Logger.Warn($"SteamGridDB: '{game.Name}' had a corrupt cached cover - deleting and re-fetching.");
                File.Delete(cachePath);
            }

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

            // Decode before caching: writing unvalidated bytes to disk first means a bad response
            // (truncated download, an HTML error page served with a 200) becomes a corrupt cache file
            // that fails identically - and gets deleted and re-fetched - on every future scan.
            var decoded = LoadBitmap(bytes);
            if (decoded is not null)
                File.WriteAllBytes(cachePath, bytes);
            return decoded;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                        or IOException or UnauthorizedAccessException or InvalidOperationException)
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

        using var reader = new StreamReader(response.Content.ReadAsStream());
        return SelectGameId(reader.ReadToEnd(), game.Name, StorefrontTagFor(game.Source));
    }

    /// <summary>Parses SteamGridDB's /search/autocomplete response body and selects a game id from it,
    /// preferring a result tagged with `storefrontTag` - pulled out of SearchGameId so malformed-
    /// response handling can be exercised directly in tests without a real HTTP round-trip. Every
    /// field is checked for its expected ValueKind before being read: the original version called
    /// GetProperty/GetString/GetInt32 straight off search results with no shape validation at all, so a
    /// response with a missing "data"/"id"/"name"/"types" field, or one of the right name but the wrong
    /// JSON type, would throw InvalidOperationException (GetProperty's own doc'd behavior for an absent
    /// property, or a present one of the wrong kind) - not a JsonException, and so not caught by
    /// GetCoverArt's old catch clause, which could crash the whole scan over a single unexpected API
    /// response. This returns null instead for any such shape it doesn't recognize - JsonDocument.Parse
    /// itself can still throw JsonException for genuinely invalid JSON, which GetCoverArt's own catch
    /// (now including InvalidOperationException too, as a backstop) still handles.</summary>
    internal static int? SelectGameId(string responseJson, string gameNameForLogging, string? storefrontTag)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            Logger.Warn($"SteamGridDB: search response for '{gameNameForLogging}' had an unexpected shape, skipping it.");
            return null;
        }

        if (data.GetArrayLength() == 0)
            return null;

        // Pass 1: a result tagged with the right storefront AND a confident name match - the strongest
        // signal available. Confidence is still checked even here: two real, different-year editions of
        // the same annual sports franchise can both plausibly carry the same storefront tag.
        if (storefrontTag is not null)
        {
            foreach (var candidate in data.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object
                    || !candidate.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
                    continue;

                var matchesStorefront = types.EnumerateArray().Any(t =>
                    t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), storefrontTag, StringComparison.OrdinalIgnoreCase));
                if (!matchesStorefront || !TryGetId(candidate, out var taggedId))
                    continue;

                var taggedName = GetNameOrUnknown(candidate);
                if (!IsConfidentMatch(gameNameForLogging, taggedName))
                {
                    Logger.Warn($"SteamGridDB: '{gameNameForLogging}' rejected low-confidence storefront-tagged "
                        + $"match '{taggedName}' (tagged '{storefrontTag}').");
                    continue;
                }

                Logger.Info($"  SteamGridDB: '{gameNameForLogging}' matched '{taggedName}' (tagged '{storefrontTag}').");
                return taggedId;
            }
        }

        // Pass 2: no confident storefront-tagged result - try the rest of the search results in rank
        // order for the first CONFIDENT match, rather than blindly trusting index 0 regardless of how
        // well it actually matches (the previous, naive behaviour). Two real, confirmed false positives
        // motivated this: "Minecraft for Windows" matched "ModFoundry" (no meaningful word overlap at
        // all), and "EA SPORTS FC 27" matched "EA Sports FC 24" (a different edition entirely).
        // Successfully downloading an image for a match like this proves nothing about whether the
        // match itself was right - this is the only place that's actually checked.
        foreach (var candidate in data.EnumerateArray())
        {
            if (!TryGetId(candidate, out var id))
                continue;

            var candidateName = GetNameOrUnknown(candidate);
            if (!IsConfidentMatch(gameNameForLogging, candidateName))
                continue;

            Logger.Info($"  SteamGridDB: '{gameNameForLogging}' matched '{candidateName}' (no storefront tag matched).");
            return id;
        }

        Logger.Warn($"SteamGridDB: no confident match for '{gameNameForLogging}' among "
            + $"{data.GetArrayLength()} search result(s).");
        return null;
    }

    /// <summary>Whether `candidateName` is a plausible enough match for `queryName` to trust -
    /// SteamGridDB's /search/autocomplete is a fuzzy text search, not an exact match. Checked against
    /// the exact strings from real logs, not paraphrased ones - a prior version's own test used a
    /// shortened "ModFoundry" instead of the actual logged "ModFoundry - Mod Maker for Minecraft", which
    /// shares the word "Minecraft" with "Minecraft for Windows" and would have PASSED a bare word-
    /// overlap check, silently failing to fix the real reported bug.
    ///
    /// Three rules, not a general similarity score - a stricter score-based threshold risks rejecting
    /// legitimate matches this hasn't been tested against (a subtitle, a different word order), trading
    /// "always shows SOME cover, occasionally wrong" for "sometimes falls back to the exe icon
    /// unnecessarily" isn't obviously better:
    ///  1. COLLAPSED-EXACT: stripped of all spacing/punctuation/case, the two names are identical - the
    ///     same title, just differently formatted ("AWayOut" vs "A Way Out"). Deliberately a precise
    ///     equality check, not a fuzzy camelCase-word-splitting heuristic that could misfire on other
    ///     titles - this only recognizes when they're the SAME text apart from formatting.
    ///  2. DERIVATIVE PRODUCT: the candidate contains a word that marks it as a MOD/ADDON/DLC/etc. of
    ///     something, and the query does not - "ModFoundry - Mod Maker for Minecraft" is a fan-made
    ///     modding tool for Minecraft, not Minecraft itself, even though it shares that one real word.
    ///     Checked BEFORE the general word-overlap rule below, since that rule alone would accept it.
    ///  3. NUMBER MISMATCH / NO WORD OVERLAP: both names contain a number and none of the query's appear
    ///     in the candidate's ("EA SPORTS FC 27" vs "EA Sports FC 24"), or the query has at least one
    ///     real (3+ letter, non-generic) word and none of them appear in the candidate at all.</summary>
    internal static bool IsConfidentMatch(string queryName, string candidateName)
    {
        if (string.Equals(CollapseForComparison(queryName), CollapseForComparison(candidateName), StringComparison.Ordinal))
            return true;

        var queryWords = SignificantWords(queryName);
        var candidateWords = SignificantWords(candidateName);

        if (candidateWords.Overlaps(DerivativeProductWords) && !queryWords.Overlaps(DerivativeProductWords))
            return false;

        var queryNumbers = ExtractNumbers(queryName);
        var candidateNumbers = ExtractNumbers(candidateName);
        if (queryNumbers.Count > 0 && candidateNumbers.Count > 0 && !queryNumbers.Overlaps(candidateNumbers))
            return false;

        if (queryWords.Count == 0)
            return true; // nothing meaningful left to compare (e.g. a name that's all short words/numbers)

        return queryWords.Overlaps(candidateWords);
    }

    // Real, confirmed case: "ModFoundry - Mod Maker for Minecraft" is a fan-made modding TOOL for
    // Minecraft, not the game - a candidate marking itself this way is a different product that merely
    // relates to the query, not the query itself, regardless of how many other words it shares.
    private static readonly HashSet<string> DerivativeProductWords = new(StringComparer.OrdinalIgnoreCase)
        { "mod", "mods", "modpack", "addon", "dlc", "wallpaper", "skin", "soundtrack", "plugin" };

    // Deliberately excludes generic words that would otherwise let two unrelated titles "match" on
    // nothing but a shared category word - "Minecraft for Windows" must not be allowed to match
    // anything else just because both happen to contain "Windows".
    private static readonly HashSet<string> GenericMatchWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "for", "of", "edition", "game", "windows", "pc" };

    private static HashSet<string> SignificantWords(string name) =>
        System.Text.RegularExpressions.Regex.Matches(name, "[A-Za-z]{3,}")
            .Select(m => m.Value)
            .Where(w => !GenericMatchWords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string CollapseForComparison(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static HashSet<int> ExtractNumbers(string name)
    {
        var numbers = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(name, @"\d+"))
        {
            if (int.TryParse(m.Value, out var n))
                numbers.Add(n);
        }
        return numbers;
    }

    private static bool TryGetId(JsonElement item, out int id)
    {
        id = 0;
        return item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.Number
            && idProp.TryGetInt32(out id);
    }

    private static string GetNameOrUnknown(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? "unknown"
            : "unknown";

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

        using var reader = new StreamReader(response.Content.ReadAsStream());
        return SelectGridImageUrl(reader.ReadToEnd());
    }

    /// <summary>Parses SteamGridDB's /grids/game/{id} response body and picks the first grid's url -
    /// same reasoning as SelectGameId: every field is checked for its expected ValueKind before being
    /// read, returning null for any unrecognized shape instead of throwing InvalidOperationException
    /// off a bare GetProperty/GetString chain.</summary>
    internal static string? SelectGridImageUrl(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        if (data.GetArrayLength() == 0)
            return null;

        var first = data[0];
        return first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
            ? urlProp.GetString()
            : null;
    }

    private static BitmapImage? LoadBitmap(byte[] bytes) => CoverArtDecoder.Decode(bytes);
}
