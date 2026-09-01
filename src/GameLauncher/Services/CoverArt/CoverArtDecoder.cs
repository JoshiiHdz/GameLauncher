using System.IO;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services.CoverArt;

public static class CoverArtDecoder
{
    /// <summary>
    /// Cover cards are ~124 DIP wide, so decoding source art at full resolution (SteamGridDB and
    /// Steam serve well above that) burns megabytes of pixel data per game for no visible gain -
    /// memory grew with library size until this. DecodePixelWidth makes WPF decode straight to
    /// display size. 320px leaves headroom for high-DPI displays without paying for the original.
    /// </summary>
    private const int DecodeWidth = 320;

    public static BitmapImage Decode(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = DecodeWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
