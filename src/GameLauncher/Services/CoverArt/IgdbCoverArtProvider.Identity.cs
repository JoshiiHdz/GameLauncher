using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.Identity;

namespace GameLauncher.Services.CoverArt;

/// <summary>The identity-facing half of the IGDB provider: title search and external-id mapping (identity), and
/// id-keyed cover fetching (artwork). Everything here is a provider BOUNDARY - a failure is a status, never an
/// exception, except the caller's own cancellation, which always propagates.</summary>
public sealed partial class IgdbCoverArtProvider
{
    /// <summary>The id-keyed cache version. Bump when what makes a cached image trustworthy for its id changes.</summary>
    internal const int IdCacheVersion = 2;

    private static readonly string IdCacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache", "Igdb");

    /// <summary>The IGDB external-game SOURCE (by its documented name) for a launcher whose store ids we can map. Steam only: GOG's
    /// `uid` format and Epic's catalog ids are UNVERIFIED (design U1), so they are deliberately not mapped. The source is looked
    /// up BY NAME through IGDB's `external_game_sources` data - no numeric source id is assumed - and the deprecated `category`
    /// field is neither queried nor trusted.</summary>
    internal static string? ExternalSourceNameFor(IdentifierNamespace launcherNamespace) =>
        launcherNamespace == IdentifierNamespace.SteamApp ? "Steam" : null;

    private readonly object _sourceGate = new();
    private readonly Dictionary<string, int> _externalSourceIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>IGDB's own id for the named external-game source, or null when IGDB lists no such source. Throws on an unreadable
    /// or unavailable response (the caller's boundary turns that into Unavailable). Cached per provider instance.</summary>
    private int? ResolveExternalSourceId(string sourceName, CancellationToken ct)
    {
        lock (_sourceGate)
        {
            if (_externalSourceIds.TryGetValue(sourceName, out var known))
                return known;
        }

        var body = $"fields name; where name = \"{EscapeApicalypseString(sourceName)}\"; limit 10;";
        var id = ParseExternalSourceId(SendApicalypseQuery("external_game_sources", body, GetAccessToken(ct), ct), sourceName);
        if (id is { } value)
        {
            lock (_sourceGate)
                _externalSourceIds[sourceName] = value;
        }

        return id;
    }

    /// <summary>The id of the one external-game source called `sourceName` (case-insensitive), null if none is listed. An
    /// unreadable entry throws (fail closed); two different ids for one name throw - a mapping cannot be trusted through it.</summary>
    internal static int? ParseExternalSourceId(string responseJson, string sourceName)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB external_game_sources response is not a JSON array.");

        var ids = new List<int>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt32(out var id) || id <= 0
                || !entry.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("IGDB external_game_sources entry has no readable id/name.");
            }

            if (string.Equals(nameProp.GetString(), sourceName, StringComparison.OrdinalIgnoreCase) && !ids.Contains(id))
                ids.Add(id);
        }

        return ids.Count switch
        {
            0 => null,
            1 => ids[0],
            _ => throw new InvalidDataException($"IGDB lists more than one external-game source named '{sourceName}'."),
        };
    }

    /// <summary>Title-path identity: exact collapsed title, unique among exact matches. Ambiguous for a known hub name.</summary>
    internal CatalogSearchResult SearchByTitleForIdentity(string title, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(title))
                return CatalogSearchResult.Ambiguous("known multi-title umbrella product name");

            var selection = SearchGameId(title, ct);
            if (selection.Ambiguous)
                return CatalogSearchResult.Ambiguous("more than one equally-confident exact title");

            return selection.Matched is { } matched
                ? CatalogSearchResult.Found(matched.Id.ToString(), matched.Title)
                : CatalogSearchResult.NoMatch();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IGDB: identity lookup for '{title}' failed - unavailable, not a non-match.", ex);
            return CatalogSearchResult.Unavailable(ex.Message);
        }
    }

    /// <summary>ID-path identity (design 4.2): does IGDB map this store id to one of its games? The response is trusted
    /// only as far as it can be VERIFIED against what we asked (uid and external_game_source must echo the request); anything
    /// unreadable is Unavailable, so this path can only ever add a mapping, never take away the title path.</summary>
    internal CatalogSearchResult MapExternalId(string sourceName, string uid, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (ResolveExternalSourceId(sourceName, ct) is not { } sourceId)
                return CatalogSearchResult.NoMatch($"IGDB lists no external source named '{sourceName}'");

            var body = $"fields game,uid,external_game_source; where external_game_source = {sourceId} & uid = \"{EscapeApicalypseString(uid)}\"; limit 10;";
            var gameIds = ParseExternalGames(SendApicalypseQuery("external_games", body, GetAccessToken(ct), ct), sourceId, uid);
            if (gameIds.Count == 0)
                return CatalogSearchResult.NoMatch();
            if (gameIds.Count > 1)
                return CatalogSearchResult.Ambiguous("the store id maps to more than one IGDB game");

            var title = ParseGameName(SendApicalypseQuery("games", $"fields name; where id = {gameIds[0]};", GetAccessToken(ct), ct), gameIds[0]);
            return CatalogSearchResult.Found(gameIds[0].ToString(), title);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IGDB: external-id lookup for {sourceName}:{uid} failed - falling back to the title path.", ex);
            return CatalogSearchResult.Unavailable(ex.Message);
        }
    }

    /// <summary>The distinct IGDB game ids whose external entry echoes exactly the (source, uid) we asked for. An entry
    /// that does not echo it is provably a different mapping and is ignored; one that does but has no usable game id, and
    /// any entry we cannot read at all, throws - it could be the mapping (fail closed, as for search candidates).</summary>
    internal static List<int> ParseExternalGames(string responseJson, int sourceId, string uid)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB external_games response is not a JSON array.");

        var games = new List<int>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("uid", out var uidProp) || uidProp.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("external_game_source", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.Number
                || !sourceProp.TryGetInt32(out var entrySource))
            {
                throw new InvalidDataException("IGDB external_games entry has no readable uid/external_game_source.");
            }

            if (entrySource != sourceId || !string.Equals(uidProp.GetString(), uid, StringComparison.Ordinal))
                continue;

            if (!entry.TryGetProperty("game", out var gameProp) || gameProp.ValueKind != JsonValueKind.Number
                || !gameProp.TryGetInt32(out var gameId) || gameId <= 0)
            {
                throw new InvalidDataException("IGDB external_games entry matches the store id but has no usable game id.");
            }

            if (!games.Contains(gameId))
                games.Add(gameId);
        }

        return games;
    }

    private static string ParseGameName(string responseJson, int expectedId)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var idValue) && idValue == expectedId
                    && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                {
                    return name.GetString()!;
                }
            }
        }

        throw new InvalidDataException($"IGDB game {expectedId} has no readable name.");
    }

    /// <summary>Does IGDB game `gameId`'s own external id for this store agree with the launcher's? Contradicted only
    /// when IGDB lists that store's id for the game and it is DIFFERENT; no listing, or any failure, is Unknown.</summary>
    internal LauncherConsistency CheckExternalConsistency(string gameId, string sourceName, string uid, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!int.TryParse(gameId, out var id) || id <= 0)
                return LauncherConsistency.Unknown;
            if (ResolveExternalSourceId(sourceName, ct) is not { } sourceId)
                return LauncherConsistency.Unknown;

            var body = $"fields uid,external_game_source,game; where game = {id} & external_game_source = {sourceId}; limit 10;";
            using var doc = JsonDocument.Parse(SendApicalypseQuery("external_games", body, GetAccessToken(ct), ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return LauncherConsistency.Unknown;

            var uids = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("uid", out var uidProp) || uidProp.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("external_game_source", out var sourceProp) || !sourceProp.TryGetInt32(out var entrySource)
                    || entrySource != sourceId)
                {
                    return LauncherConsistency.Unknown; // anything we cannot read cannot contradict
                }

                uids.Add(uidProp.GetString() ?? "");
            }

            if (uids.Count == 0)
                return LauncherConsistency.Unknown;

            return uids.Contains(uid, StringComparer.Ordinal) ? LauncherConsistency.Consistent : LauncherConsistency.Contradicted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IGDB: consistency check for game {gameId} failed - treating as unknown.", ex);
            return LauncherConsistency.Unknown;
        }
    }

    /// <summary>Rows requested from IGDB's alternative-name data. A response this large cannot prove uniqueness.</summary>
    internal const int AlternativeNameLimit = 50;

    /// <summary>Verified alternative-title identity (design 4.5): an EXHAUSTIVE exact query on `alternative_names`. Exactly one
    /// distinct game -> Found; none -> NoMatch; several, or too many rows to prove it -> Ambiguous. Nothing here scores or guesses.</summary>
    internal CatalogSearchResult SearchByAlternativeName(string title, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var body = $"fields game,name; where name ~ \"{EscapeApicalypseString(title)}\"; limit {AlternativeNameLimit};";
            var games = ParseAlternativeNameGames(SendApicalypseQuery("alternative_names", body, GetAccessToken(ct), ct), title, AlternativeNameLimit, out var truncated);
            if (truncated)
                return CatalogSearchResult.Ambiguous("too many alternative-name rows to prove the name belongs to one game");
            if (games.Count == 0)
                return CatalogSearchResult.NoMatch();
            if (games.Count > 1)
                return CatalogSearchResult.Ambiguous("more than one game lists this alternative name");

            var name = ParseGameName(SendApicalypseQuery("games", $"fields name; where id = {games[0]};", GetAccessToken(ct), ct), games[0]);
            return CatalogSearchResult.Found(games[0].ToString(), name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IGDB: alternative-name lookup for '{title}' failed - unavailable, not a non-match.", ex);
            return CatalogSearchResult.Unavailable(ex.Message);
        }
    }

    /// <summary>The distinct game ids whose alternative name is, by the shared collapsed-title rule, `title`. An entry whose name is
    /// readable and different is skipped; anything that could hide a second game (not an object, no readable name, or a matching
    /// name with no usable game id) throws - fail closed, as for search candidates. `truncated`: the response filled the limit.</summary>
    internal static List<int> ParseAlternativeNameGames(string responseJson, string title, int limit, out bool truncated)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB alternative_names response is not a JSON array.");

        truncated = doc.RootElement.GetArrayLength() >= limit;
        var games = new List<int>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameProp.GetString()))
            {
                throw new InvalidDataException("IGDB alternative_names entry has no readable name.");
            }

            if (!SteamGridDbCoverArtProvider.IsConfidentMatch(title, nameProp.GetString()!))
                continue;

            if (!entry.TryGetProperty("game", out var gameProp) || gameProp.ValueKind != JsonValueKind.Number
                || !gameProp.TryGetInt32(out var gameId) || gameId <= 0)
            {
                throw new InvalidDataException("IGDB alternative_names entry matches the title but has no usable game id.");
            }

            if (!games.Contains(gameId))
                games.Add(gameId);
        }

        return games;
    }

    /// <summary>The validated cached cover for `id`, or null: the cache read FetchCoverForId starts with, on its own (no network).</summary>
    internal BitmapImage? ReadCachedCoverForId(string id, string? cacheDirOverride)
    {
        if (!int.TryParse(id, out var gameId) || gameId <= 0)
            return null;

        return IdKeyedCoverCache.TryRead(IdKeyedCoverCache.PathFor(cacheDirOverride ?? IdCacheDir, id, IdCacheVersion), id, "IGDB");
    }

    /// <summary>The cover for `id`, by id, through the identity-bound cache: a hit needs no network and no title search.</summary>
    internal CatalogCoverResult FetchCoverForId(string id, string title, string? cacheDirOverride, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!int.TryParse(id, out var gameId) || gameId <= 0)
                return new CatalogCoverResult(CoverLookupStatus.IdentifiedWithoutUsableArt, null, false);

            var cacheDir = cacheDirOverride ?? IdCacheDir;
            var path = IdKeyedCoverCache.PathFor(cacheDir, id, IdCacheVersion);
            if (IdKeyedCoverCache.TryRead(path, id, "IGDB") is { } cached)
                return new CatalogCoverResult(CoverLookupStatus.Resolved, cached, true);

            var imageUrl = GetCoverImageUrl(gameId, ct);
            var bytes = imageUrl is null ? null : FetchBoundedImageBytes(imageUrl, ct);
            var decoded = bytes is null ? null : ValidateBytes(bytes, title);
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
            Logger.Warn($"IGDB: cover fetch for game {id} failed - unavailable.", ex);
            return new CatalogCoverResult(CoverLookupStatus.Unavailable, null, false);
        }
    }

    /// <summary>Free-text candidates for the picker. IGDB's own relevance ranking alone is not enough: for a franchise name it lists the OLD
    /// games first ("Call of Duty" put Black Ops 7 at position 47 of 97, so a 15-row list could never show it), and the game the user is
    /// looking at is usually one they installed recently. So a second, best-effort query adds the NEWEST main games whose name contains
    /// the text (no DLC, seasons or editions), and MergePickerCandidates orders the two. Not restricted to exact titles - the USER decides.
    /// Retries without the cover expansion if IGDB rejects it, so a picker search still works.</summary>
    internal IReadOnlyList<CatalogCandidate> SearchCandidatesForPicker(string text, CancellationToken ct)
    {
        var token = GetAccessToken(ct);
        string json;
        try
        {
            json = SendApicalypseQuery("games", $"search \"{EscapeApicalypseString(text)}\"; fields name,first_release_date,cover.image_id; limit 15;", token, ct);
        }
        catch (HttpRequestException)
        {
            json = SendApicalypseQuery("games", $"search \"{EscapeApicalypseString(text)}\"; fields name,first_release_date; limit 15;", GetAccessToken(ct), ct);
        }

        var byRelevance = ParseCandidates(json);
        return MergePickerCandidates(text, byRelevance, SearchNewestForPicker(text, ct));
    }

    /// <summary>The newest main games whose name contains `text`. Best effort: this only ADDS candidates, so any failure (other than the
    /// caller's cancellation) is logged and the relevance results still stand.</summary>
    private IReadOnlyList<CatalogCandidate> SearchNewestForPicker(string text, CancellationToken ct)
    {
        // `*` is IGDB's wildcard and would widen the match; quotes/backslashes are escaped like everywhere else. Too little text to be a
        // meaningful "contains" (or nothing letter-like at all) is simply skipped.
        var contains = text.Replace("*", " ").Trim();
        if (contains.Length < 3 || !contains.Any(char.IsLetterOrDigit))
            return Array.Empty<CatalogCandidate>();

        var filter = $"where name ~ *\"{EscapeApicalypseString(contains)}\"* & first_release_date != null & parent_game = null & version_parent = null; "
            + "sort first_release_date desc; limit 15;";
        try
        {
            try
            {
                return ParseCandidates(SendApicalypseQuery("games", "fields name,first_release_date,cover.image_id; " + filter, GetAccessToken(ct), ct));
            }
            catch (HttpRequestException)
            {
                return ParseCandidates(SendApicalypseQuery("games", "fields name,first_release_date; " + filter, GetAccessToken(ct), ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IGDB: the newest-matches picker query for '{contains}' failed - showing the relevance results only.", ex);
            return Array.Empty<CatalogCandidate>();
        }
    }

    /// <summary>Orders the picker's IGDB candidates: exact-title matches first (a franchise name IS a legitimate answer), then IGDB's own
    /// top three (this is what surfaces an abbreviation IGDB understands, like "BO7"), then the newest matches, then whatever else either
    /// list held. De-duplicated by id and capped, so the list stays short enough to scan.</summary>
    internal static IReadOnlyList<CatalogCandidate> MergePickerCandidates(string text, IReadOnlyList<CatalogCandidate> byRelevance,
        IReadOnlyList<CatalogCandidate> newest, int cap = 30)
    {
        var collapsed = SteamGridDbCoverArtProvider.CollapsedTitle(text);
        var ordered = new List<CatalogCandidate>();
        var seen = new HashSet<string>();

        void Add(IEnumerable<CatalogCandidate> items)
        {
            foreach (var candidate in items)
            {
                if (seen.Add(candidate.Id))
                    ordered.Add(candidate);
            }
        }

        Add(byRelevance.Concat(newest).Where(c => collapsed.Length > 0 && SteamGridDbCoverArtProvider.CollapsedTitle(c.Title) == collapsed));
        Add(byRelevance.Take(3));
        Add(newest.Take(12));
        Add(byRelevance);
        Add(newest);
        return ordered.Take(cap).ToList();
    }

    internal static IReadOnlyList<CatalogCandidate> ParseCandidates(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB search response is not a JSON array.");

        var list = new List<CatalogCandidate>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            // A picker shows what it can read: a malformed entry is simply not offered (the user is the arbiter, and
            // nothing here is used as identity evidence unless they choose it).
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt32(out var id) || id <= 0
                || !entry.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameProp.GetString()))
            {
                continue;
            }

            string? detail = null;
            if (entry.TryGetProperty("first_release_date", out var release) && release.ValueKind == JsonValueKind.Number && release.TryGetInt64(out var seconds)
                && seconds is > 0 and < 253_402_300_799)
            {
                detail = DateTimeOffset.FromUnixTimeSeconds(seconds).Year.ToString();
            }

            string? thumb = null;
            if (entry.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object
                && cover.TryGetProperty("image_id", out var imageId) && imageId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(imageId.GetString()))
            {
                thumb = $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId.GetString()}.jpg";
            }

            list.Add(new CatalogCandidate(IdentifierNamespace.IgdbGame, id.ToString(), nameProp.GetString()!, detail, thumb));
        }

        return list;
    }

    /// <summary>The covers IGDB lists for `id`, for Choose Cover.</summary>
    internal IReadOnlyList<CoverChoice> ListCoversForId(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var gameId) || gameId <= 0)
            return Array.Empty<CoverChoice>();

        var json = SendApicalypseQuery("covers", $"fields image_id; where game = {gameId}; limit 10;", GetAccessToken(ct), ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB cover response is not a JSON array.");

        var choices = new List<CoverChoice>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("image_id", out var imageId)
                && imageId.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(imageId.GetString()))
            {
                var url = $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId.GetString()}.jpg";
                choices.Add(new CoverChoice(imageId.GetString()!, url, url));
            }
        }

        return choices;
    }

    /// <summary>Bounded download of an image URL IGDB itself listed. Refuses any other host.</summary>
    internal byte[]? DownloadPickerImage(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "images.igdb.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return FetchBoundedImageBytes(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn("IGDB: image download failed.", ex);
            return null;
        }
    }
}
