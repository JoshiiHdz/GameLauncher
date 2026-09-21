using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>Image fixtures shared by the provider tests. Nothing here touches disk or the network.</summary>
internal static class TestImages
{
    /// <summary>A real, decodable PNG of exactly `width` x `height`. Gray8 with all-zero pixels compresses to
    /// almost nothing, so an over-limit fixture such as 9000x100 stays a few KB while still reporting its
    /// true original dimensions to the validator.</summary>
    public static byte[] Png(int width = 60, int height = 90)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A valid single-image ICO with a classic 32-bit DIB payload. Its decoder is IconBitmapDecoder -
    /// a genuinely decodable container OUTSIDE the five formats ArtworkAssetStore can stage, so it
    /// distinguishes the strict (asset-store) validation policy from the provider one without needing a WebP
    /// codec, which is not guaranteed to be installed. (A PNG-compressed ICO payload was tried first and WPF's
    /// decoder rejected it; the DIB form is the universally supported one.)</summary>
    public static byte[] Icon(int size = 16)
    {
        var maskRowBytes = ((size + 31) / 32) * 4;                 // 1bpp AND mask, rows padded to 32 bits
        var pixelBytes = size * size * 4;                          // 32bpp BGRA
        var maskBytes = maskRowBytes * size;
        var dibSize = 40 + pixelBytes + maskBytes;

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0);          // ICONDIR: reserved
        w.Write((ushort)1);          //          type: icon
        w.Write((ushort)1);          //          image count
        w.Write((byte)size);         // ICONDIRENTRY: width
        w.Write((byte)size);         //               height
        w.Write((byte)0);            //               palette colors
        w.Write((byte)0);            //               reserved
        w.Write((ushort)1);          //               planes
        w.Write((ushort)32);         //               bits per pixel
        w.Write((uint)dibSize);      //               payload size
        w.Write((uint)22);           //               payload offset (6-byte header + 16-byte entry)

        w.Write((uint)40);           // BITMAPINFOHEADER: size
        w.Write((int)size);          //                   width
        w.Write((int)(size * 2));    //                   height (XOR bitmap + AND mask, so doubled)
        w.Write((ushort)1);          //                   planes
        w.Write((ushort)32);         //                   bit count
        w.Write((uint)0);            //                   compression (BI_RGB)
        w.Write((uint)0);            //                   image size (may be 0 for BI_RGB)
        w.Write((int)0);             //                   x pixels/meter
        w.Write((int)0);             //                   y pixels/meter
        w.Write((uint)0);            //                   colors used
        w.Write((uint)0);            //                   colors important
        for (var i = 0; i < size * size; i++)
            w.Write(0xFF336699u);    // opaque BGRA pixel
        w.Write(new byte[maskBytes]);
        return stream.ToArray();
    }
}
