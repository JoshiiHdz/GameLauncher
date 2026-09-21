using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services.CoverArt;

/// <summary>
/// Fetches identity/box-art from IGDB (igdb.com), the PRIMARY automatic provider - see
/// CoverArtService.Apply's own remarks for why IGDB is tried before SteamGridDB, and this class's own
/// investigation notes (real, verified proof-of-concept queries: EA Sports FC 27 including its Ultimate/
/// Ultimate Plus editions, Call of Duty: Black Ops 7 including its Season 1 DLC, Minecraft's Java/Bedrock
/// hierarchy, Apex Legends' seasonal updates, and A Way Out - whose correct result ranked EIGHTH among
/// unrelated "Way ..." titles, proving IGDB's own free-text search needs the identical strict discipline
/// SteamGridDbCoverArtProvider.IsConfidentMatch already applies, not looser trust because a different
/// provider said so).
///
/// Auth is Twitch's OAuth2 client_credentials flow (AppSettings.IgdbClientId + IgdbCredentialStore's
/// OS-protected secret), or - with none - the project's RELAY (see the internal Uri constructor): the relay holds
/// the shared credentials server-side, so no secret ships in the launcher at all.
/// No access at all simply means this provider is unavailable - resolution falls straight to SteamGridDB.
/// If the relay or its token is ever down or rate-limited, that is what happens: the request fails, which is
/// reported as Unavailable, never a crash and never a cleared cover (design 4.8).
///
/// This class is a PROVIDER BOUNDARY: any failure that is not the caller's own cancellation is converted
/// into CoverLookupStatus.Unavailable rather than escaping. An escaping exception would reach
/// GameScannerService.SafeApplyCoverArt's outer safety net, which falls straight to the exe icon and so
/// skips SteamGridDB and Steam CDN entirely - a real, confirmed gap for a malformed token response, whose
/// KeyNotFoundException/FormatException were outside the previous, enumerated catch filter.
/// </summary>
public sealed partial class IgdbCoverArtProvider : ICoverArtProvider
{
    // v1: first version. Bump when changing match/selection logic, exactly as SteamGridDbCoverArtProvider
    // documents on its own CacheVersion.
    private const int CacheVersion = 1;

    internal static int CacheVersionForTest => CacheVersion;

    private static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache", "Igdb");

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly RelayEndpoint? _relay;

    public IgdbCoverArtProvider(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    /// <summary>RELAY mode: the project's own small server (docs/deploy/igdb-relay) holds the IGDB credentials, caches and rate-limits, so
    /// this instance holds NO Client ID, secret or token and sends none - it only speaks the same /v4/{endpoint} apicalypse requests to the
    /// relay's base address. Everything above the transport (matching, identity, rejections, cover caching) is identical to direct mode.</summary>
    internal IgdbCoverArtProvider(RelayEndpoint relay)
    {
        _clientId = "";
        _clientSecret = "";
        _relay = relay;
    }

    /// <summary>Test-only seam: when set, every real HTTP call this INSTANCE makes (token, search, covers,
    /// image download) goes through a client wrapping THIS handler instead of the shared production client
    /// - a real HttpMessageHandler-level fake, so behavior only observable at the transport level (a
    /// stalled body read after headers, a 401 needing a token refresh, a 429 with Retry-After) is testable
    /// without an external server. Per-INSTANCE, deliberately not a static switch: a static one would be
    /// visible to every provider constructed anywhere while a test held it set, the same shape of hazard
    /// as IgdbCredentialStore's former static directory override. Production never sets this.</summary>
    internal HttpMessageHandler? HttpHandlerOverrideForTest { get; init; }

    private HttpClient? _testHttp;

    private HttpClient Http => HttpHandlerOverrideForTest is null
        ? RealHttp
        : _testHttp ??= new HttpClient(HttpHandlerOverrideForTest, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The client for the apicalypse queries. In relay mode that is the PINNED client (the relay's key is the only thing it trusts);
    /// otherwise, and for every image download, the ordinary one. A test handler replaces both.</summary>
    private HttpClient ApiHttp => _relay is null || HttpHandlerOverrideForTest is not null ? Http : RelayTransport.ClientFor(_relay.Pins);

    /// <summary>Identifies which IGDB game a search resolved to. Deliberately a SEPARATE type from
    /// SteamGridDbCoverArtProvider.MatchedGame, even though the shape is identical - an IGDB id and a
    /// SteamGridDB id are different identifier namespaces (see this project's own identity-evidence
    /// design notes), and keeping the CLR types distinct means passing one where the other is expected is
    /// a compile error, not a silent cross-provider id mix-up caught only at runtime, if ever.</summary>
    public readonly record struct MatchedGame(int Id, string Title);

    // ---- Rate limiting -------------------------------------------------------------------------------
    // IGDB documents a 4-requests-per-second limit per Client-ID (api-docs.igdb.com/#rate-limits). A
    // library scan calls SearchGameId then GetCoverImageUrl PER GAME, across potentially many games in
    // sequence with no other throttle anywhere in the call chain - this is the one place that can
    // enforce the limit locally, shared across every IgdbCoverArtProvider instance (constructed fresh
    // per game, same as SteamGridDbCoverArtProvider). It cannot eliminate server-side throttling (the
    // same credentials may be in use elsewhere), which is what the 429 handling in SendApicalypseQuery is
    // for. Only guards api.igdb.com calls - id.twitch.tv (token) and images.igdb.com (the CDN) are
    // separate services IGDB's own documented limit doesn't govern.
    private static readonly object RateLimitLock = new();
    private static DateTime _nextAllowedApiRequestUtc = DateTime.MinValue;
    private const int MinRequestIntervalMs = 260; // slightly above 1000/4=250ms for safety margin

    /// <summary>Test-only: resets the shared rate-limit clock so a test asserting on throttling timing
    /// isn't affected by whatever other tests already advanced it. Production never calls this.</summary>
    internal static void ResetRateLimitForTest()
    {
        lock (RateLimitLock)
        {
            _nextAllowedApiRequestUtc = DateTime.MinValue;
        }
    }

    /// <summary>Test-only: makes the NEXT api.igdb.com request wait `wait` for its local rate-limit slot, so
    /// a test can prove that wait is cancellable without depending on wall-clock timing luck. Production
    /// never calls this.</summary>
    internal static void PrimeRateLimitForTest(TimeSpan wait)
    {
        lock (RateLimitLock)
        {
            _nextAllowedApiRequestUtc = DateTime.UtcNow + wait;
        }
    }

    /// <summary>Waits out the local rate-limit slot - CANCELLABLE: a superseded scan must not sit through
    /// (or, worse, go on to send) a request it no longer wants. An earlier version used Thread.Sleep,
    /// which neither observed cancellation nor stopped the request that followed it.</summary>
    private static void ThrottleForRateLimit(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TimeSpan waitTime;
        lock (RateLimitLock)
        {
            var now = DateTime.UtcNow;
            waitTime = _nextAllowedApiRequestUtc > now ? _nextAllowedApiRequestUtc - now : TimeSpan.Zero;
            _nextAllowedApiRequestUtc = (waitTime > TimeSpan.Zero ? _nextAllowedApiRequestUtc : now) + TimeSpan.FromMilliseconds(MinRequestIntervalMs);
        }

        if (waitTime > TimeSpan.Zero && ct.WaitHandle.WaitOne(waitTime))
            ct.ThrowIfCancellationRequested();
    }

    // ---- 429 handling --------------------------------------------------------------------------------
    /// <summary>Retries after a 429 - a hard bound, never indefinite: 2 retries (3 attempts total).</summary>
    internal const int MaxRateLimitRetries = 2;

    /// <summary>Longest single wait honored - a scan must not stall for minutes on one game. If the server
    /// asks for MORE than this, the request is abandoned instead (retrying early would only earn another
    /// 429), and the game simply falls back like any other Unavailable outcome.</summary>
    internal static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before retry number `retryNumber` (0-based) after a 429: the server's own
    /// Retry-After (delta-seconds or HTTP-date) when supplied, otherwise 0.5s then 1s. Null means "the wait
    /// exceeds our budget - do not retry".</summary>
    internal static TimeSpan? ComputeRateLimitBackoff(int retryNumber, RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        TimeSpan delay;
        if (retryAfter?.Delta is { } delta)
            delay = delta;
        else if (retryAfter?.Date is { } date)
            delay = date - now;
        else
            delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, retryNumber));

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        return delay > MaxRateLimitBackoff ? null : delay;
    }

    /// <summary>Test-only: when set, a 429 backoff is handed to this instead of actually waiting, so
    /// recovery/exhaustion tests can assert on the delays requested without sleeping. Production never
    /// sets this.</summary>
    internal Action<TimeSpan>? BackoffDelayOverrideForTest { get; init; }

    private void WaitForBackoff(TimeSpan delay, CancellationToken ct)
    {
        if (BackoffDelayOverrideForTest is not null)
        {
            BackoffDelayOverrideForTest(delay);
            ct.ThrowIfCancellationRequested();
            return;
        }

        if (delay > TimeSpan.Zero && ct.WaitHandle.WaitOne(delay))
            ct.ThrowIfCancellationRequested();
    }

    // ---- Token acquisition/caching -----------------------------------------------------------------
    // Static, not per-instance: CoverArtService.Apply constructs a new IgdbCoverArtProvider PER GAME PER
    // SCAN (mirroring SteamGridDbCoverArtProvider's own per-call construction) - without a shared cache,
    // scanning a library of any real size would re-authenticate against Twitch once per game. Keyed by
    // BOTH client id AND client secret (a composite key, not just the id) - a real, confirmed gap in an
    // earlier version of this class kept serving a token minted for an OLD secret under the SAME client
    // id after the secret changed in Settings, since the cache was keyed by id alone.
    //
    // Two synchronization objects, deliberately separate: TokenLock guards only the cached fields (held for
    // nanoseconds, never across I/O); TokenFetchGate serializes the actual token REQUEST (one Twitch call
    // for N concurrent lookups) and is a SemaphoreSlim so waiting for it can be cancelled.
    private static readonly object TokenLock = new();
    private static readonly SemaphoreSlim TokenFetchGate = new(1, 1);
    private static string? _cachedForClientId;
    private static string? _cachedForClientSecret;
    private static string? _cachedToken;
    private static DateTime _cachedTokenExpiresAtUtc;

    /// <summary>Longest a token is trusted from the cache, whatever the server claims - Twitch's real
    /// tokens last roughly 60 days.</summary>
    private const long MaxTokenLifetimeSeconds = 90L * 24 * 60 * 60;

    /// <summary>Test-only: clears the static token cache so tests exercising token acquisition/expiry
    /// don't leak state into each other. Production never calls this.</summary>
    internal static void ResetTokenCacheForTest()
    {
        lock (TokenLock)
        {
            _cachedForClientId = null;
            _cachedForClientSecret = null;
            _cachedToken = null;
            _cachedTokenExpiresAtUtc = default;
        }
    }

    private void InvalidateCachedToken()
    {
        lock (TokenLock)
        {
            if (_cachedForClientId == _clientId && _cachedForClientSecret == _clientSecret)
            {
                _cachedToken = null;
                _cachedForClientId = null;
                _cachedForClientSecret = null;
            }
        }
    }

    /// <summary>Thrown for a response that is structurally unusable (a malformed token body) - a distinct
    /// type so it is recognizably "the provider is unavailable", never confused with a search result.</summary>
    internal sealed class IgdbUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Validates a Twitch token response explicitly instead of trusting its shape: a missing or
    /// wrongly-typed field, or an expiry that isn't a positive integer that fits in 64 bits, is rejected.
    /// (The previous GetProperty/GetInt32 version threw KeyNotFoundException/FormatException for these -
    /// outside the provider's catch filter, so a bad response skipped every fallback provider.) An expiry
    /// that IS representable but absurdly large is clamped to MaxTokenLifetimeSeconds, not rejected.</summary>
    internal static (string Token, TimeSpan Lifetime) ParseTokenResponse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new IgdbUnavailableException("IGDB token response was not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new IgdbUnavailableException("IGDB token response was not a JSON object.");

            if (!root.TryGetProperty("access_token", out var tokenProp) || tokenProp.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenProp.GetString()))
            {
                throw new IgdbUnavailableException("IGDB token response had no usable access_token.");
            }

            if (!root.TryGetProperty("expires_in", out var expProp) || expProp.ValueKind != JsonValueKind.Number
                || !expProp.TryGetInt64(out var seconds) || seconds <= 0)
            {
                throw new IgdbUnavailableException("IGDB token response had no usable expires_in.");
            }

            return (tokenProp.GetString()!, TimeSpan.FromSeconds(Math.Min(seconds, MaxTokenLifetimeSeconds)));
        }
    }

    /// <summary>Test-only seam: when set, GetAccessToken returns this literal value instead of touching
    /// the token cache or Twitch at all - the seam every test that isn't specifically about token
    /// acquisition/expiry itself should use, so it can't be affected by (or pollute) the static cache.
    /// Production never sets this.</summary>
    internal string? AccessTokenOverrideForTest { get; set; }

    /// <summary>Test-only, transport-level seam: intercepts the actual Twitch token POST, given
    /// (clientId, clientSecret), returning the raw JSON response body Twitch would have. Production
    /// never sets this.</summary>
    internal Func<string, string, string>? TokenRequestOverride { get; set; }

    /// <summary>Test-only seam: bypasses the search+select step entirely. Production never sets this.</summary>
    internal Func<GameEntry, MatchedGame?>? SearchGameIdOverride { get; set; }

    /// <summary>Test-only, transport-level seam: intercepts the actual IGDB /v4/games POST, keyed by the
    /// exact apicalypse query body sent - proves QUERY GENERATION, not just response parsing. Production
    /// never sets this.</summary>
    internal Func<string, string>? SearchRequestOverride { get; set; }

    /// <summary>Test-only seam: bypasses the cover-image-URL lookup AND the download in one step, keyed
    /// by the matched IGDB game id - returns raw image bytes, or null for "no cover available" (the
    /// identity-resolved-but-no-usable-artwork case). Production never sets this.</summary>
    internal Func<int, byte[]?>? FetchCoverImageBytesOverride { get; set; }

    /// <summary>Test-only: shortens the image-download deadline (production: 8 seconds) so a stalled-read
    /// test doesn't have to actually wait 8 real seconds to prove the deadline fires.</summary>
    internal TimeSpan? ImageDownloadTimeoutOverrideForTest { get; set; }

    /// <summary>Test-only: shortens the time budget of each JSON request (token, /games, /covers) so a
    /// stalled-headers / stalled-body test doesn't wait the production 8 seconds. Production never sets
    /// this.</summary>
    internal TimeSpan? RequestTimeoutOverrideForTest { get; set; }

    public BitmapImage? GetCoverArt(GameEntry game) => GetCoverArt(game, out _);

    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, string? cacheDirOverride = null) =>
        GetCoverArt(game, out servedFromCache, out _, out _, cacheDirOverride);

    /// <summary>Same lookup as SteamGridDbCoverArtProvider.GetCoverArt, with an identical cache/evidence
    /// contract (servedFromCache, matched only set on a genuine successful match+cover, deleted/re-
    /// fetched on any invalid or stale-identity cache hit), plus an explicit `status` - see
    /// CoverLookupStatus - that CoverArtService.Apply uses to pick both its fallback behavior and its
    /// diagnostic.
    ///
    /// `ct` is the CALLER's cancellation (typically a scan being superseded). It is honored by token
    /// acquisition, the throttle wait, every API request, and the image download, and - unlike a timeout
    /// or any other failure, which become Unavailable - it is RETHROWN as OperationCanceledException: a
    /// cancelled scan must not be told "no artwork here" and then go on to start fallback network work
    /// for a game it no longer wants.</summary>
    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, out MatchedGame? matched,
        out CoverLookupStatus status, string? cacheDirOverride = null, CancellationToken ct = default)
    {
        servedFromCache = false;
        matched = null;
        status = CoverLookupStatus.NoMatch;
        try
        {
            ct.ThrowIfCancellationRequested();
            var searchName = game.CatalogName ?? game.Name;

            // Same guard SteamGridDbCoverArtProvider applies, and for the identical reason - a bare
            // multi-title hub name (Battle.net's/Xbox's "Call of Duty", etc.) has no name-matching rule,
            // however strict, that could recover which specific title is installed. CoverArtService.Apply
            // also checks this ONCE, ahead of both providers - this is defense in depth for anyone
            // constructing/calling this class directly, mirroring SteamGridDbCoverArtProvider's own
            // internal check.
            if (SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(searchName))
            {
                Logger.Warn($"IGDB: '{game.Name}' is a known multi-title umbrella product name - "
                    + "skipping automatic cover art rather than guessing which specific title is installed.");
                status = CoverLookupStatus.Ambiguous;
                return null;
            }

            var cacheDir = cacheDirOverride ?? CacheDir;
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{CacheVersion}.png");
            var cacheMetaPath = cachePath + ".meta.json";
            if (File.Exists(cachePath))
            {
                var cachedBytes = ProviderImageIo.ReadBoundedCacheFile(cachePath, "IGDB");
                var cached = cachedBytes is null ? null : ValidateBytes(cachedBytes, game.Name);
                var evidence = cached is not null ? TryReadMatchedGame(cacheMetaPath, cachedBytes!, searchName) : null;
                if (cached is not null && evidence is not null)
                {
                    servedFromCache = true;
                    matched = evidence;
                    status = CoverLookupStatus.Resolved;
                    return cached;
                }

                Logger.Warn(cached is null
                    ? $"IGDB: '{game.Name}' had a corrupt or invalid cached cover - deleting and re-fetching."
                    : $"IGDB: '{game.Name}' had a cached cover with missing, invalid, or stale-identity "
                        + "match evidence - deleting and re-fetching rather than trusting it unverified.");
                File.Delete(cachePath);
                TryDeleteMatchedGameFile(cacheMetaPath);
            }

            var selection = SearchGameIdOverride is not null
                ? new MatchSelection(SearchGameIdOverride(game), false)
                : SearchGameId(game, ct);

            if (selection.Ambiguous)
            {
                status = CoverLookupStatus.Ambiguous;
                return null;
            }

            var matchedGame = selection.Matched;
            if (matchedGame is null)
            {
                Logger.Warn($"IGDB: no confident match for '{game.Name}'.");
                status = CoverLookupStatus.NoMatch;
                return null;
            }

            byte[]? bytes;
            if (FetchCoverImageBytesOverride is not null)
                bytes = FetchCoverImageBytesOverride(matchedGame.Value.Id);
            else
            {
                var imageUrl = GetCoverImageUrl(matchedGame.Value.Id, ct);
                bytes = imageUrl is null ? null : FetchBoundedImageBytes(imageUrl, ct);
            }

            // Two reasons the identified game has no usable art: nothing to download (no cover listed,
            // missing on the CDN, or over the byte cap), or the bytes failed ArtworkImageValidator's
            // dimension/pixel/decode checks. Both are IdentifiedWithoutUsableArt - an earlier version
            // reported the second one as if nothing had been identified at all.
            var decoded = bytes is null ? null : ValidateBytes(bytes, game.Name);
            if (bytes is null || decoded is null)
            {
                status = CoverLookupStatus.IdentifiedWithoutUsableArt;
                Logger.Info($"IGDB: '{game.Name}' confidently identified as '{matchedGame.Value.Title}' "
                    + $"(id {matchedGame.Value.Id}) but has no usable cover image.");
                return null;
            }

            // A cancelled lookup caches nothing: checked HERE, before either write.
            ct.ThrowIfCancellationRequested();
            File.WriteAllBytes(cachePath, bytes);
            WriteMatchedGame(cacheMetaPath, matchedGame.Value, searchName, bytes);
            matched = matchedGame;
            status = CoverLookupStatus.Resolved;
            return decoded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A network/auth/rate-limit/parse failure is a DIFFERENT fact from "no confident match" -
            // recorded as Unavailable, and logged distinctly, so it can never be misread as "IGDB
            // doesn't have this game". Deliberately every non-cancellation exception, not an enumerated
            // list: see this class's own remarks on why an escaping exception skips the fallback chain.
            Logger.Warn($"IGDB: lookup failed for '{game.Name}' - unavailable, not a confident non-match.", ex);
            matched = null;
            status = CoverLookupStatus.Unavailable;
            return null;
        }
    }

    private readonly record struct MatchSelection(MatchedGame? Matched, bool Ambiguous);

    /// <summary>Searches game.CatalogName ?? game.Name (identical identity-input discipline to
    /// SteamGridDbCoverArtProvider.SearchGameId, for the identical reason - an incomplete raw detected
    /// name is a query-generation bug, not something a looser comparison downstream could safely paper
    /// over).</summary>
    private MatchSelection SearchGameId(GameEntry game, CancellationToken ct) =>
        SearchGameId(game.CatalogName ?? game.Name, ct);

    private MatchSelection SearchGameId(string searchName, CancellationToken ct)
    {
        var query = $"search \"{EscapeApicalypseString(searchName)}\"; fields name; limit 20;";

        // Token acquisition happens unconditionally, even when SearchRequestOverride substitutes the
        // actual HTTP response - SearchRequestOverride is transport-level for the SEARCH call
        // specifically (see its own remarks), not a bypass of authentication, so a test using it still
        // exercises (and can assert on) GetAccessToken's own caching and validation behavior.
        var token = GetAccessToken(ct);
        var responseJson = SearchRequestOverride is not null
            ? SearchRequestOverride(query)
            : SendApicalypseQuery("games", query, token, ct);
        var matched = SelectMatchedGame(responseJson, searchName, out var ambiguous);
        return new MatchSelection(matched, ambiguous);
    }

    /// <summary>Parses an IGDB /v4/games response (a bare JSON ARRAY, unlike SteamGridDB's {"data":[...]}
    /// wrapper) and applies the same exact-match discipline as SteamGridDbCoverArtProvider.
    /// IsConfidentMatch (reused directly, not re-implemented - one comparison rule, not two that could
    /// silently drift apart).
    ///
    /// Deliberately does NOT filter on IGDB's own parent_game/category/version_parent fields as a
    /// rejection rule - a real, confirmed proof-of-concept finding (Minecraft: id 135400 "Minecraft" has
    /// parent_game=121 "Minecraft: Java Edition" and is still the CORRECT entry for many installs) showed
    /// that a candidate having a parent_game does not by itself mean it's unsuitable DLC. Exact-text
    /// equality already excludes edition/DLC/season variants on its own (a colon-suffixed name like "...:
    /// Ultimate Edition" or "...: Season 1" fails IsConfidentMatch against a bare query the same way it
    /// already does for SteamGridDB), so a second, field-based filter would only risk wrongly excluding a
    /// legitimate exact match, not add real safety.
    ///
    /// The real safety net here is UNIQUENESS, reported via `ambiguous`: if more than one candidate in the
    /// SAME response independently passes IsConfidentMatch (two different products can share an exact
    /// collapsed title), this is Ambiguous, not a pick of whichever ranked first - IGDB's search ranking is
    /// not proven safer than SteamGridDB's own (the real A Way Out proof-of-concept result ranked the
    /// correct game eighth among unrelated titles), so rank order is never trusted as a tiebreaker.
    /// Uniqueness within THIS response says nothing about whether a same-named product exists elsewhere in
    /// the catalog. Ambiguous is a DIFFERENT fact from "no match" (`ambiguous=false`, return null).</summary>
    internal static MatchedGame? SelectMatchedGame(string responseJson, string gameNameForLogging, out bool ambiguous)
    {
        ambiguous = false;
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"IGDB search response for '{gameNameForLogging}' is not a JSON array - a malformed response, not a search result.");

        // Malformed data FAILS CLOSED (see CatalogResponseReader): a candidate that could hide a second match -
        // not an object, no usable name, or a matching name with no usable positive id - throws
        // InvalidDataException, which GetCoverArt's boundary reports as Unavailable. It used to be skipped, so
        // one readable exact match beside one unreadable one resolved as if it were unique.
        var confident = CatalogResponseReader.ReadConfidentCandidates(doc.RootElement, gameNameForLogging, "IGDB")
            .Select(c => new MatchedGame(c.Id, c.Name)).ToList();

        if (confident.Count == 0)
        {
            Logger.Warn($"IGDB: no confident match for '{gameNameForLogging}' among {doc.RootElement.GetArrayLength()} result(s).");
            return null;
        }

        if (confident.Count > 1)
        {
            Logger.Warn($"IGDB: '{gameNameForLogging}' matched {confident.Count} equally-confident, distinct "
                + $"candidates - ambiguous, skipping automatic match rather than guessing. "
                + $"ids=[{string.Join(",", confident.Select(c => c.Id))}]");
            ambiguous = true;
            return null;
        }

        Logger.Info($"  IGDB: '{gameNameForLogging}' matched '{confident[0].Title}' (id {confident[0].Id}).");
        return confident[0];
    }

    private string? GetCoverImageUrl(int gameId, CancellationToken ct)
    {
        var body = $"fields image_id; where game = {gameId};";
        var responseJson = SendApicalypseQuery("covers", body, GetAccessToken(ct), ct);
        return SelectCoverImageUrl(responseJson);
    }

    /// <summary>Parses an IGDB /v4/covers response (a bare array) into the first cover's image URL. A valid
    /// EMPTY array returns null (the identified game genuinely has no cover - IdentifiedWithoutUsableArt). A
    /// response that is valid JSON but the wrong shape - not an array, a first entry that is not an object, or
    /// one whose "image_id" is absent, not a string or blank - THROWS InvalidDataException, which GetCoverArt
    /// reports as Unavailable: an unreadable listing says nothing about whether the game has a cover, and used
    /// to be read as if it said it had none.</summary>
    internal static string? SelectCoverImageUrl(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("IGDB cover response is not a JSON array - a malformed response, not an empty listing.");

        if (doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("image_id", out var imageIdProp) || imageIdProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(imageIdProp.GetString()))
        {
            throw new InvalidDataException("IGDB cover response: the first cover has no usable \"image_id\" string.");
        }

        return $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageIdProp.GetString()}.jpg";
    }

    /// <summary>Sends one apicalypse request, throttled to IGDB's documented rate limit and cancellable at
    /// every step. Two independent, bounded recoveries:
    ///  - 401 (the access token was rejected - revoked, or minted for credentials that have since
    ///    changed): invalidate the cached token and retry EXACTLY ONCE with a freshly-minted one.
    ///  - 429 (rate limited): up to MaxRateLimitRetries retries, waiting the server's own Retry-After when
    ///    it supplies one (see ComputeRateLimitBackoff), abandoning outright if that wait exceeds
    ///    MaxRateLimitBackoff. Exhaustion throws, which GetCoverArt reports as Unavailable.</summary>
    private string SendApicalypseQuery(string endpoint, string body, string accessToken, CancellationToken ct)
    {
        // Relay mode has no token of ours to refresh: a 401 there means the server's own token is bad, which is an outage (Unavailable).
        var authRetried = _relay is not null;
        var rateLimitRetries = 0;

        while (true)
        {
            ThrottleForRateLimit(ct);

            var url = _relay is null ? $"https://api.igdb.com/v4/{endpoint}" : $"{_relay.Address.AbsoluteUri.TrimEnd('/')}/v4/{endpoint}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (_relay is null)
            {
                request.Headers.Add("Client-ID", _clientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            request.Content = new StringContent(body);

            // One deadline-and-cancellation scope per attempt (ProviderHttp.Send); the body is read - under
            // ProviderHttp.MaxJsonResponseBytes - only for a success, since a 401/429/error body is never used.
            var attempt = ProviderHttp.Send(ApiHttp, request, $"IGDB {endpoint}",
                RequestTimeoutOverrideForTest ?? ProviderHttp.DefaultRequestTimeout, ct, (response, token) =>
                    response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.TooManyRequests
                    || !response.IsSuccessStatusCode
                        ? new ApiAttempt(response.StatusCode, response.Headers.RetryAfter, null)
                        : new ApiAttempt(response.StatusCode, null,
                            ProviderHttp.ReadBoundedText(response, $"IGDB {endpoint}", ProviderHttp.MaxJsonResponseBytes, token)));

            if (attempt.Status == HttpStatusCode.Unauthorized && !authRetried)
            {
                authRetried = true;
                Logger.Warn("IGDB: access token was rejected (401) - invalidating the cached token and retrying once with a fresh one.");
                InvalidateCachedToken();
                accessToken = GetAccessToken(ct);
                continue;
            }

            if (attempt.Status == HttpStatusCode.TooManyRequests)
            {
                var delay = rateLimitRetries < MaxRateLimitRetries
                    ? ComputeRateLimitBackoff(rateLimitRetries, attempt.RetryAfter, DateTimeOffset.UtcNow)
                    : null;
                if (delay is null)
                {
                    throw new HttpRequestException($"IGDB {endpoint} request was rate limited (429) and could not be retried "
                        + $"(retries used: {rateLimitRetries}/{MaxRateLimitRetries}; server-requested waits over "
                        + $"{MaxRateLimitBackoff.TotalSeconds:0}s are not honored).");
                }

                rateLimitRetries++;
                Logger.Warn($"IGDB: {endpoint} request was rate limited (429) - retry {rateLimitRetries}/{MaxRateLimitRetries} after {delay.Value.TotalSeconds:0.##}s.");
                WaitForBackoff(delay.Value, ct);
                continue;
            }

            if (attempt.Body is null)
                throw new HttpRequestException($"IGDB {endpoint} request returned {(int)attempt.Status} {attempt.Status}.");

            return attempt.Body;
        }
    }

    /// <summary>What one apicalypse attempt produced, copied out of the response before it is disposed.
    /// Body is non-null only for a success.</summary>
    private readonly record struct ApiAttempt(HttpStatusCode Status, RetryConditionHeaderValue? RetryAfter, string? Body);

    private string GetAccessToken(CancellationToken ct)
    {
        if (AccessTokenOverrideForTest is not null)
            return AccessTokenOverrideForTest;

        if (_relay is not null)
            return ""; // the relay attaches the credentials; nothing is fetched, cached or sent from here

        ct.ThrowIfCancellationRequested();

        // A valid cached token never waits behind another caller's in-flight token request.
        if (TryGetCachedToken(out var cached))
            return cached;

        // The wait for the shared fetch gate is itself cancellable: with a plain lock, a caller whose
        // token was cancelled while ANOTHER caller's token request held the gate stayed blocked until
        // that request finished (potentially its whole HTTP timeout) - its own token only reached the
        // request it never got to send. Wait(ct) throws OperationCanceledException without acquiring, so
        // the release below is only reached on a successful acquisition.
        TokenFetchGate.Wait(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Re-check: whoever held the gate may have just populated the cache for these same credentials.
            if (TryGetCachedToken(out cached))
                return cached;

            var responseJson = TokenRequestOverride is not null
                ? TokenRequestOverride(_clientId, _clientSecret)
                : RequestNewToken(ct);

            // Validated BEFORE anything is cached: a malformed response must never poison the cache.
            var (token, lifetime) = ParseTokenResponse(responseJson);

            // 5 minutes of slack so a request already in flight when the token turns over doesn't race
            // real expiry.
            lock (TokenLock)
            {
                _cachedToken = token;
                _cachedForClientId = _clientId;
                _cachedForClientSecret = _clientSecret;
                _cachedTokenExpiresAtUtc = DateTime.UtcNow + (lifetime > TimeSpan.FromMinutes(5) ? lifetime - TimeSpan.FromMinutes(5) : TimeSpan.Zero);
            }

            return token;
        }
        finally
        {
            TokenFetchGate.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        lock (TokenLock)
        {
            if (_cachedToken is not null && _cachedForClientId == _clientId && _cachedForClientSecret == _clientSecret
                && DateTime.UtcNow < _cachedTokenExpiresAtUtc)
            {
                token = _cachedToken;
                return true;
            }
        }

        token = "";
        return false;
    }

    private string RequestNewToken(CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "client_credentials",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token") { Content = content };
        return ProviderHttp.Send(Http, request, "IGDB token", RequestTimeoutOverrideForTest ?? ProviderHttp.DefaultRequestTimeout, ct,
            (response, token) =>
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"IGDB token request returned {(int)response.StatusCode} {response.StatusCode}.");

                return ProviderHttp.ReadBoundedText(response, "IGDB token", ProviderHttp.MaxTokenResponseBytes, token);
            });
    }

    /// <summary>Bounded image download - the implementation now lives in ProviderImageIo so IGDB, SteamGridDB
    /// and Steam CDN share one contract (hard byte cap and hard time budget enforced DURING the read;
    /// null = "no usable image" (404 / over the cap); a stall, timeout or other HTTP failure THROWS so it
    /// becomes Unavailable; the caller's own cancellation propagates). See ProviderImageIo.</summary>
    private byte[]? FetchBoundedImageBytes(string url, CancellationToken ct) =>
        ProviderImageIo.FetchBoundedImageBytes(Http, url, "IGDB",
            ImageDownloadTimeoutOverrideForTest ?? ProviderImageIo.DefaultDownloadTimeout, ct);

    private static string EscapeApicalypseString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Routes bytes through the shared provider validation (ArtworkImageValidator.
    /// ValidateProviderBytes: original-dimension, pixel-count and full-decode checks - the same bounds a
    /// user-supplied local file goes through). A real, confirmed gap in an earlier version of this class
    /// decoded straight via CoverArtDecoder.Decode, which downsamples to a fixed display size regardless of
    /// how large the ORIGINAL was, so those limits were never checked for anything this provider displayed
    /// or cached.</summary>
    private static BitmapImage? ValidateBytes(byte[] bytes, string sourceForLogging) =>
        ArtworkImageValidator.ValidateProviderBytes(bytes, sourceForLogging);

    /// <summary>Identical design to SteamGridDbCoverArtProvider.CachedMatchEvidence - see its own remarks
    /// for why every field is validated the way it is (a bare "{}" must not be trusted, a SearchedName
    /// mismatch means a stale identity, an ImageSha256 mismatch means the sidecar doesn't actually
    /// describe the image sitting next to it).</summary>
    private readonly record struct CachedMatchEvidence(int Id, string Title, string SearchedName, string ImageSha256);

    private static string ComputeImageHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static MatchedGame? TryReadMatchedGame(string metaPath, byte[] cachedImageBytes, string currentSearchName)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;

            // Bounded (ProviderHttp.MaxSidecarBytes): an oversized sidecar is unverifiable evidence, exactly like
            // a corrupt one - null here makes the caller delete both files and re-fetch.
            var sidecarText = ProviderImageIo.ReadBoundedSidecarText(metaPath, "IGDB");
            if (sidecarText is null)
                return null;

            var evidence = JsonSerializer.Deserialize<CachedMatchEvidence>(sidecarText);
            if (evidence.Id <= 0 || string.IsNullOrWhiteSpace(evidence.Title) || string.IsNullOrWhiteSpace(evidence.ImageSha256))
                return null;

            if (!string.Equals(evidence.SearchedName, currentSearchName, StringComparison.Ordinal))
                return null;

            if (!string.Equals(evidence.ImageSha256, ComputeImageHash(cachedImageBytes), StringComparison.Ordinal))
                return null;

            return new MatchedGame(evidence.Id, evidence.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMatchedGame(string metaPath, MatchedGame matched, string searchName, byte[] imageBytes)
    {
        try
        {
            var evidence = new CachedMatchEvidence(matched.Id, matched.Title, searchName, ComputeImageHash(imageBytes));
            File.WriteAllText(metaPath, JsonSerializer.Serialize(evidence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"IGDB: couldn't write match evidence for cached cover '{metaPath}'.", ex);
        }
    }

    private static void TryDeleteMatchedGameFile(string metaPath)
    {
        try
        {
            if (File.Exists(metaPath))
                File.Delete(metaPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"IGDB: couldn't delete stale match evidence file '{metaPath}'.", ex);
        }
    }
}
