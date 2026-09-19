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
    // v9: two more real, confirmed false positives - "EA SPORTS FC 27" matched "EA Sports UFC" (shared
    // only the generic word "Sports"; the candidate had no edition number at all, so the old number
    // check - which only fired when BOTH sides had a number - never triggered), and a specific query
    // could match a bare franchise-umbrella candidate with no edition info of its own (e.g. "Call of
    // Duty: Black Ops 7" matching a plain "Call of Duty" result). IsConfidentMatch now requires every
    // significant word AND number the query names to actually appear on the candidate, requires the
    // reverse for numbers only (a candidate must not introduce an edition number the query never
    // mentioned), and no longer defaults to "confident" when there's nothing meaningful left to compare.
    // v10: v9's word/number CONTAINMENT still let a candidate with an unrelated extra subtitle pass as
    // "confident" whenever the query itself had no number to contradict it - "Call of Duty" matching
    // "Call of Duty: Modern Warfare" (neither has a number), and "Half-Life 2" matching "Half-Life 2:
    // Episode One" (a separately catalogued sequel, not a formatting variant - the previous version's
    // own test wrongly accepted this). Set-based number comparison also collapsed "Half-Life 2: Episode
    // 2" onto "Half-Life 2" by coincidence, since both contain a literal "2" for unrelated reasons.
    // IsConfidentMatch no longer tolerates ANY extra content on either side: only an exact match of the
    // normalized title (formatting differences and a short list of explicitly verified aliases - roman
    // numerals - collapsed away, nothing else) counts as confident. GetCoverArt also now refuses to even
    // search for a small set of known multi-title "umbrella" product names (see
    // AmbiguousUmbrellaProductNamesCollapsed) where no name-matching rule, however strict, could recover
    // which specific title is actually installed.
    // v11: the v10 umbrella-name check compared game.Name RAW against the set, while IsConfidentMatch
    // compares NORMALIZED text - "Call of Duty®" or "Call of Duty " (trailing whitespace) missed the raw
    // check entirely, then went on to exact-match the real "Call of Duty" catalog entry anyway, defeating
    // the guard for exactly the formatting variance it exists to survive. The umbrella check now
    // normalizes identically (IsAmbiguousUmbrellaProduct). Also: IsConfidentMatch previously accepted two
    // names that both collapse to an empty string (e.g. "!!!" vs "???") as "equal" - now rejected, since
    // an empty comparison is an absence of evidence, not a match.
    private const int CacheVersion = 11;

    /// <summary>Test-only: lets a test pre-populate a cache file under the exact real name GetCoverArt
    /// will look for, without hardcoding (and inevitably drifting from) the current CacheVersion.</summary>
    internal static int CacheVersionForTest => CacheVersion;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache");

    private readonly string _apiKey;

    public SteamGridDbCoverArtProvider(string apiKey)
    {
        _apiKey = apiKey;
    }

    // Some launchers register one multi-title "hub" product under a single generic display name
    // regardless of which specific game is actually installed. Activision's Call of Duty franchise is
    // the documented case: every modern yearly release (Black Ops, Modern Warfare, ...) shares one
    // Battle.net installer whose registry DisplayName is literally just "Call of Duty" (see
    // BattleNetScanner/PublisherUninstallScanner) - GameEntry.Name is that generic name verbatim,
    // regardless of the strictness of any string-matching rule against it. Real product-identity
    // resolution would need launcher-specific metadata this scanner doesn't currently read (Battle.net's
    // own install manifest is a private binary format - see BattleNetScanner's remarks); until that
    // exists, this skips automatic cover art entirely for a known ambiguous name rather than guessing,
    // exactly like SelectGameId returning no match - GetCoverArt's caller already falls back to the exe
    // icon.
    //
    // This is a deliberately blunt, temporary safety measure, NOT product-identity resolution - it also
    // suppresses SteamGridDB lookup for the ORIGINAL 2003 "Call of Duty", which genuinely is just "Call
    // of Duty" and would otherwise get a correct cover. That's an accepted tradeoff for now (no icon at
    // all is better than possibly the wrong specific yearly title's cover), not a claim that every entry
    // named this way is confirmed to be a modern hub install. Not verified against a live Battle.net Call
    // of Duty install; extend this set if another real launcher/hub entry is confirmed to have the same
    // problem.
    //
    // Held pre-collapsed (see CollapseForComparison/IsAmbiguousUmbrellaProduct) so a trademark symbol,
    // trailing whitespace, or repeated spacing in GameEntry.Name - "Call of Duty®", "Call of Duty " -
    // still matches this set exactly the same way IsConfidentMatch would go on to treat it as identical
    // to "Call of Duty" - a raw, unnormalized string comparison here missed those variants and let the
    // matcher accept the (wrong) exact match anyway.
    private static readonly HashSet<string> AmbiguousUmbrellaProductNamesCollapsed = new(StringComparer.Ordinal)
        { "callofduty" };

    /// <summary>Whether `gameName` is a known multi-title umbrella product name - normalized the exact
    /// same way IsConfidentMatch normalizes both sides of a match, so this can't be evaded (or wrongly
    /// triggered) by a formatting difference IsConfidentMatch itself wouldn't care about. A more specific
    /// name built on top of the umbrella name (e.g. "Call of Duty: Black Ops 7") collapses to different
    /// text entirely and is correctly NOT caught by this - only an exact, empty-of-other-content match
    /// against the bare umbrella name is.</summary>
    internal static bool IsAmbiguousUmbrellaProduct(string gameName) =>
        AmbiguousUmbrellaProductNamesCollapsed.Contains(CollapseForComparison(NormalizeRomanNumerals(gameName)));

    /// <summary>Test-only seam: lets a test observe whether GetCoverArt went on to search (and, in
    /// production, reach the network) instead of short-circuiting - e.g. for a known ambiguous umbrella
    /// product name - without needing a real HTTP round-trip either way. Defaults to the real
    /// SearchGameId; production code never sets this.</summary>
    internal Func<GameEntry, int?>? SearchGameIdOverride { get; set; }

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
        try
        {
            if (IsAmbiguousUmbrellaProduct(game.Name))
            {
                Logger.Warn($"SteamGridDB: '{game.Name}' is a known multi-title umbrella product name - "
                    + "skipping automatic cover art rather than guessing which specific title is installed.");
                return null;
            }

            var cacheDir = cacheDirOverride ?? CacheDir;
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{CacheVersion}.png");
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
                // to re-search/re-download instead of returning null for good.
                Logger.Warn($"SteamGridDB: '{game.Name}' had a corrupt cached cover - deleting and re-fetching.");
                File.Delete(cachePath);
            }

            var gameId = (SearchGameIdOverride ?? SearchGameId)(game);
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
        // order for the first CONFIDENT (exact-title, see IsConfidentMatch) match, rather than blindly
        // trusting index 0 regardless of how well it actually matches (the previous, naive behaviour).
        // Evaluating every candidate here, not just the first, is what lets an exact match further down
        // the list win even when a broader/unrelated candidate happens to rank first.
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
    /// SteamGridDB's /search/autocomplete is a fuzzy text search, not an exact match.
    ///
    /// A wrong cover is a real failure, not an acceptable trade for "always shows SOME cover" - when
    /// identity is uncertain, GetCoverArt's caller falls back to the exe icon instead, which is always
    /// the safer wrong answer. Earlier versions of this method tried to tolerate "close enough" matches
    /// via shared-word/shared-number heuristics (a candidate could carry extra words or an extra number
    /// the query never mentioned); every one of those heuristics turned out to accept a genuinely
    /// different, separately catalogued product at least once in practice - a shared generic word
    /// ("Sports"), a shared franchise prefix with no number to contradict it ("Call of Duty" matching
    /// "Call of Duty: Modern Warfare"), and a shared-but-coincidental number ("Half-Life 2" matching
    /// "Half-Life 2: Episode One" or "...: Episode 2", where the "2" means something different on each
    /// side). None of that is recoverable by adding another exception to the same kind of rule.
    ///
    /// So this only trusts an EXACT match: stripped of all spacing/punctuation/case, and with a short,
    /// explicitly verified list of roman-numeral sequel markers normalized to digits first (see
    /// NormalizeRomanNumerals), the two names must be identical - the same title, just differently
    /// formatted ("AWayOut" vs "A Way Out", "Overwatch 2" vs "Overwatch II"). Anything else - a subtitle,
    /// an edition suffix, a colon-separated episode name - is treated as a DIFFERENT product, not a
    /// looser version of the same one, until it's added to that explicit alias list. SelectGameId still
    /// evaluates every candidate in rank order, so an exact match further down the list is found even
    /// when a broader, non-matching candidate happens to rank first.
    ///
    /// A name that collapses to NOTHING (no letters or digits at all - e.g. "!!!" or "???") is rejected
    /// even against another equally-empty name: two empty strings being "equal" is not identity evidence,
    /// it's the absence of any evidence at all.</summary>
    internal static bool IsConfidentMatch(string queryName, string candidateName)
    {
        var collapsedQuery = CollapseForComparison(NormalizeRomanNumerals(queryName));
        var collapsedCandidate = CollapseForComparison(NormalizeRomanNumerals(candidateName));

        if (collapsedQuery.Length == 0 || collapsedCandidate.Length == 0)
            return false;

        return string.Equals(collapsedQuery, collapsedCandidate, StringComparison.Ordinal);
    }

    // Common roman-numeral sequel markers, normalized to their digit form before comparison so
    // "Overwatch 2" and "Overwatch II" are recognized as the same title instead of failing an exact-text
    // comparison purely over notation. Deliberately excludes solo "I"/"V"/"X": those collide with real
    // words and titles too easily ("V Rising", "X-Men") to safely rewrite unconditionally. This is
    // intentionally a short, explicit, hand-verified list - not a general fuzzy-matching mechanism.
    private static readonly Dictionary<string, string> RomanNumeralSequelNumbers = new(StringComparer.OrdinalIgnoreCase)
        { ["II"] = "2", ["III"] = "3", ["IV"] = "4", ["VI"] = "6", ["VII"] = "7", ["VIII"] = "8", ["IX"] = "9" };

    private static string NormalizeRomanNumerals(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\b(II|III|IV|VI|VII|VIII|IX)\b",
            m => RomanNumeralSequelNumbers[m.Value.ToUpperInvariant()],
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string CollapseForComparison(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

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
