using System.IO;

namespace GameLauncher.Services;

/// <summary>
/// Resolves where settings/cache live. Prefers a "Data" folder next to the exe so the whole
/// app is xcopy-portable (USB stick, any folder); falls back to %AppData% if that location
/// isn't writable (e.g. running from Program Files without elevation).
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    private static string ResolveDataDir()
    {
        var portableDir = Path.Combine(AppContext.BaseDirectory, "Data");
        try
        {
            Directory.CreateDirectory(portableDir);
            return portableDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLauncher");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
