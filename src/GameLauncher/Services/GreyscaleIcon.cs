using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services;

/// <summary>
/// Builds a desaturated, slightly dimmed copy of an icon for the collapsed sidebar's "this launcher is
/// switched off" state. Done per pixel on a Pbgra32 copy so transparency survives - converting to a
/// Gray pixel format would drop alpha and turn the icon's transparent corners into a solid square.
/// </summary>
public static class GreyscaleIcon
{
    public static BitmapSource? From(BitmapSource? source)
    {
        if (source is null)
            return null;

        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                // Max-channel alone turns a saturated blue EA backplate and white lettering into
                // the same grey. Luma alone flattens Xbox's green/grey mark. Blend the two to keep
                // both kinds of contrast; premultiplied channels retain transparent edges.
                double max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                double luma = 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
                double intensity = (0.5 * max + 0.5 * luma) * 0.85;
                var grey = (byte)Math.Min(255, intensity);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
            }

            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
