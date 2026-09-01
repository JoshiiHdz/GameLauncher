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
    public static void Apply(GameEntry game, AppSettings settings)
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

        if (!string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey))
        {
            var gridArt = new SteamGridDbCoverArtProvider(settings.SteamGridDbApiKey).GetCoverArt(game);
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
                    + (string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey) && game.Source != GameSource.Steam
                        ? " (no SteamGridDB key set)"
                        : string.Empty));
    }
}
