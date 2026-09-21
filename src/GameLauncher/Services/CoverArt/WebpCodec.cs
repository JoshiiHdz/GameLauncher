using System.IO;
using System.Windows.Media.Imaging;

namespace GameLauncher.Services.CoverArt;

/// <summary>Whether this machine can decode WebP - and how to tell "the codec is missing" from "the file is
/// damaged". SteamGridDB can serve WebP grids, but WPF has no built-in WebP decoder: it works only through
/// Windows' WIC codec (the Microsoft "WebP Image Extensions", installed by default on many Windows 10/11
/// builds but not guaranteed). Where it is absent a WebP cover cannot be shown, and that must read as an
/// UNSUPPORTED CODEC, not as a corrupt image - the cover's own bytes are fine.
///
/// The probe decodes a real 1x1 lossless WebP once per process (cached); it never throws.</summary>
internal static class WebpCodec
{
    // A real, valid 1x1 lossless WebP (36 bytes). Decoded once, to learn whether a WebP decoder is registered.
    private const string ProbeBase64 = "UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==";

    private static readonly Lazy<bool> Installed = new(Probe);

    /// <summary>True when a WebP decoder is registered with WIC on this machine.</summary>
    internal static bool IsDecoderInstalled => Installed.Value;

    /// <summary>Whether `bytes` are a WebP container: "RIFF" + a 4-byte size + "WEBP".</summary>
    internal static bool IsWebpContainer(byte[] bytes) =>
        bytes.Length >= 12
        && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
        && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    private static bool Probe()
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(ProbeBase64));
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth == 1 && decoder.Frames[0].PixelHeight == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
