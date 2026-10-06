using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Actions on one game: launch it, open its folder, favorite it or hide it.</summary>
public partial class LibraryViewModel
{
    [RelayCommand]
    private void ToggleFavorite(GameEntry? game)
    {
        if (game is null)
            return;

        game.Favorite = !game.Favorite;

        if (!_settings.Overrides.TryGetValue(game.Id, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[game.Id] = over;
        }

        over.Favorite = game.Favorite;
        _settingsService.Save(_settings);

        // Always re-filter: the game has to move between the Favorites section and the main grid.
        ApplyFilter();
    }

    /// <summary>Toggles a game's Hidden state - shared by the card's "Hide" button and the Hidden
    /// section's "Unhide" button, since it's the same flip either direction.</summary>
    [RelayCommand]
    private void ToggleHidden(GameEntry? game)
    {
        if (game is null)
            return;

        game.Hidden = !game.Hidden;

        if (!_settings.Overrides.TryGetValue(game.Id, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[game.Id] = over;
        }

        over.Hidden = game.Hidden;
        _settingsService.Save(_settings);

        ApplyFilter();
    }

    /// <summary>Raised after a game successfully launches, so the view can get out of the way and
    /// start watching for the game to exit. Carries the started process where there is one.</summary>
    public event Action<GameEntry, Process?>? GameLaunched;

    [RelayCommand]
    private void Launch(GameEntry? game)
    {
        if (game is null)
            return;

        var switchedPowerPlan = false;
        try
        {
            Logger.Info($"Launching '{game.Name}' ({game.Source}) - {game.LaunchUri ?? game.ExecutablePath}");
            if (OptimizeBeforeLaunch)
                FreeMemoryBeforeLaunch();
            switchedPowerPlan = BeginFocusPlay();
            var started = GameLauncherService.Launch(game);
            GameLaunched?.Invoke(game, started);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            if (switchedPowerPlan)
                EndFocusPlay(); // the game never started: do not leave the PC on the fast plan (a switch made for another running game is left alone)
            Logger.Error($"Failed to launch '{game.Name}'.", ex);
            StatusText = $"Failed to launch {game.Name}: {ex.Message}";
        }
    }

    /// <summary>Test seam: stands in for the real explorer.exe launch - actually shelling out would be
    /// slow, visible, and untestable. Receives the path Explorer was asked to show (the game's exe when it
    /// is selected inside its folder, otherwise the folder itself).</summary>
    internal Action<string>? OpenFolderInExplorerForTest { get; set; }

    /// <summary>What "Open install location" shows: the game's own executable, selected inside its folder, when that file
    /// exists - so the user lands on the exact file, not at the top of a folder full of them - otherwise the install folder.
    /// Null when neither exists any more. ExecutablePath can also be a directory (a packaged game with no confirmed exe)
    /// or a path that has since gone, both of which fall back to the folder.</summary>
    internal static (string Path, bool SelectFile)? ResolveInstallLocationTarget(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            return (game.ExecutablePath, true);

        return Directory.Exists(game.InstallDir) ? (game.InstallDir, false) : null;
    }

    [RelayCommand]
    private void OpenInstallLocation(GameEntry? game)
    {
        if (game is null)
            return;

        if (ResolveInstallLocationTarget(game) is not var (path, selectFile))
        {
            StatusText = $"Can't find {game.Name}'s install folder anymore - it may have been moved or removed.";
            Logger.Warn($"Open install location: neither '{game.ExecutablePath}' nor '{game.InstallDir}' exists any more for '{game.Name}'.");
            return;
        }

        try
        {
            Logger.Info($"Open install location: '{game.Name}' -> {(selectFile ? "selecting" : "opening")} '{path}'.");
            if (OpenFolderInExplorerForTest is { } forTest)
                forTest(path);
            else
                Process.Start(new ProcessStartInfo("explorer.exe", selectFile ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logger.Error($"Failed to open install location for '{game.Name}'.", ex);
            StatusText = $"Couldn't open {game.Name}'s install folder: {ex.Message}";
        }
    }
}
