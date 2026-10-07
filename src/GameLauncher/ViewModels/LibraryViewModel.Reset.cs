using System.IO;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Clearing the cache and resetting settings. Covers and icons are cached on disk, and after an update a cached one can be out of
/// date; these put the launcher back to looking things up fresh. A person's own data - favorites, hidden games, collections, notes,
/// play time, watched folders, and every cover they chose themselves - is never touched by either.</summary>
public partial class LibraryViewModel
{
    /// <summary>The cache folders under the data folder: cover art from every catalog, and the icons read from game exes. Custom
    /// covers the user picked live elsewhere (CustomCovers) and are not here.</summary>
    internal static readonly string[] CacheFolderNames = ["CoverArtCache", "IconCache"];

    /// <summary>Test seam: stands in for the question; receives its text, returns true for yes.</summary>
    internal Func<string, bool>? ConfirmResetForTest { get; set; }

    /// <summary>What happens after a change that makes the library out of date (a reset, an uninstalled game): a rescan. Replaced in
    /// tests so they never scan the machine.</summary>
    internal Func<Task> RescanAfterChange { get; set; } = null!;

    private bool ConfirmReset(string title, string question, string yes) =>
        ConfirmResetForTest is { } seam ? seam(question) : AppShell.Confirm(title, question, yes, "Cancel", warning: true);

    /// <summary>Deletes the cached cover art and icons and forgets which covers were chosen automatically, so the next scan asks the
    /// catalogs again instead of trusting stale answers. Returns how many cache folders were removed.</summary>
    internal int ClearCacheCore()
    {
        var removed = 0;
        foreach (var name in CacheFolderNames)
        {
            var folder = Path.Combine(_settingsService.DataDir, name);
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Reset: couldn't remove the cache folder '{folder}'.", ex);
            }
        }

        PlatformIconService.ClearCache();

        // Automatic choices only: a cover the user picked, and an identity the user confirmed, are theirs and stay.
        foreach (var over in _settings.Overrides.Values)
        {
            if (over.Artwork is { IsUserSelected: false })
                over.Artwork = null;

            if (over.Identity is { } identity)
                identity.LastAttempt = null; // forget "asked recently, no match", so the catalogs are asked again at once
        }

        _settingsService.Save(_settings);
        Logger.Info($"Reset: cache cleared ({removed} folder(s)).");
        return removed;
    }

    /// <summary>Every preference back to its default (the same values a new install starts with). Goes through the properties, so each
    /// change is saved and takes effect - Start with Windows is registered again, the background effect is reapplied - exactly as if
    /// the person had flipped each switch.</summary>
    internal void ResetSettingsCore()
    {
        VibrantBackground = true;
        MinimizeToTrayWhileGaming = true;
        MinimizeToTrayOnMinimize = false;
        ControllerMap.Reset();
        OptimizeBeforeLaunch = false;
        FocusPlayEnabled = false;
        FocusPlayOnlyWhenPluggedIn = true;
        GlobalHotkeyEnabled = true;
        TrackExternalGames = true;
        CheckForUpdates = true;
        StartMaximized = true;
        AppearanceTheme = ThemeId.Axis;
        StartWithWindows = _startupRegistration?.SuitableForDefaultStartup ?? false; // on by default, but only for an installed copy

        _bulkSettingChange = true;
        try
        {
            FindGamesWithoutLauncher = true; // the caller rescans once, so this must not start a scan of its own
        }
        finally
        {
            _bulkSettingChange = false;
        }

        DetectSteam = DetectEpic = DetectGog = DetectXbox = DetectEa = DetectUbisoft = DetectBattleNet = DetectRockstar = DetectAmazonGames = DetectManual = true;
        IsSidebarExpanded = true;
        IsToolsExpanded = IsLaunchersExpanded = IsDrivesExpanded = true;
        SettingsCategory = 0;
        Logger.Info("Reset: settings restored to their defaults.");
    }

    /// <summary>Clears the cache and looks the covers up again - for covers that went stale after an update.</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (!ConfirmReset("Clear cache",
                "Clear the saved cover art and icons, and look every cover up again?\n\nYour favorites, collections, notes, play time and the covers you chose yourself are not touched.",
                "Clear cache"))
        {
            return;
        }

        ClearCacheCore();
        StatusText = "Cache cleared - looking the covers up again...";
        await (RescanAfterChange ?? DefaultRescan)();
    }

    /// <summary>Restores every setting to its default and clears the cache.</summary>
    [RelayCommand]
    private async Task ResetEverythingAsync()
    {
        if (!ConfirmReset("Reset all settings and cache",
                "Put every setting back to its default and clear the cache?\n\nYour favorites, hidden games, collections, notes, play time, watched folders and the covers you chose yourself are kept.",
                "Reset"))
        {
            return;
        }

        ResetSettingsCore();
        ClearCacheCore();
        StatusText = "Settings reset and cache cleared - looking the covers up again...";
        await (RescanAfterChange ?? DefaultRescan)();
    }

    private Task DefaultRescan() => RefreshCommand.ExecuteAsync(null);
}
