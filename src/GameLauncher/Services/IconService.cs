using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services;

public static class IconService
{
    private static readonly string CacheDir = Path.Combine(AppPaths.DataDir, "IconCache");

    public static BitmapImage? GetIcon(GameEntry game)
    {
        var cachePath = ExtractIconToCache(game);
        return LoadBitmap(cachePath);
    }

    private static string? ExtractIconToCache(GameEntry game)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            // Keying purely by game.Id (stable across an exe-repick, by design - see GameOverride)
            // meant a fixed scanner heuristic that starts pointing at a different exe would still
            // load the old exe's stale cached icon forever, since the old cache file was never
            // invalidated. Folding a short hash of the actual resolved ExecutablePath into the
            // filename means a changed exe naturally lands on a new cache file instead; the old one
            // is just an orphan on disk (a handful of small PNGs - not worth cleaning up).
            var pathHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(game.ExecutablePath.ToLowerInvariant())))[..8];
            var cachePath = Path.Combine(CacheDir, $"{game.Id}-{pathHash}.png");
            if (File.Exists(cachePath))
                return cachePath;

            var sourcePath = ResolveExecutableForIcon(game);
            if (sourcePath is null)
                return null;

            using var icon = Icon.ExtractAssociatedIcon(sourcePath);
            if (icon is null)
                return null;

            using var bitmap = icon.ToBitmap();
            bitmap.Save(cachePath, System.Drawing.Imaging.ImageFormat.Png);
            return cachePath;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Logger.Warn($"Couldn't extract an icon for '{game.Name}'.", ex);
            return null;
        }
    }

    private static string? ResolveExecutableForIcon(GameEntry game)
    {
        if (File.Exists(game.ExecutablePath))
            return game.ExecutablePath;

        if (Directory.Exists(game.ExecutablePath))
        {
            return Directory.EnumerateFiles(game.ExecutablePath, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        return null;
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return null;
        }
    }
}
