using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>Null on anything but a clean decode. Cover art bytes come from a network response or
    /// a locally-cached file written by a previous run - either can be truncated or corrupt (an
    /// interrupted download, a write cut short by a crash), and WPF's imaging stack throws a mix of
    /// exception types for that (not just IOException) that callers weren't catching, crashing the
    /// whole scan instead of just this one game falling back to its exe icon.</summary>
    public static BitmapImage? Decode(byte[] bytes)
    {
        try
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
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                                        or OverflowException or COMException)
        {
            Logger.Warn("Cover art image data was corrupt or an unsupported format.", ex);
            return null;
        }
    }
}
