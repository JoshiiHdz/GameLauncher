using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services;

/// <summary>
/// Samples a cover image down to a handful of pixels and averages them into one soft accent colour,
/// for the library hero's ambient background tint (see LibraryViewModel.HeroAccentColor). Deliberately
/// approximate - this is mood lighting, not colour-accurate reproduction, so a cheap downscale-and-
/// average is enough; nothing here needs to identify the image's actual "dominant" cluster the way a
/// real palette-extraction algorithm would.
/// </summary>
public static class DominantColorExtractor
{
    public static Color? Extract(BitmapSource? source)
    {
        if (source is null)
            return null;

        try
        {
            // PixelWidth/Height themselves can throw - a BitmapImage that was constructed but never
            // had BeginInit/EndInit called (a bare placeholder, as some tests use for Icon) throws
            // InvalidOperationException just from reading them, before any of the actual sampling work
            // below even starts. The guard has to live INSIDE the try for that reason.
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
                return null;

            const int sampleSize = 12;
            var scaled = new TransformedBitmap(source,
                new ScaleTransform((double)sampleSize / source.PixelWidth, (double)sampleSize / source.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
                return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            long r = 0, g = 0, b = 0;
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                // Transparent padding (a letterboxed cover that doesn't fill its tile) shouldn't dilute
                // the average toward whatever the letterbox background happens to be.
                if (pixels[i + 3] < 32)
                    continue;

                b += pixels[i];
                g += pixels[i + 1];
                r += pixels[i + 2];
                count++;
            }

            if (count == 0)
                return null;

            return BoostForAmbientUse(Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or OverflowException or InvalidOperationException)
        {
            return null; // a cosmetic effect must never be able to take the app down
        }
    }

    /// <summary>A raw pixel average skews toward whatever colour covers the most AREA, which for real
    /// box art is often a large dark background or a white logo field - tinting the hero with that
    /// average directly would read as near-black or near-white, not as "this game's colour." Converting
    /// to HSL and clamping saturation/lightness into a usable band keeps the hue (the part that actually
    /// looks tied to the game) while guaranteeing the tint itself stays visible.</summary>
    private static Color BoostForAmbientUse(Color c)
    {
        var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
        s = Math.Clamp(s * 1.6, 0.35, 0.75);
        l = Math.Clamp(l, 0.30, 0.55);
        return HslToRgb(h, s, l);
    }

    private static (double H, double S, double L) RgbToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;

        if (max == min)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;

        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var r = HueToRgb(p, q, h + 1.0 / 3);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1.0 / 3);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
