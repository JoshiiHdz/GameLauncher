using System.IO;

namespace GameLauncher.Services;

/// <summary>
/// Resolves where settings/cache live.
///
/// Defaults to %AppData%\GameLauncher so settings survive replacing the exe. Keeping them beside
/// the exe meant downloading a new build into a different folder silently lost everything - API
/// key, watched folders, favourites - which is exactly what happened in practice.
///
/// Portable mode is still available: drop a file named "portable.txt" next to the exe (or keep an
/// existing "Data" folder there) and everything stays alongside the exe as before. An existing
/// beside-the-exe Data folder is also migrated once into %AppData% so upgrades carry settings over.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static bool IsPortable { get; private set; }

    private static string ResolveDataDir()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "Data");
        var portableMarker = Path.Combine(AppContext.BaseDirectory, "portable.txt");

        // Explicit portable mode, or an existing portable install - leave it exactly where it is.
        if (File.Exists(portableMarker) || Directory.Exists(besideExe))
        {
            try
            {
                Directory.CreateDirectory(besideExe);
                IsPortable = true;
                return besideExe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not writable (e.g. Program Files) - fall through to the roaming location.
            }
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameLauncher");

        try
        {
            Directory.CreateDirectory(roaming);
            MigrateFromBesideExe(besideExe, roaming);
            return roaming;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(besideExe);
            IsPortable = true;
            return besideExe;
        }
    }

    /// <summary>Carries an older beside-the-exe install over to the roaming location, once.</summary>
    private static void MigrateFromBesideExe(string besideExe, string roaming)
    {
        var oldSettings = Path.Combine(besideExe, "settings.json");
        var newSettings = Path.Combine(roaming, "settings.json");

        if (!File.Exists(oldSettings) || File.Exists(newSettings))
            return;

        try
        {
            File.Copy(oldSettings, newSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
