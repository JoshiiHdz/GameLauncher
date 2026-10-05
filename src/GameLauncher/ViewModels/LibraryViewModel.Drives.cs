using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The drives games live on, the drive filter, and measuring how much space each game takes.</summary>
public partial class LibraryViewModel
{
    /// <summary>Only the drives games were actually detected on, not every drive on the system - a
    /// user with a 6-drive PC doesn't need to see the 4 that hold nothing but Windows and documents.
    /// Rebuilt from the game list itself (not the whole DriveInfo.GetDrives() set) so it's always
    /// exactly the drives relevant to this library.</summary>
    public ObservableCollection<DriveSpaceInfo> Drives { get; } = new();

    /// <summary>Hides the sidebar's "DRIVES" header when there's nothing to show it above.</summary>
    [ObservableProperty]
    private bool _hasDrives;

    /// <summary>Rebuilds the Drives list from the drive letters games actually live on. Re-run after
    /// every scan, not just once, since free space changes from other activity even when the set of
    /// drives games live on doesn't.</summary>
    private void RefreshDrives()
    {
        Drives.Clear();

        // Drives with games on them, plus any the user switched off: their games are gone from the library, but the row stays (dimmed)
        // so the same menu can switch the drive back on.
        var filter = NewDriveFilter();
        var driveLetters = _allGames
            .Select(g => Path.GetPathRoot(g.InstallDir))
            .Concat(filter.HasAny ? InstallPaths.ReadyDrives().Where(filter.IsIgnored) : [])
            .Where(root => !string.IsNullOrEmpty(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase);

        foreach (var root in driveLetters)
        {
            try
            {
                var drive = new DriveInfo(root!);
                if (!drive.IsReady)
                    continue;

                Drives.Add(new DriveSpaceInfo
                {
                    Letter = drive.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsIgnored = filter.IsIgnored(root),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Warn($"Couldn't read space for drive '{root}'.", ex);
            }
        }

        HasDrives = Drives.Count > 0;
        HasMultipleDrives = Drives.Count > 1;
        RefreshSearchableDrives();

        // A rescan replaces every DriveSpaceInfo instance wholesale, which would otherwise silently
        // drop the previous selection's highlight. If the selected drive no longer has any games on it
        // at all, clear the filter instead of leaving it pointed at a letter nothing can ever match.
        if (SelectedDriveLetter is { } letter && Drives.All(d => !string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedDriveLetter = null; // re-enters via OnSelectedDriveLetterChanged, which re-syncs IsSelected/ApplyFilter
            return;
        }

        foreach (var drive in Drives)
            drive.IsSelected = string.Equals(drive.Letter, SelectedDriveLetter, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    private string? _selectedDriveLetter;

    [ObservableProperty]
    private bool _hasSelectedDriveFilter;

    partial void OnSelectedDriveLetterChanged(string? value)
    {
        HasSelectedDriveFilter = value is not null;

        foreach (var drive in Drives)
            drive.IsSelected = string.Equals(drive.Letter, value, StringComparison.OrdinalIgnoreCase);

        ApplyFilter();
        _ = EstimateInstallSizesAsync();
    }

    /// <summary>Clicking a drive filters the library down to games on it; clicking the ALREADY-selected
    /// drive again clears the filter - the same toggle shape as a sidebar source switch, just driven by
    /// a click instead of a checkbox.</summary>
    [RelayCommand]
    private void SelectDrive(string? letter)
    {
        // A drive that is switched off has no games to show; opening it would only give an empty library.
        if (Drives.FirstOrDefault(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase))?.IsIgnored == true)
            return;

        ClosePage();
        SelectedDriveLetter = string.Equals(SelectedDriveLetter, letter, StringComparison.OrdinalIgnoreCase) ? null : letter;
    }

    private static string? DriveLetterOf(string installDir)
    {
        try
        {
            return Path.GetPathRoot(installDir)?.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private CancellationTokenSource? _sizeEstimationCts;

    /// <summary>Test seam: a synchronous, deterministic stand-in for InstallSizeEstimator.Estimate -
    /// tests must never depend on real disk I/O or its timing.</summary>
    internal Func<string, CancellationToken, long?>? InstallSizeEstimatorForTest { get; set; }

    /// <summary>Measures every game's install size in the background, one at a time so sizes populate
    /// as they're found rather than the UI freezing until the whole library finishes. Whichever drive is
    /// selected goes FIRST, so the thing being looked at fills in before the rest - the sizes are needed
    /// library-wide regardless (the hero shows one with no drive selected, and "Largest installed" sorts
    /// on them), so scoping the work to one drive would just mean most of them never arrive. Each walk
    /// is itself bounded and cancellable (see InstallSizeEstimator). Already-known sizes are skipped, so
    /// re-running this after a view change costs nothing. Cancel-and-replace (the same shape as
    /// RefreshAsync's _refreshCts) means a slower, now-stale pass can never overwrite a newer one.</summary>
    private async Task EstimateInstallSizesAsync(string? priorityLetter = null)
    {
        _sizeEstimationCts?.Cancel();

        var cts = new CancellationTokenSource();
        _sizeEstimationCts = cts;
        var token = cts.Token;

        var letter = priorityLetter ?? SelectedDriveLetter;
        var targets = _allGames
            .Where(g => g.InstallSizeBytes is null && !_sizeEstimationInFlight.Contains(g.Id))
            .OrderByDescending(g => letter is not null
                && string.Equals(DriveLetterOf(g.InstallDir), letter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var game in targets)
        {
            if (token.IsCancellationRequested)
                return;

            // Claimed before the walk starts and released after it: a pass that begins while this one
            // is mid-walk skips what's already being measured instead of queuing it a second time.
            // Without this, a selection change raised from INSIDE a walk re-queues the very game still
            // being measured (its size isn't assigned yet, so it still looks unmeasured) and recurses
            // until the stack gives out - a real, confirmed crash, not a theoretical one.
            if (!_sizeEstimationInFlight.Add(game.Id))
                continue;

            long? bytes;
            try
            {
                bytes = InstallSizeEstimatorForTest is { } forTest
                    ? forTest(game.InstallDir, token)
                    : await Task.Run(() => InstallSizeEstimator.Estimate(game.InstallDir, token).Bytes, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _sizeEstimationInFlight.Remove(game.Id);
            }

            if (!ReferenceEquals(_sizeEstimationCts, cts))
                return; // superseded by a newer selection while this game's walk was running

            game.InstallSizeBytes = bytes;
        }
    }

    // Game ids currently being measured, so overlapping passes never walk the same folder twice (and
    // can never recurse into each other) - see EstimateInstallSizesAsync.
    private readonly HashSet<string> _sizeEstimationInFlight = new();

    // ---- switching a drive off -------------------------------------------------------------------------------------------

    /// <summary>Test seam: the volume serial of a drive root like "D:\", so tests never depend on this PC's disks.</summary>
    internal Func<string, uint?> VolumeSerialOf { get; set; } = VolumeInfo.GetSerialNumber;

    private DriveFilter NewDriveFilter() => new(_settings.IgnoredDrives, VolumeSerialOf);

    /// <summary>Every drive that can be switched on or off, for the Settings list - all ready fixed and removable drives, not only the
    /// ones with games (a drive with nothing found yet can still be switched off before the first search).</summary>
    public ObservableCollection<DriveToggleItem> SearchableDrives { get; } = new();

    private void RefreshSearchableDrives()
    {
        SearchableDrives.Clear();

        foreach (var root in InstallPaths.ReadyDrives())
        {
            try
            {
                var drive = new DriveInfo(root);
                var letter = DriveFilter.NormalizeLetter(root);
                SearchableDrives.Add(new DriveToggleItem(
                    letter,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    $"{DriveSpaceInfo.FormatGb(drive.TotalSize - drive.AvailableFreeSpace)} used of {DriveSpaceInfo.FormatGb(drive.TotalSize)}",
                    () => !NewDriveFilter().IsIgnored(letter + @"\"),
                    searched => _ = SetDriveIgnoredAsync(letter, ignored: !searched)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Warn($"Couldn't read drive '{root}' for the search list.", ex);
            }
        }
    }

    /// <summary>The sidebar's 3-dot menu: switches a drive off, or back on.</summary>
    [RelayCommand]
    private Task ToggleDriveIgnoredAsync(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter))
            return Task.CompletedTask;

        var ignoredNow = NewDriveFilter().IsIgnored(letter + @"\");
        return SetDriveIgnoredAsync(letter, ignored: !ignoredNow);
    }

    /// <summary>Switches a drive off (its games leave the library at once, nothing on it is searched again) or back on (the library is
    /// searched again, including a full look at the disks, so its games return). The choice is saved by volume serial as well as letter.</summary>
    internal async Task SetDriveIgnoredAsync(string letter, bool ignored)
    {
        var normalized = DriveFilter.NormalizeLetter(letter);
        if (normalized.Length == 0)
            return;

        var root = normalized + @"\";
        if (NewDriveFilter().IsIgnored(root) == ignored)
            return;

        if (ignored)
        {
            string? label = null;
            try
            {
                label = new DriveInfo(root).VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            _settings.IgnoredDrives.Add(new IgnoredDrive { Letter = normalized, VolumeSerial = VolumeSerialOf(root), Label = label });
        }
        else
        {
            // Whichever record makes this drive ignored (by serial, or by letter when the serial is unknown) is the one to remove.
            _settings.IgnoredDrives.RemoveAll(entry => new DriveFilter([entry], VolumeSerialOf).IsIgnored(root));
        }

        _settingsService.Save(_settings);

        if (ignored)
        {
            // Gone from the library right now - no rescan, so nothing waits on the disks. A scan that is still running is filtered the same
            // way when it publishes (see ReplaceAllGames).
            var filter = NewDriveFilter();
            var removed = _allGames.RemoveAll(g => filter.IsIgnored(g.InstallDir));
            if (SelectedDriveLetter is { } selected && filter.IsIgnored(selected + @"\"))
                SelectedDriveLetter = null;

            ApplyFilter();
            RefreshDrives();
            StatusText = removed == 0
                ? $"{normalized} will not be searched"
                : $"{normalized} switched off - {removed} {(removed == 1 ? "game" : "games")} hidden until it is switched back on";
            Logger.Info($"Drive {normalized} switched off ({removed} game(s) removed from the library).");
        }
        else
        {
            RefreshDrives();
            StatusText = $"{normalized} switched on - searching it for games...";
            Logger.Info($"Drive {normalized} switched back on.");
            await (RescanAfterChange ?? ForcedRescan)();
        }

        foreach (var item in SearchableDrives)
            item.Refresh();
    }
}
