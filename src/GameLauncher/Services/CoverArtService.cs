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
    /// <summary>steamGridDbApiKey is a snapshot of AppSettings.SteamGridDbApiKey taken by the caller,
    /// not a live AppSettings reference - GameScannerService runs this from a background thread, and
    /// AppSettings is a mutable object the UI thread can be editing at the same moment (Settings
    /// window key entry, among others). Taking just the one string value it actually needs avoids
    /// retaining any live, shared-mutable-state reference on the worker thread.</summary>
    public static void Apply(GameEntry game, string? steamGridDbApiKey)
    {
        if (game.Source == GameSource.Steam)
        {
            var steamArt = new SteamCoverArtProvider().GetCoverArt(game);
            if (steamArt is not null)
            {
                game.Icon = steamArt;
                game.IsCoverArt = true;
                Logger.Info($"  art: '{game.Name}' <- Steam CDN");
                return;
            }

            Logger.Warn($"  art: '{game.Name}' has no Steam CDN box art (appid {game.Id}).");
        }

        // A key entered in Settings wins; otherwise fall back to the key compiled into the build.
        var apiKey = string.IsNullOrWhiteSpace(steamGridDbApiKey)
            ? DefaultApiKey.SteamGridDb
            : steamGridDbApiKey;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var gridArt = new SteamGridDbCoverArtProvider(apiKey).GetCoverArt(game);
            if (gridArt is not null)
            {
                game.Icon = gridArt;
                game.IsCoverArt = true;
                Logger.Info($"  art: '{game.Name}' <- SteamGridDB");
                return;
            }
        }

        game.Icon = IconService.GetIcon(game);
        game.IsCoverArt = false;
        Logger.Info($"  art: '{game.Name}' <- exe icon fallback"
                    + (string.IsNullOrWhiteSpace(apiKey) && game.Source != GameSource.Steam
                        ? " (no SteamGridDB key available)"
                        : string.Empty));
    }
}
