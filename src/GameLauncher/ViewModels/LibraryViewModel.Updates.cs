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
    [NotifyPropertyChangedFor(nameof(InstallUpdateText), nameof(UpdatePillText))]
    private string _availableUpdateVersion = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand), nameof(CheckForUpdateNowCommand))]
    [NotifyPropertyChangedFor(nameof(InstallUpdateText), nameof(UpdatePillText), nameof(CheckUpdateButtonText))]
    private bool _isUpdating;

    /// <summary>The update state in words, kept where the person is looking: the Settings page's Updates group says it in place (the status line it also updates is
    /// behind the page), and the top bars show a button when one is ready. Never blank.</summary>
    [ObservableProperty]
    private string _updateStatusText = $"You're on version v{AppInfo.Version}. The launcher checks when it starts.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdateNowCommand))]
    [NotifyPropertyChangedFor(nameof(CheckUpdateButtonText))]
    private bool _isCheckingForUpdate;

    /// <summary>The Settings button that installs a found update.</summary>
    public string InstallUpdateText => IsUpdating ? "Updating..." : $"Install v{AvailableUpdateVersion} and restart";

    /// <summary>The top bar's update button (the PlayStation theme and Axis).</summary>
    public string UpdatePillText => IsUpdating ? "Updating..." : $"Update v{AvailableUpdateVersion}";

    public string CheckUpdateButtonText => IsCheckingForUpdate ? "Checking..." : "Check for Updates Now";

    private void SetUpdateStatus(string text)
    {
        UpdateStatusText = text;
        StatusText = text;
    }

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
        {
            ApplyFoundUpdate(result.Update!);
            UpdateStatusText = $"Update available: v{AvailableUpdateVersion}. It is ready to install.";
        }
        else if (result.Status == UpdateCheckStatus.UpToDate)
        {
            UpdateStatusText = $"You're on the latest version (v{AppInfo.Version}). Checked when the launcher started.";
        }
    }

    private void ApplyFoundUpdate(UpdateInfo update)
    {
        _pendingUpdate = update;
        ShowUpdateFound(update.TargetFullRelease.Version.ToString());
    }

    /// <summary>Records that this version is ready to install: the banner, the top bars' button and the Settings page's Install button all follow.</summary>
    internal void ShowUpdateFound(string version)
    {
        AvailableUpdateVersion = version;
        UpdateAvailable = true;
    }

    /// <summary>Tests answer the check without the network.</summary>
    internal Func<Task<UpdateCheckResult>>? CheckForUpdateForTest { get; set; }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        _settingsService.Save(_settings);
    }

    /// <summary>Hides the update notice (the pill's right-click menu). Not while the update is being downloaded - the pill is what shows that.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (!IsUpdating)
            UpdateAvailable = false;
    }

    /// <summary>The Settings window's "Check for Updates Now" button - runs regardless of the
    /// CheckForUpdates toggle (an explicit click is a request to check right now, not a request to
    /// change the toggle), and unlike the silent startup check, gives feedback either way so the
    /// button doesn't look like it did nothing when already up to date.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdateNow))]
    private async Task CheckForUpdateNowAsync()
    {
        IsCheckingForUpdate = true;
        SetUpdateStatus("Checking for updates...");
        try
        {
            var result = await (CheckForUpdateForTest?.Invoke() ?? _updateService.CheckForUpdateAsync());

            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Update is not null)
            {
                ApplyFoundUpdate(result.Update);
                SetUpdateStatus($"Update available: v{AvailableUpdateVersion}. Install it from here, or from the Update button at the top.");
                return;
            }

            SetUpdateStatus(result.Status switch
            {
                UpdateCheckStatus.UpToDate => $"You're on the latest version (v{AppInfo.Version}).",
                UpdateCheckStatus.NotInstalled => "Update checks aren't available for this copy (not an installed build).",
                _ => "Couldn't check for updates - try again later.",
            });
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    private bool CanCheckForUpdateNow() => !IsCheckingForUpdate && !IsUpdating;

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
            SetUpdateStatus("Can't update while a game is running - try again after it closes.");
            return;
        }

        IsUpdating = true;
        SetUpdateStatus($"Downloading update v{AvailableUpdateVersion}...");
        try
        {
            await _updateService.DownloadAndApplyAsync(update,
                new Progress<int>(percent => SetUpdateStatus($"Downloading update v{AvailableUpdateVersion}... {percent}%")));

            // ApplyUpdatesAndRestart exits this process on success - nothing below normally runs.
        }
        catch (Exception ex)
        {
            // Broad by design: Velopack can fail in ways beyond plain I/O (checksum mismatch, a held
            // update lock, a corrupt package) and every one of them must still land here rather than
            // escape this command and leave the UI stuck showing "Updating..." forever - see the
            // finally block below, which is what actually guarantees that can't happen.
            Logger.Error("Failed to download/apply the update.", ex);
            SetUpdateStatus($"Update failed: {ex.Message}");
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private bool CanDownloadUpdate() => !IsUpdating;
}
