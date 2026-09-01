using System.IO;
using System.Reflection;

namespace GameLauncher.Services;

/// <summary>
/// A SteamGridDB key compiled into the build, so a fresh install shows real cover art for non-Steam
/// games immediately with no setup. Supplied by an embedded "default-api-key.txt" that is gitignored,
/// so the key lives in the built exe rather than in source control. Builds without that file simply
/// have no default, and anything entered in Settings always takes precedence.
/// </summary>
public static class DefaultApiKey
{
    public static string? SteamGridDb { get; } = Load();

    private static string? Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("default-api-key.txt", StringComparison.OrdinalIgnoreCase));

            if (name is null)
                return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            var key = reader.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or NotSupportedException)
        {
            Logger.Warn("Couldn't read the built-in SteamGridDB key.", ex);
            return null;
        }
    }
}
