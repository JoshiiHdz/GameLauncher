using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>"What's using this drive?": the games on one drive ranked by size, opened from a drive's right-click menu or the button
/// beside the drive filter. The low-space warning itself lives on DriveSpaceInfo.</summary>
public partial class LibraryViewModel
{
    /// <summary>Test seam: receives the drive's view model instead of opening the window.</summary>
    internal Action<StorageViewModel>? StorageDialogForTest { get; set; }

    /// <summary>Every game whose install folder is on the drive - hidden ones too, since they take space all the same.</summary>
    internal List<GameEntry> GamesOnDrive(string letter) =>
        _allGames.Where(g => string.Equals(DriveLetterOf(g.InstallDir), letter, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>No letter means the drive currently filtering the grid, which is what the button beside "Clear drive filter" means.</summary>
    [RelayCommand]
    private void ShowStorage(string? letter)
    {
        letter ??= SelectedDriveLetter;
        if (letter is null)
            return;

        StorageViewModel? storage = null;
        try
        {
            var drive = Drives.FirstOrDefault(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase));
            storage = new StorageViewModel(drive?.Letter ?? letter, drive, GamesOnDrive(letter));
            storage.OpenInstallLocationRequested += OpenInstallLocation;

            // Created before the measuring starts, so every size that lands re-ranks the open page.
            _ = EstimateInstallSizesAsync(letter);

            if (StorageDialogForTest is { } seam)
            {
                seam(storage);
                storage.Dispose();
            }
            else
            {
                // The page owns the view model from here: ClosePage disposes it.
                OpenPage(StoragePageKey, storage.Title, storage);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Couldn't open the storage page for '{letter}'.", ex);
            StatusText = $"Couldn't open the storage page for {letter}: {ex.Message}";
            storage?.Dispose();
        }
    }
}
