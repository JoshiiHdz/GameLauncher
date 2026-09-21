using System.IO;
using System.Reflection;

namespace GameLauncher.Tests.Services.CoverArt;

/// <summary>The real WebP files embedded from Fixtures\WebP (see generate_fixtures.py there), by file name.</summary>
internal static class WebpFixtures
{
    internal static byte[] Load(string fileName)
    {
        var name = "Fixtures.WebP." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded WebP fixture '{name}' not found - was generate_fixtures.py's output added to the project?");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal const string Lossy = "cover-600x900-lossy.webp";
    internal const string Lossless = "cover-600x900-lossless.webp";
    internal const string Alpha = "cover-600x900-alpha-lossless.webp";
    internal const string Tiny = "tiny-1x1-lossless.webp";
    internal const string TooWide = "too-wide-9000x100.webp";
    internal const string TooTall = "too-tall-100x9000.webp";
    internal const string AtLimit = "at-limit-8000x50.webp";
    internal const string Animated = "animated-2-frames-120x180.webp";
    internal const string Truncated = "truncated-lossy.webp";
    internal const string HeaderOnly = "riff-header-only.webp";
}
