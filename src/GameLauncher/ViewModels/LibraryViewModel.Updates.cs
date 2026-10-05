using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;
using Velopack;

namespace GameLauncher.ViewModels;

/// <summary>Checking for, downloading and announcing app updates, and the "What's new" notes shown after one.</summary>
public partial class LibraryViewModel
{
    private readonly UpdateService _updateService = new();

    private readonly PendingUpdateNotesService _pendingUpdateNotesService;

    // Held here rather than just exposing the version string: DownloadUpdateCommand needs to hand
    // the actual UpdateInfo back to UpdateService.DownloadAndApplyAsync, and re-checking for updates
    // a second time just to get it back would be wasteful and could race with a newer release
    // appearing between the two calls.
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    private bool _checkForUpdates = true;

    /// <summary>Drives the update banner - true only once a real, confirmed-newer release has been
    /// found, never speculatively (a failed/inconclusive check just leaves this false).</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _availableUpdateVersion = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _isUpdating;

    /// <summary>Drives the "What's New" dialog - true for exactly one launch, the one right after
    /// DownloadUpdateAsync applied an update and restarted the app. See PendingUpdateNotesService.</summary>
    [ObservableProperty]
    private bool _showWhatsNew;

    [ObservableProperty]
    private string _whatsNewVersion = string.Empty;

    [ObservableProperty]
    private string _whatsNewNotes = string.Empty;

    /// <summary>Called by MainWindow right after the "What's New" dialog closes, however it closed
    /// (the "Got it" button, the window chrome's own close button, Alt+F4, ...) - the one point that's
    /// guaranteed to mean the notes were actually shown to the user, which is what makes it safe to
    /// delete the marker now instead of at read time. See PendingUpdateNotesService's remarks.</summary>
    public void AcknowledgeWhatsNew()
    {
        _pendingUpdateNotesService.Acknowledge();
        ShowWhatsNew = false;
    }

    private async Task CheckForUpdateInBackgroundAsync()
    {
        var result = await _updateService.CheckForUpdateAsync();
        if (result.Status == UpdateCheckStatus.UpdateAvailable)
            ApplyFoundUpdate(result.Update!);
    }

    private void ApplyFoundUpdate(UpdateInfo update)
    {
        _pendingUpdate = update;
        AvailableUpdateVersion = update.TargetFullRelease.Version.ToString();
        UpdateAvailable = true;
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    /// <summary>The Settings window's "Check for Updates Now" button - runs regardless of the
    /// CheckForUpdates toggle (an explicit click is a request to check right now, not a request to
    /// change the toggle), and unlike the silent startup check, gives feedback either way so the
    /// button doesn't look like it did nothing when already up to date.</summary>
    [RelayCommand]
    private async Task CheckForUpdateNowAsync()
    {
        StatusText = "Checking for updates...";
        var result = await _updateService.CheckForUpdateAsync();

        if (result.Status == UpdateCheckStatus.UpdateAvailable)
        {
            ApplyFoundUpdate(result.Update!);
            StatusText = $"Update available: v{AvailableUpdateVersion}";
            return;
        }

        StatusText = result.Status switch
        {
            UpdateCheckStatus.UpToDate => "You're on the latest version.",
            UpdateCheckStatus.NotInstalled => "Update checks aren't available for this copy (not an installed build).",
            _ => "Couldn't check for updates - try again later.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate is not { } update)
            return;

        // Never restart the app out from under an active play session - _runningGameId is set/cleared
        // by MarkGameRunning/MarkGameNotRunning, the same calls that drive the "Running" badge, and
        // unlike scanning _allGames it survives a rescan replacing every GameEntry mid-session.
        if (_runningGameId is not null || _passiveSessions.Count > 0)
        {
            StatusText = "Can't update while a game is running - try again after it closes.";
            return;
        }

        IsUpdating = true;
        StatusText = $"Downloading update {AvailableUpdateVersion}...";
        try
        {
            await _updateService.DownloadAndApplyAsync(update,
                new Progress<int>(percent => StatusText = $"Downloading update {AvailableUpdateVersion}... {percent}%"));

            // ApplyUpdatesAndRestart exits this process on success - nothing below normally runs.
        }
        catch (Exception ex)
        {
            // Broad by design: Velopack can fail in ways beyond plain I/O (checksum mismatch, a held
            // update lock, a corrupt package) and every one of them must still land here rather than
            // escape this command and leave the UI stuck showing "Updating..." forever - see the
            // finally block below, which is what actually guarantees that can't happen.
            Logger.Error("Failed to download/apply the update.", ex);
            StatusText = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private bool CanDownloadUpdate() => !IsUpdating;
}
