using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services;

/// <summary>Wide, high-resolution background art for the console themes' home screens. A game's cover is a 600 x 900 portrait; stretched across a whole
/// window it is soft and washed out. Steam publishes a real widescreen "library hero" for most games (up to 3840 x 1240) on the same public CDN as the
/// cover, keyed by app id, so Steam games get that; any other game keeps its cover as the background. Images are cached under the app's data folder, a
/// missing one is remembered so it is not asked for again, and nothing here needs a key or sends anything about the user.</summary>
public static class BackdropArtService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();

    /// <summary>The widest the decoded background is kept (the file on disk keeps its full size): the hero is 3840 x 1240, so a 1080p or 1440p window shows it at its own scale, and decoded whole it is about 19 MB.</summary>
    private const int DecodeWidth = 3840;

    /// <summary>How many games keep their decoded background at once. Each is up to about 19 MB, so hovering through a big library must not keep every one
    /// pinned; the oldest are let go (the file stays on disk, so coming back to one only costs a quick decode).</summary>
    internal const int KeepBackdrops = 4;

    private static readonly LinkedList<GameEntry> Held = new();

    /// <summary>Notes that a game's background is in use now and lets go of the ones used longest ago. UI thread only.</summary>
    internal static void Hold(GameEntry game)
    {
        Held.Remove(game);
        Held.AddLast(game);
        while (Held.Count > KeepBackdrops)
        {
            var oldest = Held.First!.Value;
            Held.RemoveFirst();
            oldest.Backdrop = null;
        }
    }

    /// <summary>How long "this game has no hero art" is remembered before asking again.</summary>
    private static readonly TimeSpan MissingFor = TimeSpan.FromDays(14);

    internal static string CacheDir => Path.Combine(AppPaths.DataDir, "BackdropCache");

    /// <summary>Test seam: returns the image bytes the CDN would, or null for "none". Production never sets this.</summary>
    internal static Func<string, byte[]?>? FetchOverrideForTest { get; set; }

    internal static string? SteamAppId(GameEntry game)
    {
        const string prefix = "steam-";
        return game.Source == GameSource.Steam && game.Id.StartsWith(prefix, StringComparison.Ordinal) ? game.Id[prefix.Length..] : null;
    }

    /// <summary>Gives the game its wide background when it has one to get (and does not have yet). Safe to call often - it returns at once, works in the
    /// background, and sets <see cref="GameEntry.Backdrop"/> on the UI thread when the image is ready.</summary>
    public static void Request(GameEntry? game)
    {
        if (game is null)
            return;

        if (game.Backdrop is not null)
        {
            Hold(game); // already loaded: it is the one in use, so it is the last to be let go
            return;
        }

        if (SteamAppId(game) is not { } appId || !InFlight.TryAdd(appId, 0))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var image = Load(appId, CacheDir);
                if (image is not null)
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        game.Backdrop = image;
                        Hold(game);
                    });
            }
            catch (Exception ex)
            {
                Logger.Warn($"  backdrop: couldn't get background art for '{game.Name}'.", ex);
            }
            finally
            {
                InFlight.TryRemove(appId, out _);
            }
        });
    }

    /// <summary>The hero image for a Steam app: from the cache, else downloaded and cached. Null when there is none (or it could not be fetched now).</summary>
    internal static BitmapImage? Load(string appId, string cacheDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var imagePath = Path.Combine(cacheDir, $"steam-{appId}-hero.jpg");
        var missingPath = Path.Combine(cacheDir, $"steam-{appId}-hero.none");

        if (File.Exists(imagePath))
        {
            var cached = Decode(ReadBounded(imagePath));
            if (cached is not null)
                return cached;

            File.Delete(imagePath); // damaged: fetch it again
        }

        if (File.Exists(missingPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missingPath) < MissingFor)
            return null;

        byte[]? bytes;
        try
        {
            bytes = FetchOverrideForTest is { } seam
                ? seam(appId)
                : ProviderImageIo.FetchBoundedImageBytes(Http, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_hero.jpg", "Steam CDN hero",
                    TimeSpan.FromSeconds(12), ct);
        }
        catch (HttpRequestException)
        {
            return null; // the network, not the game: try again next time
        }

        if (bytes is null)
        {
            File.WriteAllBytes(missingPath, []); // Steam has no hero for this app
            return null;
        }

        var image = Decode(bytes);
        if (image is null)
            return null;

        File.WriteAllBytes(imagePath, bytes);
        return image;
    }

    private static byte[]? ReadBounded(string path)
    {
        var info = new FileInfo(path);
        return info.Length is > 0 and <= 16 * 1024 * 1024 ? File.ReadAllBytes(path) : null;
    }

    /// <summary>A frozen bitmap (usable from any thread), decoded no wider than <see cref="DecodeWidth"/>; null when the bytes are not a usable image.</summary>
    internal static BitmapImage? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            // Read the size first: DecodePixelWidth would also stretch a smaller image up to that width.
            int sourceWidth;
            using (var probe = new MemoryStream(bytes))
                sourceWidth = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (sourceWidth > DecodeWidth)
                image.DecodePixelWidth = DecodeWidth;

            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image.PixelWidth >= 400 && image.PixelHeight >= 200 ? image : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException or IOException)
        {
            return null;
        }
    }
}
