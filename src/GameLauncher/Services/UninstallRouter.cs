using GameLauncher.Converters;
using GameLauncher.Models;

namespace GameLauncher.Services;

public enum UninstallRoute
{
    /// <summary>The game's own uninstall wizard - the same one Windows' Installed apps list would run for it.</summary>
    Wizard,

    /// <summary>A link the launcher itself handles - straight to this game's uninstall prompt or page.</summary>
    LauncherPage,

    /// <summary>The launcher's own app, opened for the user to find the game and choose Uninstall.</summary>
    LauncherApp,

    /// <summary>An Xbox / Microsoft Store game: no wizard exists, Windows removes the package (after the launcher has asked).</summary>
    XboxPackage,

    /// <summary>Nothing can uninstall it: it was copied into its folder, not installed. The launcher offers to open the folder.</summary>
    NoUninstaller,
}

/// <summary>What "Uninstall..." does, in the order Revo Uninstaller works: the game's own uninstall wizard, found in the same
/// installed-programs list Windows uses (or in the game's folder); Steam's own prompt; Windows' package removal for Xbox/Store games;
/// then the owning launcher. It never lands on a list the user has to search. Axis never deletes a game's files itself.</summary>
public sealed record UninstallTarget(UninstallRoute Route, string Target, string Explanation, string Arguments = "");

public static class UninstallRouter
{
    /// <param name="findLauncherExe">Finds a launcher's executable on this PC (null when it isn't installed).</param>
    /// <param name="findWizard">Finds the game's own uninstaller (see <see cref="UninstallLocator"/>); null means none is looked for.</param>
    public static UninstallTarget Resolve(GameEntry game, Func<GameSource, string?> findLauncherExe,
        Func<GameEntry, UninstallCommand?>? findWizard = null)
    {
        var launcher = GameSourceDisplayConverter.Name(game.Source);

        // Steam and GOG Galaxy each have a link that opens the right place for exactly this game.
        if (game.Source == GameSource.Steam && NumericIdAfter(game.Id, "steam-") is { } appId)
        {
            return new UninstallTarget(UninstallRoute.LauncherPage, $"steam://uninstall/{appId}",
                $"Opened Steam's uninstall prompt for {game.Name} - confirm there to remove it.");
        }

        // Anything with an uninstaller of its own (registered with Windows, or sitting in its folder) gets that wizard, whichever
        // launcher it came from or none at all.
        if (findWizard?.Invoke(game) is { } wizard)
        {
            return new UninstallTarget(UninstallRoute.Wizard, wizard.Exe,
                $"Opened the uninstaller for {game.Name} - follow it to remove the game.", wizard.Arguments);
        }

        // Xbox / Microsoft Store games have no wizard; Windows removes the package.
        if (game.Source == GameSource.Xbox)
        {
            return new UninstallTarget(UninstallRoute.XboxPackage, XboxPackageRemover.FamilyNameOf(game) ?? "",
                $"Uninstalling {game.Name}...", game.InstallDir);
        }

        if (game.Source == GameSource.Gog && NumericIdAfter(game.Id, "gog-") is { } productId && findLauncherExe(GameSource.Gog) is not null)
        {
            return new UninstallTarget(UninstallRoute.LauncherPage, $"goggalaxy://openGameView/{productId}",
                $"Opened {game.Name} in GOG Galaxy - choose Uninstall there.");
        }

        // The other launchers have no dependable per-game link, so open the launcher itself.
        if (findLauncherExe(game.Source) is { } exe)
        {
            return new UninstallTarget(UninstallRoute.LauncherApp, exe,
                $"Opened {launcher} - find {game.Name} in your library and choose Uninstall.");
        }

        return new UninstallTarget(UninstallRoute.NoUninstaller, game.InstallDir,
            $"{game.Name} has no uninstaller I can find.");
    }

    private static string? NumericIdAfter(string id, string prefix) =>
        id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length && id[prefix.Length..].All(char.IsAsciiDigit)
            ? id[prefix.Length..]
            : null;
}
