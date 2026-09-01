using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

/// <summary>
/// Picks the best available art for a game: SteamGridDB (if an API key is configured) for
/// non-Steam sources, falling back to the exe icon everywhere else. Today, with no key configured,
/// this always resolves to the icon - the SteamGridDB path is wired but dormant until a key is added
/// in Settings.
/// </summary>
public static class CoverArtService
{
    public static BitmapImage? GetCoverArt(GameEntry game, AppSettings settings)
    {
        if (game.Source != GameSource.Steam && !string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey))
        {
            var art = new SteamGridDbCoverArtProvider(settings.SteamGridDbApiKey).GetCoverArt(game);
            if (art is not null)
                return art;
        }

        return IconService.GetIcon(game);
    }
}
