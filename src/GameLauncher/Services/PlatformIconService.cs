using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>
/// Resolves the icon for each game source by extracting it from that launcher's own executable on
/// this machine - so the badge shows the real launcher mark without the app shipping any brand
/// artwork. Returns null when the launcher isn't installed, the source has no conventional launcher
/// exe to extract from (manual folders), or its icon can never be read by path regardless of PC
/// (Xbox's MSIX package) - the UI falls back to a generic glyph in all three cases.
/// Resolved at most once per source per run.
/// </summary>
public static class PlatformIconService
{
    private static readonly Dictionary<GameSource, BitmapImage?> Cache = new();
    private static readonly object Lock = new();

    public static BitmapImage? GetIcon(GameSource source)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(source, out var cached))
                return cached;

            var icon = Resolve(source);
            Cache[source] = icon;

            if (icon is null && source != GameSource.Manual)
                Logger.Info($"No launcher icon found for {source} (launcher not installed?).");

            return icon;
        }
    }

    private static BitmapImage? Resolve(GameSource source)
    {
        var exePath = source switch
        {
            GameSource.Steam => FindSteamExe(),
            GameSource.Epic => FindEpicExe(),
            GameSource.Gog => FindGogExe(),
            GameSource.Ea => FindEaExe(),
            GameSource.Ubisoft => FindUbisoftExe(),
            GameSource.BattleNet => FindBattleNetExe(),
            GameSource.Rockstar => FindRockstarExe(),
            GameSource.AmazonGames => FindAmazonGamesExe(),
            // Xbox: the Xbox app is an MSIX package under WindowsApps, which is ACL-locked, so its
            // icon can't be read by path. Those fall back to the generic badge.
            _ => null,
        };

        return exePath is null ? null : ExtractIcon(exePath);
    }

    private static string? FindSteamExe()
    {
        var steamPath = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

        if (steamPath is null)
            return null;

        var exe = Path.Combine(steamPath.Replace('/', '\\'), "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string? FindEpicExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindGogExe()
    {
        var clientPath = ReadRegistryString(Registry.LocalMachine,
                             @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client")
                         ?? ReadRegistryString(Registry.LocalMachine,
                             @"SOFTWARE\GOG.com\GalaxyClient\paths", "client");

        if (clientPath is null)
            return null;

        var exe = Path.Combine(clientPath, "GalaxyClient.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string? FindEaExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Origin", "Origin.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindUbisoftExe()
    {
        var installDir = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir");
        if (installDir is null)
            return null;

        var candidates = new[] { "UbisoftConnect.exe", "upc.exe" }
            .Select(exe => Path.Combine(installDir, exe));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Unverified exact registry key/exe name - falls back to the conventional install path.</summary>
    private static string? FindBattleNetExe()
    {
        var installDir = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Battle.net", "InstallPath");
        if (installDir is not null)
        {
            var exe = Path.Combine(installDir, "Battle.net.exe");
            if (File.Exists(exe))
                return exe;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Battle.net", "Battle.net.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static string? FindRockstarExe()
    {
        var installDir = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Rockstar Games\Launcher", "InstallFolder");
        if (installDir is null)
            return null;

        var exe = Path.Combine(installDir, "Launcher.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Unverified exact path - Amazon Games has no documented registry key for this, only
    /// the conventional per-user install location.</summary>
    private static string? FindAmazonGamesExe()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon Games", "App", "Amazon Games.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string? ReadRegistryString(RegistryKey root, string keyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapImage? ExtractIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
                return null;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't extract a launcher icon from '{exePath}'.", ex);
            return null;
        }
    }
}
