using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services.CoverArt;

/// <summary>A source of box-art images for a game. Implementations should return null (never throw)
/// when they can't find art, so CoverArtService can fall back to the next provider.</summary>
public interface ICoverArtProvider
{
    BitmapImage? GetCoverArt(GameEntry game);
}
