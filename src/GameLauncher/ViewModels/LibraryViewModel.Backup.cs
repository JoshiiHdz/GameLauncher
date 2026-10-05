using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Export and import of the user's library data (see LibraryBackup for exactly what is and isn't in a backup).</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: stands in for the Save dialog; returns the chosen path or null for Cancel.</summary>
    internal Func<string, string?>? ExportPathPickerForTest { get; set; }

    /// <summary>Test seam: stands in for the Open dialog; returns the chosen path or null for Cancel.</summary>
    internal Func<string?>? ImportPathPickerForTest { get; set; }

    /// <summary>Test seam: stands in for "Import this?"; receives the question, returns true for Yes.</summary>
    internal Func<string, bool>? ConfirmImportForTest { get; set; }

    internal static string DefaultBackupFileName(DateTime localNow) => $"axis-library-backup-{localNow:yyyy-MM-dd}.json";

    private static string? PickExportPath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export library data", FileName = suggestedName, DefaultExt = ".json",
            Filter = "Axis library backup (*.json)|*.json", OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickImportPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import library data", Filter = "Axis library backup (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Saves favorites, hidden games, collections, notes, custom names, tracked play time and a few preferences to a file the
    /// user chooses. No cover art, keys or PC-specific folders go in it.</summary>
    [RelayCommand]
    private void ExportLibraryData()
    {
        try
        {
            var suggested = DefaultBackupFileName(DateTime.Now);
            var path = ExportPathPickerForTest is { } seam ? seam(suggested) : PickExportPath(suggested);
            if (path is null)
                return;

            var file = LibraryBackup.Build(_settings, AppInfo.Version, DateTime.UtcNow);
            var temp = path + ".tmp";
            File.WriteAllText(temp, LibraryBackup.ToJson(file), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true); // written whole or not at all - never a half file over an older backup

            Logger.Info($"Backup: exported {file.Games.Count} games to '{path}'.");
            StatusText = $"Exported data for {file.Games.Count:N0} {(file.Games.Count == 1 ? "game" : "games")} to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Backup: export failed.", ex);
            StatusText = $"Couldn't export your library data: {ex.Message}";
        }
    }

    /// <summary>Merges a backup into this library. Nothing existing is removed, and importing the same file again changes nothing.</summary>
    [RelayCommand]
    private void ImportLibraryData()
    {
        try
        {
            var path = ImportPathPickerForTest is { } seam ? seam() : PickImportPath();
            if (path is null)
                return;

            if (new FileInfo(path).Length > LibraryBackup.MaxFileBytes)
            {
                StatusText = "That file is too large to be a library backup.";
                return;
            }

            var (file, error) = LibraryBackup.Parse(File.ReadAllText(path));
            if (file is null)
            {
                Logger.Warn($"Backup: couldn't import '{path}': {error}");
                StatusText = error!;
                return;
            }

            var question = $"Import data for {file.Games.Count:N0} {(file.Games.Count == 1 ? "game" : "games")} from this backup"
                + (file.ExportedUtc == default ? "" : $" (made {file.ExportedUtc.ToLocalTime():d MMM yyyy})")
                + "?\n\nIt is merged into your library: nothing you have now is removed, and your preferences are replaced by the ones in the file.";
            var confirmed = ConfirmImportForTest is { } confirm
                ? confirm(question)
                : AppShell.Confirm("Import library data", question, yes: "Import", no: "Cancel");
            if (!confirmed)
            {
                StatusText = "Nothing was imported.";
                return;
            }

            var summary = LibraryBackup.Merge(_settings, file, _allGames.Select(g => g.Id).ToHashSet());
            _settingsService.Save(_settings);
            ApplyBackupSettings(file.Settings);
            ReapplyOverridesToGames();

            Logger.Info($"Backup: imported '{path}': {summary.GamesInFile} games, {summary.GamesAddedAsNew} new, {summary.GamesUpdated} changed.");
            StatusText = summary.Describe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Backup: import failed.", ex);
            StatusText = $"Couldn't read that file: {ex.Message}";
        }
    }

    private void ApplyBackupSettings(BackupSettings s)
    {
        DetectSteam = s.DetectSteam;
        DetectEpic = s.DetectEpic;
        DetectGog = s.DetectGog;
        DetectXbox = s.DetectXbox;
        DetectEa = s.DetectEa;
        DetectUbisoft = s.DetectUbisoft;
        DetectBattleNet = s.DetectBattleNet;
        DetectRockstar = s.DetectRockstar;
        DetectAmazonGames = s.DetectAmazonGames;
        DetectManual = s.DetectManual;
        IsSidebarExpanded = s.SidebarExpanded;
        VibrantBackground = s.VibrantBackground;
        MinimizeToTrayWhileGaming = s.MinimizeToTrayWhileGaming;
        TrackExternalGames = s.TrackExternalGames;
        CheckForUpdates = s.CheckForUpdates;
        OptimizeBeforeLaunch = s.OptimizeBeforeLaunch;
    }

    /// <summary>Pushes the (just merged) overrides onto the games already on screen, the same fields a rescan re-applies.</summary>
    private void ReapplyOverridesToGames()
    {
        foreach (var game in _allGames)
        {
            _settings.Overrides.TryGetValue(game.Id, out var over);
            if (!string.IsNullOrWhiteSpace(over?.CustomName))
                game.Name = over.CustomName;
            game.Hidden = over?.Hidden ?? false;
            game.Favorite = over?.Favorite ?? false;
            game.Collections = over?.Collections is { Count: > 0 } collections ? [.. collections] : [];
            if (over?.DateAdded is { } dateAdded)
                game.DateAdded = dateAdded;
            game.TotalPlaySeconds = over?.TotalPlaySeconds ?? 0;
            game.LastPlayedUtc = over?.LastPlayedUtc;
        }

        ApplyFilter();
    }
}
