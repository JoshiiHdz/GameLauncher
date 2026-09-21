using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

/// <summary>
/// Picks the best available art for a game, in this order - required, per explicit product decision, to
/// be IGDB FIRST even for Steam games, not just "everything except Steam" (an earlier version of this
/// class tried Steam CDN before IGDB unconditionally, which contradicted that decision):
///   1. IGDB, if credentials are configured - the primary automatic provider (see IgdbCoverArtProvider's
///      own remarks for why).
///   2. SteamGridDB, as a fallback when IGDB is unavailable, unconfident, or has no cover.
///   3. Steam's free public CDN, for Steam games specifically (no key needed, always an exact match by
///      app ID - the one provider here that never performs a name search at all, so it runs regardless of
///      whether IGDB/SteamGridDB found anything, including when IGDB's identity was AMBIGUOUS: an exact
///      appid lookup isn't a guess, so ambiguity elsewhere doesn't make it any less safe).
///   4. The exe icon, when nothing above has art.
///
/// A known multi-title umbrella product name (see SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct)
/// skips BOTH name-searching providers (IGDB and SteamGridDB) entirely, checked ONCE here so a future
/// third name-searching provider inherits the same protection automatically, rather than needing its own
/// copy of the same guard. IGDB and SteamGridDB are each queried independently, with their OWN identifier
/// namespace - an IGDB game id is never handed to SteamGridDB (or vice versa) as if the two catalogs
/// shared one id space. If IGDB itself reports its identity as AMBIGUOUS (more than one equally-confident
/// candidate), SteamGridDB is not tried either - ambiguous identity is not equivalent to "this provider
/// doesn't have artwork," and letting a second, independent name search guess against the same ambiguous
/// name would reopen exactly the risk the ambiguity check exists to prevent.
/// </summary>
public static class CoverArtService
{
    /// <summary>Runs the AUTOMATIC matcher and applies its result to game.Icon/IsCoverArt exactly as
    /// before, but now also returns the match's identity/evidence (null if nothing was found and Icon
    /// fell back to the exe icon) so the caller can record it as automatic metadata - see
    /// GameScannerService.SafeApplyCoverArt and LibraryViewModel's revision-guarded reconciliation.
    /// Does NOT consult any existing selection - callers decide whether to call this at all versus
    /// ApplyStored/ApplyStoredSafely when a user selection already exists (see SafeApplyCoverArt).
    ///
    /// steamGridDbApiKey/igdbClientId/igdbClientSecret are snapshots of the corresponding AppSettings/
    /// IgdbCredentialStore values taken by the caller, not live references - GameScannerService runs this
    /// from a background thread, and AppSettings is a mutable object the UI thread can be editing at the
    /// same moment (Settings window key entry, among others). Taking just the values actually needed
    /// avoids retaining any live, shared-mutable-state reference on the worker thread.
    ///
    /// igdbProviderOverride/steamGridDbProviderOverride/steamProviderOverride are test-only seams (production always
    /// constructs a real provider from the credential strings above when null) - the same
    /// optional-parameter-as-test-seam idiom ApplyStoredSafely's own `getIcon` parameter already uses in
    /// this file, letting a test inject a provider instance with ITS OWN transport-level seams
    /// (SearchRequestOverride etc.) already set, to prove the fallback ORDERING itself - not just each
    /// provider's own internal matching logic, which SteamGridDbCoverArtProviderTests/
    /// IgdbCoverArtProviderTests already cover in isolation.
    ///
    /// cacheDirOverride is test-only too (production always resolves each provider's own real
    /// %AppData% cache dir) - forwarded to every provider called here so a test exercising Apply's
    /// automatic path directly (with a real, non-overridden GetCoverArt call) can never write into the
    /// real CoverArtCache, the same isolation discipline every other test touching these providers
    /// already requires.
    ///
    /// `ct` is the CALLER's cancellation (typically GameScannerService.ScanAllAsync's scan-level token).
    /// It is passed INTO every provider - IGDB, SteamGridDB and Steam CDN alike - so it bounds each one's
    /// requests and body reads, and it is checked again before EACH later provider: a superseded scan must
    /// neither sit in a stalled request nor go on to start further network work for a game it no longer wants.
    /// Cancellation propagates as OperationCanceledException and is never converted into "no artwork found"
    /// (SafeApplyCoverArt rethrows it rather than falling back to the exe icon).
    ///
    /// diagnosticSinkForTest is test-only: receives the fallback diagnostic for an IGDB outcome, and for a
    /// SteamGridDB stage that produced no art (the same text Logger gets; nothing is emitted for a stage
    /// that resolved), so a test can prove that e.g. an authentication/network failure is reported as
    /// unavailable rather than as "no confident match" - Logger itself has no capture seam, and a global
    /// one would reintroduce the shared-mutable-test-state hazard this suite is trying to avoid.</summary>
    public static ArtworkSelection? Apply(GameEntry game, string? steamGridDbApiKey,
        string? igdbClientId = null, string? igdbClientSecret = null,
        IgdbCoverArtProvider? igdbProviderOverride = null, SteamGridDbCoverArtProvider? steamGridDbProviderOverride = null,
        string? cacheDirOverride = null, CancellationToken ct = default, Action<string>? diagnosticSinkForTest = null,
        SteamCoverArtProvider? steamProviderOverride = null)
    {
        var searchName = game.CatalogName ?? game.Name;
        var isAmbiguousUmbrella = SteamGridDbCoverArtProvider.IsAmbiguousUmbrellaProduct(searchName);
        if (isAmbiguousUmbrella)
        {
            Logger.Warn($"  art: '{game.Name}' is a known multi-title umbrella product name - skipping "
                + "IGDB/SteamGridDB name search entirely (Steam CDN, if applicable, is unaffected - it never guesses by name).");
        }

        var igdbAmbiguous = false;
        if (!isAmbiguousUmbrella
            && (igdbProviderOverride is not null || (!string.IsNullOrWhiteSpace(igdbClientId) && !string.IsNullOrWhiteSpace(igdbClientSecret))))
        {
            ct.ThrowIfCancellationRequested();
            var igdb = igdbProviderOverride ?? new IgdbCoverArtProvider(igdbClientId!, igdbClientSecret!);
            var igdbArt = igdb.GetCoverArt(game, out var igdbFromCache, out var igdbMatched, out var igdbStatus, cacheDirOverride, ct);
            if (igdbArt is not null)
            {
                game.Icon = igdbArt;
                game.IsCoverArt = true;
                Logger.Info($"  art: '{game.Name}' <- IGDB" + (igdbFromCache ? " (cached)" : string.Empty));
                return new ArtworkSelection
                {
                    Provider = ArtworkProvider.Igdb,
                    RetrievedFrom = igdbFromCache ? ArtworkRetrievalMethod.LocalCache : ArtworkRetrievalMethod.NetworkDownload,
                    ProviderGameId = igdbMatched?.Id.ToString(),
                    ProviderTitle = igdbMatched?.Title,
                    MatchMethod = "ExactTitle",
                    IsUserSelected = false,
                    SelectedAt = DateTime.UtcNow,
                };
            }

            // The diagnostic AND the fallback decision both come from the explicit status - never from
            // "IGDB returned no art, therefore no confident match" (an authentication/network failure
            // used to be logged exactly that way). Only Ambiguous stops the cascade: the other three
            // leave SteamGridDB free to search independently, never seeded with anything IGDB found. A
            // future identity-persistence layer can make more of IdentifiedWithoutUsableArt (a confirmed
            // identity worth keeping even without a cover); that doesn't exist yet, so nothing is
            // silently promised here.
            igdbAmbiguous = igdbStatus == CoverLookupStatus.Ambiguous;
            var diagnostic = DescribeIgdbFallback(game.Name, igdbStatus);
            if (igdbAmbiguous || igdbStatus == CoverLookupStatus.Unavailable)
                Logger.Warn(diagnostic);
            else
                Logger.Info(diagnostic);
            diagnosticSinkForTest?.Invoke(diagnostic);
        }

        // A key entered in Settings wins; otherwise fall back to the key compiled into the build.
        var apiKey = string.IsNullOrWhiteSpace(steamGridDbApiKey)
            ? DefaultApiKey.SteamGridDb
            : steamGridDbApiKey;

        if (!isAmbiguousUmbrella && !igdbAmbiguous
            && (steamGridDbProviderOverride is not null || !string.IsNullOrWhiteSpace(apiKey)))
        {
            ct.ThrowIfCancellationRequested(); // a superseded scan must not start fallback network work
            var gridDb = steamGridDbProviderOverride ?? new SteamGridDbCoverArtProvider(apiKey!);
            var gridArt = gridDb.GetCoverArt(game, out var gridFromCache, out var matched, out var gridStatus, cacheDirOverride, ct);
            if (gridArt is null)
            {
                // Same rule as the IGDB stage: the diagnostic comes from the explicit status, never from
                // "no art, therefore no match". Neither Ambiguous nor Unavailable stops the cascade here -
                // Steam CDN (below) is keyed by the launcher's own app id and never guesses by name.
                var gridDiagnostic = DescribeSteamGridDbFallback(game.Name, gridStatus);
                if (gridStatus is CoverLookupStatus.Ambiguous or CoverLookupStatus.Unavailable)
                    Logger.Warn(gridDiagnostic);
                else
                    Logger.Info(gridDiagnostic);
                diagnosticSinkForTest?.Invoke(gridDiagnostic);
            }
            else
            {
                game.Icon = gridArt;
                game.IsCoverArt = true;
                Logger.Info($"  art: '{game.Name}' <- SteamGridDB" + (gridFromCache ? " (cached)" : string.Empty));
                return new ArtworkSelection
                {
                    Provider = ArtworkProvider.SteamGridDb,
                    RetrievedFrom = gridFromCache ? ArtworkRetrievalMethod.LocalCache : ArtworkRetrievalMethod.NetworkDownload,
                    // Null only for a cache hit against an entry written before this evidence sidecar
                    // existed (see SteamGridDbCoverArtProvider.GetCoverArt's own remarks) - a real but
                    // self-healing gap: the very next time this game's cache is invalidated (a
                    // CacheVersion bump, or the file going missing) a fresh fetch records it.
                    ProviderGameId = matched?.Id.ToString(),
                    ProviderTitle = matched?.Title,
                    // ProviderArtworkRef (which SPECIFIC grid image among the game's available covers)
                    // still isn't surfaced - GetGridImageUrl only returns the first grid's URL, not its
                    // own id. Phase 1b's Identify-Game search needs that exposed anyway (to render a
                    // candidate grid), and will close this at the same time rather than threading it
                    // through twice.
                    MatchMethod = "ExactTitle",
                    IsUserSelected = false,
                    SelectedAt = DateTime.UtcNow,
                };
            }
        }

        if (game.Source == GameSource.Steam)
        {
            ct.ThrowIfCancellationRequested(); // ...nor Steam CDN work either
            var steamArt = (steamProviderOverride ?? new SteamCoverArtProvider()).GetCoverArt(game, out var steamFromCache, cacheDirOverride, ct);
            if (steamArt is not null)
            {
                game.Icon = steamArt;
                game.IsCoverArt = true;
                Logger.Info($"  art: '{game.Name}' <- Steam CDN" + (steamFromCache ? " (cached)" : string.Empty));
                return new ArtworkSelection
                {
                    Provider = ArtworkProvider.SteamCdn,
                    // Genuine retrieval evidence, not assumed: GetCoverArt can satisfy this from its own
                    // on-disk cache without touching the network at all - claiming NetworkDownload
                    // unconditionally would manufacture provenance that isn't actually true this time.
                    RetrievedFrom = steamFromCache ? ArtworkRetrievalMethod.LocalCache : ArtworkRetrievalMethod.NetworkDownload,
                    ProviderGameId = game.Id, // "steam-{appid}" - verified by construction, see SteamScanner
                    MatchMethod = "SteamAppId",
                    IsUserSelected = false,
                    SelectedAt = DateTime.UtcNow,
                };
            }

            Logger.Warn($"  art: '{game.Name}' has no Steam CDN box art (appid {game.Id}).");
        }

        game.Icon = IconService.GetIcon(game);
        game.IsCoverArt = false;
        Logger.Info($"  art: '{game.Name}' <- exe icon fallback"
                    + (string.IsNullOrWhiteSpace(apiKey) && game.Source != GameSource.Steam
                        ? " (no SteamGridDB key available)"
                        : string.Empty));
        return null;
    }

    /// <summary>The one place an IGDB outcome becomes diagnostic text - pure, so the mapping itself is
    /// pinned directly, and shared by Apply's logging and its test sink so they can never disagree. The
    /// Unavailable text deliberately does NOT say "no confident match": that is a statement about IGDB's
    /// catalog, which an authentication/network/rate-limit failure says nothing about.</summary>
    internal static string DescribeIgdbFallback(string gameName, CoverLookupStatus status) => status switch
    {
        CoverLookupStatus.Ambiguous =>
            $"  art: '{gameName}' - IGDB's identity is ambiguous (multiple equally-confident candidates, or a "
            + "multi-title umbrella name); not letting SteamGridDB guess independently against the same name.",
        CoverLookupStatus.NoMatch =>
            $"  art: '{gameName}' - IGDB had no confident match; trying SteamGridDB.",
        CoverLookupStatus.IdentifiedWithoutUsableArt =>
            $"  art: '{gameName}' - IGDB identified the game but had no usable cover; trying SteamGridDB.",
        CoverLookupStatus.Unavailable =>
            $"  art: '{gameName}' - IGDB was unavailable (authentication, network, timeout, rate-limit, or a "
            + "malformed response - NOT a search result); trying SteamGridDB.",
        _ => $"  art: '{gameName}' - IGDB returned status {status} without art; trying SteamGridDB.",
    };

    /// <summary>The SteamGridDB counterpart of DescribeIgdbFallback - same purpose, same rule: a diagnostic
    /// is chosen from the explicit status, so an outage is never worded as a catalog non-match and an
    /// ambiguous identity is never worded as "no art". Neither stops the cascade: Steam CDN (Steam games
    /// only) is keyed by app id, and the exe icon is the last resort.</summary>
    internal static string DescribeSteamGridDbFallback(string gameName, CoverLookupStatus status) => status switch
    {
        CoverLookupStatus.Ambiguous =>
            $"  art: '{gameName}' - SteamGridDB's identity is ambiguous (multiple equally-confident candidates, or "
            + "a multi-title umbrella name); not guessing. Steam CDN (Steam games only, keyed by app id) and the exe icon remain.",
        CoverLookupStatus.NoMatch =>
            $"  art: '{gameName}' - SteamGridDB had no confident match; falling back to Steam CDN (Steam games) or the exe icon.",
        CoverLookupStatus.IdentifiedWithoutUsableArt =>
            $"  art: '{gameName}' - SteamGridDB identified the game but had no usable cover; falling back to Steam CDN "
            + "(Steam games) or the exe icon.",
        CoverLookupStatus.Unavailable =>
            $"  art: '{gameName}' - SteamGridDB was unavailable (authentication, network, timeout, or a malformed "
            + "response - NOT a search result); falling back to Steam CDN (Steam games) or the exe icon.",
        _ => $"  art: '{gameName}' - SteamGridDB returned status {status} without art; falling back.",
    };

    /// <summary>Loads a previously-selected image's bytes from ArtworkAssetStore and decodes them,
    /// without touching any GameEntry - safe to call from a background thread (the decoded BitmapImage
    /// is frozen by CoverArtDecoder.Decode before it's ever returned). Null for anything short of a
    /// fully successful read+decode. `storeDirOverride`: see ArtworkAssetStore.TryResolvePath.</summary>
    public static BitmapImage? TryDecodeStored(ArtworkSelection selection, string? storeDirOverride = null)
    {
        var bytes = ArtworkAssetStore.TryRead(selection.AssetId, selection.AssetExtension, storeDirOverride);
        return bytes is null ? null : CoverArtDecoder.Decode(bytes);
    }

    /// <summary>Loads a previously-selected image (from ArtworkAssetStore) and applies it to
    /// game.Icon/IsCoverArt. Returns false - never throws - if the stored asset is missing, fails path
    /// validation, or fails to decode: callers treat that as "the selection is retained, but its image
    /// can't currently be shown," never as license to silently pick different artwork. See
    /// ApplyStoredSafely for the crash-isolated entry point real callers should use.</summary>
    public static bool ApplyStored(GameEntry game, ArtworkSelection selection, string? storeDirOverride = null)
    {
        var decoded = TryDecodeStored(selection, storeDirOverride);
        if (decoded is null)
            return false;

        game.Icon = decoded;
        game.IsCoverArt = true;
        return true;
    }

    /// <summary>Never throws - the shared crash-isolation entry point for loading a stored selection,
    /// used by both the background scan (GameScannerService.SafeApplyCoverArt) and the UI-thread
    /// reconciliation loop (LibraryViewModel.ApplyScanResultAsync), neither of which can let one game's
    /// corrupt asset abort processing every other game. Mirrors SafeApplyCoverArt's own nested icon-
    /// fallback isolation exactly: if BOTH loading the stored artwork AND the exe-icon fallback fail,
    /// Icon ends up null and IsCoverArt false, and the UI's built-in placeholder covers the rest.
    /// `storeDirOverride`: see ArtworkAssetStore.TryResolvePath.</summary>
    public static void ApplyStoredSafely(GameEntry game, ArtworkSelection selection,
        Func<GameEntry, BitmapImage?>? getIcon = null, string? storeDirOverride = null)
    {
        try
        {
            if (ApplyStored(game, selection, storeDirOverride))
                return;

            Logger.Warn($"'{game.Name}': selected cover art is missing or corrupt - showing the exe icon; the selection itself is kept.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"'{game.Name}': loading the selected cover art failed unexpectedly - falling back to the exe icon.", ex);
        }

        ApplyIconFallbackSafely(game, getIcon);
    }

    /// <summary>The shared, never-throws exe-icon fallback - used everywhere a caller needs to fall back
    /// to the exe icon without risking a second, unrelated failure (a bad icon extraction) escaping and
    /// aborting whatever loop or commit called it. Every direct IconService.GetIcon call outside of this
    /// file that could run mid-scan-publish or mid-commit should go through this instead.</summary>
    public static void ApplyIconFallbackSafely(GameEntry game, Func<GameEntry, BitmapImage?>? getIcon = null)
    {
        getIcon ??= IconService.GetIcon;

        try
        {
            game.Icon = getIcon(game);
        }
        catch (Exception iconEx)
        {
            Logger.Warn($"'{game.Name}': exe icon fallback failed unexpectedly - showing the built-in placeholder instead.", iconEx);
            game.Icon = null;
        }

        game.IsCoverArt = false;
    }
}
