using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Works out which launcher installed a game from what that launcher leaves in the game's own folder, so a game found by watching a
/// folder lands under the right launcher in the sidebar instead of under "No launcher". Only markers a launcher writes and nothing
/// else does are used - never the folder's name or its place on the disk, since people put games anywhere:
///  - Epic Games: a <c>.egstore</c> folder;
///  - GOG: <c>goggame-*.info</c> files;
///  - EA app / Origin: <c>__Installer\installerdata.xml</c>;
///  - Amazon Games: <c>fuel.json</c>;
///  - Xbox: <c>MicrosoftGame.config</c> next to the game's content.
/// Steam has no marker in the folder (its id lives in the library manifests, which the Steam scan already reads), so a Steam game is
/// left to that scan. No marker means the game stays "No launcher".
/// </summary>
public static class LauncherMarkers
{
    /// <summary>How many folders up from the game's folder are looked in - the exe is often in <c>Binaries\Win64</c>, the marker at the root.</summary>
    private const int LevelsUp = 3;

    /// <summary>The launcher this folder belongs to, or null when it carries no launcher's marker.</summary>
    public static GameSource? Infer(string installDir, Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null,
        Func<string, string, bool>? anyFileMatching = null)
    {
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;
        anyFileMatching ??= AnyFileMatching;

        var folder = installDir;
        for (var level = 0; level <= LevelsUp && !string.IsNullOrEmpty(folder); level++)
        {
            if (directoryExists(Path.Combine(folder, ".egstore")))
                return GameSource.Epic;
            if (anyFileMatching(folder, "goggame-*.info"))
                return GameSource.Gog;
            if (fileExists(Path.Combine(folder, "__Installer", "installerdata.xml")))
                return GameSource.Ea;
            if (fileExists(Path.Combine(folder, "fuel.json")))
                return GameSource.AmazonGames;
            if (fileExists(Path.Combine(folder, "MicrosoftGame.config")))
                return GameSource.Xbox;

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    /// <summary>Gives every game that no scanner claimed (it came from a watched folder) the launcher its folder says it belongs to.
    /// Returns how many were relabelled.</summary>
    public static int Apply(IEnumerable<GameEntry> games, Func<GameEntry, GameSource?>? infer = null)
    {
        infer ??= g => Infer(g.InstallDir);
        var changed = 0;
        foreach (var game in games.Where(g => g.Source == GameSource.Manual))
        {
            if (infer(game) is not { } source || source == GameSource.Manual)
                continue;

            game.Source = source;
            changed++;
            Logger.Info($"'{game.Name}' was found in a watched folder; its folder shows it belongs to {source}.");
        }

        return changed;
    }

    private static bool AnyFileMatching(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, pattern).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
