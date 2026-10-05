using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Cover art that a game keeps in its own install folder. Fortnite's current art is the splash screen it ships with
/// (<c>...\Fortnite\Content\Resources\SplashScreen.png</c>) and every update replaces it, so reading the file gives the cover
/// that matches the installed version, offline, with no catalog guessing. Only games listed here use it - other Xbox-installed
/// games ship a splash screen too, but for most of them a catalog's portrait box art is the better cover.
///
/// It is applied on top of the automatic cover after each scan and is never saved as a choice: a cover the user picked is
/// always left alone, and if the file disappears the automatic cover simply shows again.
/// </summary>
public static class InstallFolderArt
{
    /// <summary>The splash is landscape; it is decoded at a bounded width, then set on a portrait plate (below).</summary>
    private const int DecodeWidth = 900;

    /// <summary>The card's frame is portrait (2:3). A landscape splash filling that frame is cropped to its middle third and looks far
    /// too big, so the whole image is fitted to the card's width on a plate of this shape instead.</summary>
    private const int PlateWidth = 400;
    private const int PlateHeight = 600;

    private static readonly string[] RelativeFiles =
    [
        Path.Combine("Content", "Resources", "SplashScreen.png"),
        Path.Combine("Resources", "SplashScreen.png"),
    ];

    /// <summary>True for the games whose own install folder is the better source of their cover.</summary>
    internal static bool UsesInstallFolderArt(GameEntry game) =>
        game.DetectedTitle.Equals("Fortnite", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(game.ExecutablePath ?? "").StartsWith("FortniteClient", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(game.ExecutablePath ?? "").StartsWith("FortniteLauncher", StringComparison.OrdinalIgnoreCase);

    /// <summary>The art file for this game, or null. The install folder may be the package root (<c>X:\XboxGames\Fortnite</c>), its
    /// <c>Content</c> folder, or the exe may sit deeper, so every folder from the exe's up to the install folder's parent is tried.</summary>
    internal static string? FindFile(GameEntry game, Func<string, bool>? exists = null)
    {
        if (!UsesInstallFolderArt(game))
            return null;

        exists ??= File.Exists;
        foreach (var folder in CandidateFolders(game))
        {
            foreach (var relative in RelativeFiles)
            {
                var path = Path.Combine(folder, relative);
                if (exists(path))
                    return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateFolders(GameEntry game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Up(string? start, int levels)
        {
            if (string.IsNullOrWhiteSpace(start))
                yield break;

            string? current = start;
            for (var i = 0; i <= levels && !string.IsNullOrEmpty(current); i++)
            {
                yield return current;
                current = Path.GetDirectoryName(current);
            }
        }

        foreach (var folder in Up(game.InstallDir, 1).Concat(Up(Path.GetDirectoryName(game.ExecutablePath), 6)))
        {
            if (seen.Add(folder))
                yield return folder;
        }
    }

    /// <summary>Decodes the image at a bounded size and freezes it. Null when it is unreadable - never throws.</summary>
    internal static BitmapImage? Load(string path)
    {
        try
        {
            // Read through a stream we close ourselves: the file is never left open (an update can replace it), even when it is not an image.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image.PixelHeight > image.PixelWidth ? image : OnPortraitPlate(image); // portrait art already suits the card
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                        or FileFormatException or UriFormatException)
        {
            Logger.Warn($"Couldn't read the install-folder cover '{path}'.", ex);
            return null;
        }
    }

    /// <summary>Sets landscape art on a portrait plate: the whole image, fitted to the plate's width and centred, over a dimmed copy of
    /// itself filling the plate - so the bands above and below carry the art's own colours instead of being empty black.</summary>
    internal static BitmapImage OnPortraitPlate(BitmapSource art)
    {
        var plate = new Rect(0, 0, PlateWidth, PlateHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x07, 0x09, 0x10)), null, plate);

            var fill = Math.Max((double)PlateWidth / art.PixelWidth, (double)PlateHeight / art.PixelHeight);
            var (fillWidth, fillHeight) = (art.PixelWidth * fill, art.PixelHeight * fill);
            context.PushOpacity(0.3);
            context.DrawImage(art, new Rect((PlateWidth - fillWidth) / 2, (PlateHeight - fillHeight) / 2, fillWidth, fillHeight));
            context.Pop();

            var fitHeight = art.PixelHeight * ((double)PlateWidth / art.PixelWidth);
            context.DrawImage(art, new Rect(0, (PlateHeight - fitHeight) / 2, PlateWidth, fitHeight));
        }

        var rendered = new RenderTargetBitmap(PlateWidth, PlateHeight, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);

        // Icon is a BitmapImage, so the composed pixels go through an in-memory PNG.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Puts each listed game's own cover on it, unless the user chose a cover for that game. Returns how many were changed.</summary>
    public static int Apply(IEnumerable<GameEntry> games, Func<GameEntry, bool> userPickedCover, Func<GameEntry, string?>? findFile = null)
    {
        findFile ??= g => FindFile(g);
        var changed = 0;
        foreach (var game in games)
        {
            if (!UsesInstallFolderArt(game) || userPickedCover(game))
                continue;

            var path = findFile(game);
            if (path is null)
                continue;

            if (Load(path) is not { } image)
                continue;

            game.Icon = image;
            game.IsCoverArt = true;
            changed++;
            Logger.Info($"Cover: '{game.Name}' uses the art in its install folder ({path}).");
        }

        return changed;
    }
}
