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
public sealed partial class SteamGridDbCoverArtProvider : ICoverArtProvider
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
    // v12: two real, confirmed IDENTITY (not comparison-strictness) gaps found from live v1.18.0 logs.
    // First: "Apex" (EA installs Apex Legends under a folder literally named that) searched and matched
    // an unrelated, differently-catalogued game also named exactly "Apex" - IsConfidentMatch was working
    // exactly as designed (rejecting the real "Apex Legends" storefront-tagged candidate as not an exact
    // match for the query "Apex", then accepting the one candidate that WAS an exact match for that
    // incomplete query). No amount of loosening the comparison could fix this safely; the query itself
    // was wrong. GameEntry.CatalogName (see EaScanner.ResolveCatalogName) now supplies the verified real
    // title as the search/comparison basis instead, for the short, explicit list of EA names known to be
    // abbreviated this way. Second: "AWayOut" (also EA, also a real folder name) returned zero SteamGridDB
    // search results at all - IsConfidentMatch's own collapsed comparison already recognizes "AWayOut" and
    // "A Way Out" as the same title, but that comparison never got the chance to run, because
    // SteamGridDB's own search/autocomplete never returned "A Way Out" as a candidate for a query with no
    // word-boundary spacing in the first place. SearchGameId now retries with SplitCompactedWords'
    // word-boundary-inserted form of the query whenever the raw query returns no confident match.
    // v13: B1 provider parity. (a) SelectMatchedGame now requires UNIQUENESS, exactly as IgdbCoverArtProvider
    // does: when more than one DISTINCT candidate in a response independently passes IsConfidentMatch (two
    // different products can share an exact collapsed title), the lookup is Ambiguous instead of picking
    // whichever ranked first or carried a matching storefront tag - so a cover cached under the old
    // first-confident-wins logic may belong to the wrong one of several same-titled products and must be
    // forgotten and re-evaluated. (b) Every downloaded image AND every cache hit now goes through
    // ArtworkImageValidator (original size/dimension/decode bounds) instead of CoverArtDecoder.Decode alone,
    // which downsamples and so never checked them; an entry cached before that existed is re-validated on
    // its next read rather than trusted.
    private const int CacheVersion = 13;

    /// <summary>Test-only: lets a test pre-populate a cache file under the exact real name GetCoverArt
    /// will look for, without hardcoding (and inevitably drifting from) the current CacheVersion.</summary>
    internal static int CacheVersionForTest => CacheVersion;

    private static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "CoverArtCache");

    /// <summary>Test-only seam, per INSTANCE (deliberately not a static switch - see IgdbCoverArtProvider.
    /// HttpHandlerOverrideForTest): when set, every real HTTP call this instance makes (search, grid lookup,
    /// image download) goes through a client wrapping THIS handler. Makes transport-level behavior - a
    /// non-success status, a stalled body, an endless body - testable through the real HttpClient pipeline.
    /// Production never sets this.</summary>
    internal HttpMessageHandler? HttpHandlerOverrideForTest { get; init; }

    /// <summary>Test-only: shortens the image download's own time budget so a stall test doesn't wait the
    /// production 8 seconds. Production never sets this.</summary>
    internal TimeSpan? ImageDownloadTimeoutOverrideForTest { get; init; }

    /// <summary>Test-only: shortens the time budget of each JSON request (search, grid listing) so a stalled-
    /// headers / stalled-body test doesn't wait the production 8 seconds. Production never sets this.</summary>
    internal TimeSpan? RequestTimeoutOverrideForTest { get; init; }

    private HttpClient? _testHttp;

    private HttpClient Http => HttpHandlerOverrideForTest is null
        ? RealHttp
        : _testHttp ??= new HttpClient(HttpHandlerOverrideForTest, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(8) };

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
    internal Func<GameEntry, MatchedGame?>? SearchGameIdOverride { get; set; }

    /// <summary>Test-only, transport-level seam: intercepts SearchGameId's actual HTTP round-trip, keyed
    /// by the EXACT query text that would be sent (URL-encoded and all) - given the raw search string,
    /// returns the raw response body SteamGridDB would have. Unlike SearchGameIdOverride (which replaces
    /// the whole search+select step and so can't prove what text was actually searched for), this seam
    /// exists specifically to verify QUERY GENERATION itself: that a verified CatalogName is what's
    /// actually searched for instead of the raw abbreviated Name, and that a no-match primary search
    /// actually retries with SplitCompactedWords' expanded text - not merely that a correctly-shaped
    /// response, however it arrived, gets parsed correctly (SelectMatchedGame's own tests already cover
    /// that). Production code never sets this.</summary>
    internal Func<string, string>? SearchRequestOverride { get; set; }

    /// <summary>Test-only seam: bypasses GetGridImageUrl AND the image download in one step, keyed by
    /// the matched SteamGridDB game id - returns raw image bytes, or null for "no grid art available".
    /// Needed for transport-level tests that exercise SearchRequestOverride/SearchGameIdOverride through
    /// to a real decoded result (and so real `matched` evidence) without ever reaching the real network
    /// for the grid lookup/download half of GetCoverArt, which has no override of its own otherwise.
    /// Production code never sets this.</summary>
    internal Func<int, byte[]?>? FetchGridImageBytesOverride { get; set; }

    public BitmapImage? GetCoverArt(GameEntry game) => GetCoverArt(game, out _);

    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, string? cacheDirOverride = null,
        CancellationToken ct = default) =>
        GetCoverArt(game, out servedFromCache, out _, cacheDirOverride, ct);

    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, out MatchedGame? matched,
        string? cacheDirOverride = null, CancellationToken ct = default) =>
        GetCoverArt(game, out servedFromCache, out matched, out _, cacheDirOverride, ct);

    /// <summary>Same lookup, but also reports whether the result came from this provider's own on-disk
    /// cache rather than a fresh network fetch just now - CoverArtService.Apply needs this to record
    /// accurate retrieval evidence (ArtworkRetrievalMethod) instead of manufacturing "NetworkDownload"
    /// for what was actually a cache hit - which SteamGridDB game id/title the match actually resolved to
    /// (so it can persist that as matching evidence instead of leaving ArtworkSelection.ProviderGameId/
    /// ProviderTitle permanently null), and an explicit `status` (see CoverLookupStatus) so an ambiguous
    /// identity, a provider outage and a genuine non-match are never conflated.
    /// `matched` is populated on a cache hit too, via a small sidecar file written alongside the cached
    /// image at fetch time - without that, every scan AFTER the first (which is always a cache hit, since
    /// SafeApplyCoverArt re-runs the automatic matcher unconditionally for any non-user-selected selection)
    /// would silently null out evidence a truly fresh match had just recorded moments earlier.
    /// `cacheDirOverride` is test-only (production never passes it, always resolving under AppPaths.
    /// DataDir) - same per-call-override pattern as ArtworkAssetStore.TryResolvePath, for the same reason
    /// (xUnit's default parallel test execution).
    ///
    /// This class is a PROVIDER BOUNDARY, like IgdbCoverArtProvider: every failure is converted into
    /// CoverLookupStatus.Unavailable rather than escaping - EXCEPT the caller's own cancellation (`ct`), which
    /// propagates as OperationCanceledException and is never a lookup outcome. `ct` reaches every stage: each
    /// request (deadline and cancellation share one scope, see ProviderHttp), each body read, and the checks
    /// between stages; it is checked before anything is written, so a cancelled lookup caches nothing.
    ///
    /// Every image - freshly downloaded OR a cache hit - is read under a hard byte cap and validated by
    /// ArtworkImageValidator (original size/dimension/decode bounds) before it is displayed or written.
    /// A cache entry that fails is deleted and re-fetched, exactly like a corrupt one.</summary>
    public BitmapImage? GetCoverArt(GameEntry game, out bool servedFromCache, out MatchedGame? matched,
        out CoverLookupStatus status, string? cacheDirOverride = null, CancellationToken ct = default)
    {
        servedFromCache = false;
        matched = null;
        status = CoverLookupStatus.NoMatch;
        try
        {
            ct.ThrowIfCancellationRequested();

            if (IsAmbiguousUmbrellaProduct(game.Name))
            {
                Logger.Warn($"SteamGridDB: '{game.Name}' is a known multi-title umbrella product name - "
                    + "skipping automatic cover art rather than guessing which specific title is installed.");
                status = CoverLookupStatus.Ambiguous;
                return null;
            }

            var searchName = game.CatalogName ?? game.Name;
            var cacheDir = cacheDirOverride ?? CacheDir;
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, $"{game.Id}-v{CacheVersion}.png");
            var cacheMetaPath = cachePath + ".meta.json";
            if (File.Exists(cachePath))
            {
                var cachedBytes = ProviderImageIo.ReadBoundedCacheFile(cachePath, "SteamGridDB");
                var cached = cachedBytes is null ? null : LoadBitmap(cachedBytes, game.Name);
                var evidence = cached is not null ? TryReadMatchedGame(cacheMetaPath, cachedBytes!, searchName) : null;
                if (cached is not null && evidence is not null)
                {
                    servedFromCache = true;
                    matched = evidence;
                    status = CoverLookupStatus.Resolved;
                    return cached;
                }

                // Three distinct reasons land here, all handled the same way (delete and re-fetch):
                // the image itself failed the shared validation (a corrupt file - e.g. an interrupted write
                // from a previous crash - or one whose ORIGINAL dimensions/size exceed the bounds, which
                // this path used to accept because it only decoded a downsampled copy); the sidecar is
                // missing, malformed, or doesn't actually describe THESE cached bytes (see
                // TryReadMatchedGame's own remarks); or the sidecar is well-formed but was written for a
                // DIFFERENT resolved identity than the one being searched for now (the real, confirmed gap
                // this closes - a cache entry produced under a still-abbreviated name, or under a
                // since-corrected one, must not silently keep being served once the resolved identity has
                // changed - see this class's own CacheVersion remarks: a version bump alone only clears
                // mistakes ONCE, at release time, not for an identity correction that happens to land
                // afterward, e.g. via Reset picking up a CatalogName the earlier scan that first cached
                // this image never had). Without deleting it, every one of these would fail/mismatch
                // identically forever instead of self-healing on the very next call.
                Logger.Warn(cached is null
                    ? $"SteamGridDB: '{game.Name}' had a corrupt or invalid cached cover - deleting and re-fetching."
                    : $"SteamGridDB: '{game.Name}' had a cached cover with missing, invalid, or stale-identity "
                        + "match evidence - deleting and re-fetching rather than trusting it unverified.");
                File.Delete(cachePath);
                TryDeleteMatchedGameFile(cacheMetaPath);
            }

            ct.ThrowIfCancellationRequested();
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
                Logger.Warn($"SteamGridDB: no match for '{game.Name}'.");
                status = CoverLookupStatus.NoMatch;
                return null;
            }

            byte[]? bytes;
            if (FetchGridImageBytesOverride is not null)
            {
                ct.ThrowIfCancellationRequested();
                bytes = FetchGridImageBytesOverride(matchedGame.Value.Id);
            }
            else
            {
                var imageUrl = GetGridImageUrl(matchedGame.Value.Id, ct);
                bytes = imageUrl is null
                    ? null
                    : ProviderImageIo.FetchBoundedImageBytes(Http, imageUrl, "SteamGridDB",
                        ImageDownloadTimeoutOverrideForTest ?? ProviderImageIo.DefaultDownloadTimeout, ct);
            }

            // Two reasons the identified game has no usable art: nothing to download (no grid listed,
            // missing on the CDN, or over the byte cap), or the bytes failed the shared validation. Both
            // are IdentifiedWithoutUsableArt - validated BEFORE caching: writing unvalidated bytes to disk
            // first means a bad response (truncated download, an HTML error page served with a 200) becomes
            // a corrupt cache file that fails identically - and gets deleted and re-fetched - on every
            // future scan.
            var decoded = bytes is null ? null : LoadBitmap(bytes, game.Name);
            if (bytes is null || decoded is null)
            {
                status = CoverLookupStatus.IdentifiedWithoutUsableArt;
                Logger.Warn($"SteamGridDB: matched '{game.Name}' as '{matchedGame.Value.Title}' "
                    + $"(id {matchedGame.Value.Id}) but it has no usable grid art.");
                return null;
            }

            // A cancelled lookup caches nothing: checked HERE, before either write, so the (uninterruptible, but
            // tiny) file writes are never begun for a lookup the caller no longer wants.
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
            // A network/auth/parse failure is a DIFFERENT fact from "no confident match" - recorded as
            // Unavailable, and logged distinctly, so it can never be misread as "SteamGridDB doesn't have
            // this game". Deliberately every non-cancellation exception, not an enumerated list (see
            // IgdbCoverArtProvider): an escaping exception would reach SafeApplyCoverArt's outer net and skip
            // Steam CDN.
            Logger.Warn($"SteamGridDB: lookup failed for '{game.Name}' - unavailable, not a confident non-match.", ex);
            matched = null;
            status = CoverLookupStatus.Unavailable;
            return null;
        }
    }

    private readonly record struct MatchSelection(MatchedGame? Matched, bool Ambiguous);

    /// <summary>Identifies which SteamGridDB game/title a search resolved to - Id is the catalog id
    /// GetGridImageUrl needs; Title is the candidate's own name, kept as matching evidence (see
    /// ArtworkSelection.ProviderGameId/ProviderTitle). Public since it appears in GetCoverArt's own
    /// public signature.</summary>
    public readonly record struct MatchedGame(int Id, string Title);

    /// <summary>
    /// A bare name search ("Apex" for a folder called just "Apex") can match multiple unrelated
    /// games - live check found "Apex" (an obscure title) ranked above "Apex Legends" for that exact
    /// query. SteamGridDB tags each result with which storefronts carry it (steam/egs/origin/gog), so
    /// when we know the game's source, a result actually listed under that storefront is strong
    /// evidence it's the right one - "Apex Legends" is the only "Apex"-prefixed result tagged
    /// "origin", which is exactly our signal for an EA-sourced game. Falls back to the top result
    /// when nothing carries a matching tag, which is the previous (naive) behaviour.
    ///
    /// Searches game.CatalogName instead of game.Name when a verified one is available (see
    /// GameEntry.CatalogName/EaScanner.ResolveCatalogName) - the real, confirmed "Apex" -> unrelated-
    /// game-also-named-"Apex" false positive was IsConfidentMatch correctly rejecting the true
    /// "Apex Legends" storefront-tagged candidate because the QUERY itself was an incomplete
    /// abbreviation, not a comparison-strictness gap.
    ///
    /// If that search finds no confident match at all, retries once with SplitCompactedWords' word-
    /// boundary-spaced form of the same name - a second real, confirmed gap: "AWayOut" (also an EA
    /// folder name) returned zero results from SteamGridDB's own search/autocomplete outright, even
    /// though IsConfidentMatch's collapsed comparison already recognizes it as identical to "A Way
    /// Out" once a candidate by that name is actually among the results to compare against.
    /// </summary>
    private MatchSelection SearchGameId(GameEntry game, CancellationToken ct)
    {
        var searchName = game.CatalogName ?? game.Name;
        if (!string.Equals(searchName, game.Name, StringComparison.Ordinal))
            Logger.Info($"  SteamGridDB: '{game.Name}' has a verified catalog title - searching as '{searchName}'.");

        var found = SearchGameId(game, searchName, ct);
        // An Ambiguous primary search is a settled fact about the IDENTITY, not a failure to find one: it
        // must not fall through to the compacted-words retry, which would be a second attempt to guess.
        if (found.Matched is not null || found.Ambiguous)
            return found;

        var expanded = SplitCompactedWords(searchName);
        if (string.Equals(expanded, searchName, StringComparison.Ordinal))
            return found;

        Logger.Info($"  SteamGridDB: '{searchName}' returned no confident match - retrying as '{expanded}'.");
        ct.ThrowIfCancellationRequested();
        return SearchGameId(game, expanded, ct);
    }

    /// <summary>Runs one actual search/autocomplete request for `queryText` (which may differ from
    /// game.Name/game.CatalogName - see SearchGameId's own remarks on the catalog-name substitution and
    /// the compacted-words retry) and selects a match from it. `queryText` is also what's compared
    /// against each candidate (via SelectMatchedGame -> IsConfidentMatch): comparing against whichever
    /// name variant was actually searched for is always at least as correct as comparing against the
    /// raw game.Name, and IsConfidentMatch's own collapsed comparison already ignores the spacing
    /// differences SplitCompactedWords introduces, so this never makes a real match harder to confirm.
    /// A non-success HTTP status THROWS (it becomes Unavailable): an authentication or server failure says
    /// nothing about whether SteamGridDB has the game, and used to be returned as if it were a non-match.</summary>
    private MatchSelection SearchGameId(GameEntry game, string queryText, CancellationToken ct) =>
        SearchQuery(StorefrontTagFor(game.Source), queryText, ct);

    private MatchSelection SearchQuery(string? storefrontTag, string queryText, CancellationToken ct)
    {
        string responseJson;
        if (SearchRequestOverride is not null)
        {
            ct.ThrowIfCancellationRequested();
            responseJson = SearchRequestOverride(queryText);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(queryText)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            responseJson = ProviderHttp.Send(Http, request, "SteamGridDB search",
                RequestTimeoutOverrideForTest ?? ProviderHttp.DefaultRequestTimeout, ct, (response, token) =>
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"SteamGridDB search request returned {(int)response.StatusCode} "
                            + $"{response.StatusCode}{(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? " - check the API key in Settings" : "")}.");
                    }

                    return ProviderHttp.ReadBoundedText(response, "SteamGridDB search", ProviderHttp.MaxJsonResponseBytes, token);
                });
        }

        var matched = SelectMatchedGame(responseJson, queryText, storefrontTag, out var ambiguous);
        return new MatchSelection(matched, ambiguous);
    }

    /// <summary>Parses SteamGridDB's /search/autocomplete response body and selects a game id from it. Same
    /// contract as SelectMatchedGame (which this wraps): a malformed response THROWS InvalidDataException
    /// rather than returning null - null means only "no confident match", never "unreadable".</summary>
    internal static int? SelectGameId(string responseJson, string gameNameForLogging, string? storefrontTag) =>
        SelectMatchedGame(responseJson, gameNameForLogging, storefrontTag)?.Id;

    /// <summary>Same selection as SelectGameId, but also returns the matched candidate's own title as
    /// evidence (see MatchedGame) - split out so SelectGameId itself (and every existing test against
    /// it) keeps working unchanged while GetCoverArt/CoverArtService.Apply can now record what was
    /// actually matched instead of leaving ArtworkSelection.ProviderGameId/ProviderTitle permanently
    /// null. An ambiguous response reports null here exactly as "no match" always did; callers that need
    /// to tell the two apart use the overload with `ambiguous`.</summary>
    internal static MatchedGame? SelectMatchedGame(string responseJson, string gameNameForLogging, string? storefrontTag) =>
        SelectMatchedGame(responseJson, gameNameForLogging, storefrontTag, out _);

    /// <summary>Parses SteamGridDB's /search/autocomplete response body and applies the same exact-match
    /// discipline AND the same UNIQUENESS requirement as IgdbCoverArtProvider.SelectMatchedGame (B1 provider
    /// parity - IsConfidentMatch is shared, not re-implemented).
    ///
    /// Every candidate is evaluated, not just the first: exactly ONE distinct candidate id passing
    /// IsConfidentMatch is a match; more than one is `ambiguous` (two different products can share an exact
    /// collapsed title - e.g. separately catalogued editions or a remake with the original's name), reported
    /// as null with ambiguous=true, never a pick of whichever ranked first. Rank order is not evidence:
    /// SteamGridDB's search is a fuzzy text search, and the same real A Way Out proof-of-concept that
    /// showed IGDB ranking the correct game eighth applies to any free-text search. A repeated id is one
    /// catalog entry, not two.
    ///
    /// The storefront tag (`storefrontTag`, which storefronts carry a result) is deliberately NOT a
    /// tiebreaker any more. It used to be the "strongest signal" that let a tagged candidate win over an
    /// untagged one, but a storefront tag says a game with this name is sold there, not that it is THIS
    /// product - two separately catalogued same-titled products are both plausibly tagged - so under a
    /// uniqueness rule it cannot be allowed to resolve a real ambiguity. It is kept for diagnostics only.
    ///
    /// Malformed data FAILS CLOSED (see CatalogResponseReader): a response that is not {"data": [...]}, or a
    /// candidate that could hide a second match (not an object, no usable name, or a matching name with no
    /// usable positive id), throws InvalidDataException, which GetCoverArt's provider boundary reports as
    /// Unavailable. It used to skip such a candidate and carry on, so {id:111 "Apex Legends"} beside
    /// {id:"invalid" "Apex Legends"} resolved to 111 even though the second entry might be a different product;
    /// and a missing name became the literal "unknown". A valid empty "data" list is still a plain null with
    /// ambiguous=false. JsonDocument.Parse itself throws JsonException for invalid JSON - also Unavailable.</summary>
    internal static MatchedGame? SelectMatchedGame(string responseJson, string gameNameForLogging, string? storefrontTag, out bool ambiguous)
    {
        ambiguous = false;
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"SteamGridDB search response for '{gameNameForLogging}' is not {{\"data\": [...]}} - a malformed response, not a search result.");

        // A valid empty list is a genuine "nothing found"; anything unreadable in a non-empty one throws (see
        // CatalogResponseReader) rather than being skipped - a skipped candidate would silently establish a
        // uniqueness the response never demonstrated.
        var confident = CatalogResponseReader.ReadConfidentCandidates(data, gameNameForLogging, "SteamGridDB");

        if (confident.Count == 1)
        {
            var only = confident[0];
            var tagged = HasStorefrontTag(only.Element, storefrontTag);
            Logger.Info($"  SteamGridDB: '{gameNameForLogging}' matched '{only.Name}' (id {only.Id}"
                + (storefrontTag is null ? ")." : tagged ? $", tagged '{storefrontTag}')." : $", not tagged '{storefrontTag}')."));
            return new MatchedGame(only.Id, only.Name);
        }

        if (confident.Count > 1)
        {
            Logger.Warn($"SteamGridDB: '{gameNameForLogging}' matched {confident.Count} equally-confident, distinct "
                + "candidates - ambiguous, skipping automatic match rather than guessing. "
                + $"ids=[{string.Join(",", confident.Select(c => c.Id))}]");
            ambiguous = true;
            return null;
        }

        if (data.GetArrayLength() > 0)
        {
            // Nothing confident - dump every candidate actually considered (id/title/storefront tags, never
            // anything from the request itself) so "no confident match among N results" can be told apart
            // from a genuine catalog gap without re-running this search by hand against the live API. Every
            // name here was readable (an unreadable one would have thrown above); an id may not be.
            var candidates = new List<string>();
            foreach (var candidate in data.EnumerateArray())
            {
                var storefronts = candidate.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array
                    ? string.Join(",", types.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()))
                    : "";
                CatalogResponseReader.TryReadName(candidate, out var name);
                candidates.Add($"id={(CatalogResponseReader.TryReadId(candidate, out var id) ? id.ToString() : "?")} name='{name}' storefronts=[{storefronts}]");
            }

            Logger.Warn($"SteamGridDB: no confident match for '{gameNameForLogging}' among "
                + $"{data.GetArrayLength()} search result(s): {string.Join(" | ", candidates)}");
        }

        return null;
    }

    private static bool HasStorefrontTag(JsonElement candidate, string? storefrontTag) =>
        storefrontTag is not null
        && candidate.ValueKind == JsonValueKind.Object
        && candidate.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array
        && types.EnumerateArray().Any(t =>
            t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), storefrontTag, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>The normalization IsConfidentMatch compares titles under (roman-numeral sequel markers as digits,
    /// then letters and digits only, lower-cased) - exposed so the identity layer's "same canonical title" and
    /// fingerprint decisions cannot drift from the matcher's own.</summary>
    internal static string CollapsedTitle(string name) => CollapseForComparison(NormalizeRomanNumerals(name));

    private static string CollapseForComparison(string name) =>
        new string(TrademarkText.Replace(name, "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Trademark notices written as TEXT - "Call of Duty(R)", "Forza(TM)", "(C)", "(SM)" - which launchers (the Xbox Start Menu, for one) put
    // in a display name. The symbols themselves (a registered/trademark sign) vanish under the letters-and-digits collapse, but these leave a
    // stray letter behind ("callofdutyr"), so the umbrella name and every exact-title comparison silently failed for exactly those games.
    private static readonly System.Text.RegularExpressions.Regex TrademarkText =
        new(@"\s*\((?:tm|r|c|sm)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The title as it should be SEARCHED: textual and symbol trademark notices removed, spacing tidied. A title that is nothing
    /// but notices comes back unchanged, never empty. Comparison already ignores these (CollapsedTitle); this makes the text sent to a
    /// catalog, and shown as the picker's starting query, match.</summary>
    internal static string StripTrademarks(string name)
    {
        var stripped = TrademarkText.Replace(name, "");
        stripped = new string(stripped.Where(c => c is not ('\u2122' or '\u00AE' or '\u00A9' or '\u2120')).ToArray());
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s{2,}", " ").Trim();
        return stripped.Length == 0 ? name : stripped;
    }

    // Matches the boundary between two words glued together with no separator - "AWayOut" - so the
    // string can be re-split into "A Way Out" before being sent to SteamGridDB's own search/autocomplete,
    // which (confirmed live: a query of "AWayOut" itself returned zero results) appears to tokenize on
    // whitespace rather than fuzzy-matching a compacted run of words. Two rules, applied together:
    // lower-to-upper ("ayOut" -> "ay Out") and a run of capitals followed by a lowercase letter
    // ("AWay" -> "A Way", splitting before the LAST capital in the run rather than the first, since that
    // last capital starts the next word). This is a generic, reversible text transformation - never a
    // per-game guess - so it can only ever add a retry opportunity; a name with no such boundary
    // (SplitCompactedWords("Apex") == "Apex") is returned unchanged and SearchGameId skips the retry.
    private static readonly System.Text.RegularExpressions.Regex CompactedWordBoundary =
        new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string SplitCompactedWords(string name) => CompactedWordBoundary.Replace(name, " ");

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

    private string? GetGridImageUrl(int gameId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var listing = ProviderHttp.Send<string?>(Http, request, "SteamGridDB grid listing",
            RequestTimeoutOverrideForTest ?? ProviderHttp.DefaultRequestTimeout, ct, (response, token) =>
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Logger.Warn($"SteamGridDB: game {gameId} has no grid listing (404).");
                    return null; // nothing listed: the identified game simply has no art
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Not "no art": an auth/server failure says nothing about whether this game has a grid.
                    // Thrown so GetCoverArt reports Unavailable instead of IdentifiedWithoutUsableArt.
                    throw new HttpRequestException($"SteamGridDB grid request returned {(int)response.StatusCode} {response.StatusCode}.");
                }

                return ProviderHttp.ReadBoundedText(response, "SteamGridDB grid listing", ProviderHttp.MaxJsonResponseBytes, token);
            });

        return listing is null ? null : SelectGridImageUrl(listing);
    }

    /// <summary>Parses SteamGridDB's /grids/game/{id} response body and picks the first grid's url. A valid
    /// EMPTY listing returns null (the identified game genuinely has no grid - IdentifiedWithoutUsableArt).
    /// A response that is valid JSON but the wrong shape - no "data" array, a first entry that is not an object,
    /// or one whose "url" is absent, not a string, blank or not an absolute http(s) address - THROWS
    /// InvalidDataException, which GetCoverArt reports as Unavailable: an unreadable listing says nothing about
    /// whether the game has art, and used to be read as if it said it had none.</summary>
    internal static string? SelectGridImageUrl(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SteamGridDB grid response is not {\"data\": [...]} - a malformed response, not an empty listing.");

        if (data.GetArrayLength() == 0)
            return null;

        var first = data[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("SteamGridDB grid response: the first grid has no \"url\" string.");
        }

        var url = urlProp.GetString();
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidDataException("SteamGridDB grid response: the first grid's \"url\" is not an absolute http(s) address.");
        }

        return url;
    }

    /// <summary>Shared validation (ArtworkImageValidator.ValidateProviderBytes) for every image this provider
    /// displays or caches - a fresh download and a cache hit alike. Not CoverArtDecoder.Decode: that
    /// downsamples to a fixed display width, so it never checked the ORIGINAL's size or dimensions.</summary>
    private static BitmapImage? LoadBitmap(byte[] bytes, string sourceForLogging) =>
        ArtworkImageValidator.ValidateProviderBytes(bytes, sourceForLogging);

    /// <summary>The sidecar's actual on-disk shape - deliberately NOT the same type GetCoverArt returns
    /// (MatchedGame), which only carries what CoverArtService.Apply needs. This carries two additional
    /// fields solely so a cache HIT can be validated before ever being trusted: SearchedName is the
    /// resolved search identity (game.CatalogName ?? game.Name) THIS entry was fetched under, compared
    /// against the CURRENT resolved identity on every read - a real, confirmed gap otherwise: a
    /// CacheVersion bump alone only clears mistakes once, at release time, but an identity correction
    /// that lands afterward (Reset picking up a CatalogName the scan that first cached this image never
    /// had, or a future KnownAbbreviatedCatalogNames addition) would otherwise leave a stale-identity
    /// cache entry being served indefinitely. ImageSha256 binds this sidecar to the SPECIFIC cached
    /// image bytes it was written for - the image and its sidecar are two separate files written one
    /// after the other (see GetCoverArt), so an old sidecar surviving a failed/interrupted rewrite of
    /// the image itself must never be attributed to whatever image happens to be sitting at cachePath
    /// now.</summary>
    private readonly record struct CachedMatchEvidence(int Id, string Title, string SearchedName, string ImageSha256);

    private static string ComputeImageHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>Reads the matched-game evidence sidecar written alongside a cached cover image (see
    /// GetCoverArt's own remarks on why a cache hit needs this) - null for anything short of FULLY
    /// verified evidence: a missing/corrupt/unrecognized-JSON sidecar (including one written before this
    /// validation existed, whose absent new fields deserialize to null/0 and so fail the checks below
    /// the same way); a non-positive id or empty title (a bare `{}` deserializes to exactly these
    /// defaults - default field values are not evidence, and must never be presented as if they were);
    /// a SearchedName that doesn't match `currentSearchName` (the cached image was resolved under a
    /// DIFFERENT identity than the one being searched for now - stale, not wrong-shaped); or a hash that
    /// doesn't match `cachedImageBytes` (the sidecar doesn't actually describe this image - see
    /// CachedMatchEvidence's own remarks on why the two files can disagree). GetCoverArt treats every one
    /// of these identically: delete both files and re-fetch, rather than silently keep serving something
    /// that can no longer be verified.</summary>
    private static MatchedGame? TryReadMatchedGame(string metaPath, byte[] cachedImageBytes, string currentSearchName)
    {
        try
        {
            if (!File.Exists(metaPath))
                return null;

            // Bounded (ProviderHttp.MaxSidecarBytes): an oversized sidecar is unverifiable evidence, exactly like
            // a corrupt one - null here makes the caller delete both files and re-fetch.
            var sidecarText = ProviderImageIo.ReadBoundedSidecarText(metaPath, "SteamGridDB");
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
            Logger.Warn($"SteamGridDB: couldn't write match evidence for cached cover '{metaPath}'.", ex);
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
        }
    }
}
