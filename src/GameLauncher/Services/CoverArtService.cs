using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

/// <summary>
/// Picks the best available art for a game: Steam's free public CDN for Steam games (no key needed,
/// always an exact match by app ID), then SteamGridDB for everything else if a key is configured,
/// falling back to the exe icon when neither has art.
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
    /// steamGridDbApiKey is a snapshot of AppSettings.SteamGridDbApiKey taken by the caller, not a live
    /// AppSettings reference - GameScannerService runs this from a background thread, and AppSettings is
    /// a mutable object the UI thread can be editing at the same moment (Settings window key entry,
    /// among others). Taking just the one string value it actually needs avoids retaining any live,
    /// shared-mutable-state reference on the worker thread.</summary>
    public static ArtworkSelection? Apply(GameEntry game, string? steamGridDbApiKey)
    {
        if (game.Source == GameSource.Steam)
        {
            var steamArt = new SteamCoverArtProvider().GetCoverArt(game, out var steamFromCache);
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

        // A key entered in Settings wins; otherwise fall back to the key compiled into the build.
        var apiKey = string.IsNullOrWhiteSpace(steamGridDbApiKey)
            ? DefaultApiKey.SteamGridDb
            : steamGridDbApiKey;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var gridArt = new SteamGridDbCoverArtProvider(apiKey).GetCoverArt(game, out var gridFromCache, out var matched);
            if (gridArt is not null)
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

        game.Icon = IconService.GetIcon(game);
        game.IsCoverArt = false;
        Logger.Info($"  art: '{game.Name}' <- exe icon fallback"
                    + (string.IsNullOrWhiteSpace(apiKey) && game.Source != GameSource.Steam
                        ? " (no SteamGridDB key available)"
                        : string.Empty));
        return null;
    }

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
