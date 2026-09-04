using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Win32;
using Velopack;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly GameScannerService _scannerService = new();
    private readonly UpdateService _updateService = new();
    private readonly PendingUpdateNotesService _pendingUpdateNotesService;
    private readonly AppSettings _settings;
    private List<GameEntry> _allGames = new();
    private CancellationTokenSource? _refreshCts;

    // Id of the game GameSessionWatcher is currently tracking, kept independent of any particular
    // GameEntry instance. RefreshAsync replaces every entry in _allGames wholesale on each rescan,
    // so tracking "is a game running" via GameEntry.IsRunning alone would let DownloadUpdateCommand's
    // running-game guard go blind the moment a rescan happens mid-session - it would only ever see
    // the freshly-scanned entries, which all start not-running. MainWindow owns the actual watcher
    // lifecycle and calls MarkGameRunning/MarkGameNotRunning instead of touching GameEntry.IsRunning
    // directly, so this id and the badge can never disagree.
    private string? _runningGameId;

    // Ownership token for the session identified by _runningGameId. MarkGameNotRunning only clears
    // tracking when the session id it's given still matches this value. Without it, relaunching the
    // *same* game right after a refresh is broken: MainWindow calls MarkGameRunning(newEntry) for the
    // new session and then MarkGameNotRunning(oldEntry) to clean up the one it superseded - but
    // oldEntry and newEntry share the same game id, so a plain id comparison in MarkGameNotRunning
    // can't tell "the session I'm cleaning up" apart from "the session that just replaced it," and
    // would wrongly clear the brand new session no matter which order the two calls happen in. A
    // monotonically increasing session id makes that distinction unambiguous regardless of call
    // order. See MarkGameRunning/MarkGameNotRunning.
    private int _runningSessionId;
    private int _sessionCounter;

    // Held here rather than just exposing the version string: DownloadUpdateCommand needs to hand
    // the actual UpdateInfo back to UpdateService.DownloadAndApplyAsync, and re-checking for updates
    // a second time just to get it back would be wasteful and could race with a newer release
    // appearing between the two calls.
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateDesktopShortcutCommand))]
    private bool _canCreateDesktopShortcut;

    [ObservableProperty]
    private string _desktopShortcutButtonText = "Create Desktop Shortcut";

    [ObservableProperty]
    private GameSortOption _sortOption = GameSortOption.NameAsc;

    [ObservableProperty]
    private bool _hasNoGames;

    [ObservableProperty]
    private string _libraryHeaderText = "My Library";

    /// <summary>Drives the Favorites section and its separator - both vanish when nothing is starred.</summary>
    [ObservableProperty]
    private bool _hasFavorites;

    /// <summary>User's toggle for whether the Hidden section is expanded - deliberately not
    /// persisted, so a fresh launch never opens straight onto a wall of games the user hid.</summary>
    [ObservableProperty]
    private bool _showHiddenGames;

    /// <summary>Whether any game is currently hidden - drives the toolbar toggle's visibility, since
    /// there's nothing useful for it to reveal when nothing is hidden.</summary>
    [ObservableProperty]
    private bool _hasHiddenGames;

    /// <summary>ShowHiddenGames AND HasHiddenGames - the section itself should only appear once both
    /// the user asked to see it and there's actually something in it.</summary>
    [ObservableProperty]
    private bool _showHiddenSection;

    [ObservableProperty]
    private string _steamGridDbApiKey = string.Empty;

    [ObservableProperty]
    private bool _vibrantBackground = true;

    [ObservableProperty]
    private bool _minimizeToTrayWhileGaming = true;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private bool _detectSteam = true;

    [ObservableProperty]
    private bool _detectEpic = true;

    [ObservableProperty]
    private bool _detectGog = true;

    [ObservableProperty]
    private bool _detectXbox = true;

    [ObservableProperty]
    private bool _detectEa = true;

    [ObservableProperty]
    private bool _detectUbisoft = true;

    [ObservableProperty]
    private bool _detectBattleNet = true;

    [ObservableProperty]
    private bool _detectRockstar = true;

    [ObservableProperty]
    private bool _detectAmazonGames = true;

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

    // Same launcher-exe icon extraction the game card platform badges already use, so the sidebar
    // shows each launcher's real logo when it's installed on this PC - null (falls back to a
    // letter badge in the view) for whichever ones aren't. Xbox has no equivalent property: its
    // MSIX package icon can never be extracted by path, on any PC, so the view renders the
    // hardcoded real Xbox logo (Assets\XboxLogo.png) for that row instead of attempting extraction
    // at all.
    public BitmapImage? SteamIcon => PlatformIconService.GetIcon(GameSource.Steam);
    public BitmapImage? EpicIcon => PlatformIconService.GetIcon(GameSource.Epic);
    public BitmapImage? GogIcon => PlatformIconService.GetIcon(GameSource.Gog);
    public BitmapImage? EaIcon => PlatformIconService.GetIcon(GameSource.Ea);
    public BitmapImage? UbisoftIcon => PlatformIconService.GetIcon(GameSource.Ubisoft);
    public BitmapImage? BattleNetIcon => PlatformIconService.GetIcon(GameSource.BattleNet);
    public BitmapImage? RockstarIcon => PlatformIconService.GetIcon(GameSource.Rockstar);
    public BitmapImage? AmazonGamesIcon => PlatformIconService.GetIcon(GameSource.AmazonGames);

    // Drives whether each sidebar source row shows up at all - only once a scan has actually found
    // a game from that launcher, so a PC without (say) Battle.net installed never sees a Battle.net
    // toggle it could never turn anything on. Set in ApplyFilter from the full, unfiltered scan
    // result, not from the Detect-filtered Games list, so disabling a source can never hide its own
    // row - there'd be no way back on.
    [ObservableProperty]
    private bool _hasSteamGames;

    [ObservableProperty]
    private bool _hasEpicGames;

    [ObservableProperty]
    private bool _hasGogGames;

    [ObservableProperty]
    private bool _hasXboxGames;

    [ObservableProperty]
    private bool _hasEaGames;

    [ObservableProperty]
    private bool _hasUbisoftGames;

    [ObservableProperty]
    private bool _hasBattleNetGames;

    [ObservableProperty]
    private bool _hasRockstarGames;

    [ObservableProperty]
    private bool _hasAmazonGames;

    /// <summary>Hides the "SOURCES" header itself once every individual row above has already
    /// hidden itself - otherwise a PC with no detected launchers shows a floating label over nothing.</summary>
    [ObservableProperty]
    private bool _hasAnySourceGames;

    public ObservableCollection<GameEntry> Games { get; } = new();

    public ObservableCollection<GameEntry> FavoriteGames { get; } = new();

    public ObservableCollection<GameEntry> HiddenGames { get; } = new();

    public ObservableCollection<WatchedFolder> WatchedFolders { get; } = new();

    /// <summary>Only the drives games were actually detected on, not every drive on the system - a
    /// user with a 6-drive PC doesn't need to see the 4 that hold nothing but Windows and documents.
    /// Rebuilt from the game list itself (not the whole DriveInfo.GetDrives() set) so it's always
    /// exactly the drives relevant to this library.</summary>
    public ObservableCollection<DriveSpaceInfo> Drives { get; } = new();

    /// <summary>Hides the sidebar's "DRIVES" header when there's nothing to show it above.</summary>
    [ObservableProperty]
    private bool _hasDrives;

    public List<SortOptionItem> SortOptions { get; } =
    [
        new("Name (A-Z)", GameSortOption.NameAsc),
        new("Name (Z-A)", GameSortOption.NameDesc),
        new("Source", GameSortOption.Source),
        new("Favorites First", GameSortOption.FavoritesFirst),
        new("Recently Added", GameSortOption.RecentlyAdded),
    ];

    public LibraryViewModel() : this(new SettingsService(), new PendingUpdateNotesService())
    {
    }

    /// <summary>Lets tests point settings and pending-update-notes storage at isolated temp
    /// directories instead of the real %AppData%\GameLauncher - production code always uses the
    /// parameterless constructor above. Same idea as SettingsService's/PendingUpdateNotesService's own
    /// testable constructor overloads. There is deliberately no settings-only overload: a shortcut
    /// that isolated settings but left pending-update-notes defaulting to the real path is exactly how
    /// this suite ended up able to delete a real user's pending marker (see
    /// LibraryViewModelRunningGameTests, which doesn't care about update notes at all but still must
    /// not touch real %AppData%).</summary>
    internal LibraryViewModel(SettingsService settingsService, PendingUpdateNotesService pendingUpdateNotesService)
    {
        _settingsService = settingsService;
        _pendingUpdateNotesService = pendingUpdateNotesService;
        _settings = _settingsService.Load();
        foreach (var folder in _settings.WatchedFolders)
            WatchedFolders.Add(folder);
        _steamGridDbApiKey = _settings.SteamGridDbApiKey ?? string.Empty;
        _vibrantBackground = _settings.VibrantBackground;
        _minimizeToTrayWhileGaming = _settings.MinimizeToTrayWhileGaming;
        _isSidebarExpanded = _settings.SidebarExpanded;
        _detectSteam = _settings.DetectSteam;
        _detectEpic = _settings.DetectEpic;
        _detectGog = _settings.DetectGog;
        _detectXbox = _settings.DetectXbox;
        _detectEa = _settings.DetectEa;
        _detectUbisoft = _settings.DetectUbisoft;
        _detectBattleNet = _settings.DetectBattleNet;
        _detectRockstar = _settings.DetectRockstar;
        _detectAmazonGames = _settings.DetectAmazonGames;
        _checkForUpdates = _settings.CheckForUpdates;

        Logger.WriteEnvironment(_settings);
        RefreshShortcutState();

        // Checked unconditionally, regardless of the CheckForUpdates toggle: the marker only ever
        // exists because DownloadUpdateAsync itself just applied an update and restarted, which is
        // an explicit action the user already took, not a background check they may have opted out
        // of. A non-destructive read, not a consume - the marker stays on disk until
        // AcknowledgeWhatsNew confirms the dialog was actually shown and closed, so a crash or forced
        // shutdown between here and then just tries again next launch instead of losing the notes.
        var pendingNotes = _pendingUpdateNotesService.TryRead();
        if (pendingNotes is not null && string.Equals(pendingNotes.Version, AppInfo.Version, StringComparison.OrdinalIgnoreCase))
        {
            WhatsNewVersion = pendingNotes.Version;
            WhatsNewNotes = string.IsNullOrWhiteSpace(pendingNotes.NotesMarkdown)
                ? "No release notes were provided for this update."
                : pendingNotes.NotesMarkdown;
            ShowWhatsNew = true;
        }
        else if (pendingNotes is not null)
        {
            // Present but for some other version - either ApplyUpdatesAndRestart failed right after
            // Save (this is still the old version) or the marker is otherwise stale/malformed (an
            // empty Version never equals a real AppInfo.Version, so that case lands here too). It will
            // never legitimately match, so unlike the case above there's nothing to wait for - discard
            // it now rather than let it linger and re-check forever.
            _pendingUpdateNotesService.Discard();
        }

        // Fire-and-forget by design, not awaited from the constructor: UpdateService is fully
        // defensive internally (see its remarks) and never throws, so there's nothing here for a
        // caller to observe or react to beyond the UpdateAvailable/AvailableUpdateVersion properties
        // it sets on success. Runs once per app launch, not on every rescan - unlike the library scan,
        // there's nothing to gain from checking again until the app restarts.
        if (_checkForUpdates)
            _ = CheckForUpdateInBackgroundAsync();
    }

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

    /// <summary>Marks a game as the one session-watching currently tracks - called by MainWindow when
    /// a launch starts, never by setting GameEntry.IsRunning directly, so the update guard's
    /// _runningGameId can never drift out of sync with the badge. Returns a session id the caller
    /// must hold onto and pass back to MarkGameNotRunning for this exact session - see
    /// _runningSessionId's remarks for why that matters.</summary>
    public int MarkGameRunning(GameEntry game)
    {
        var sessionId = ++_sessionCounter;
        _runningGameId = game.Id;
        _runningSessionId = sessionId;
        game.IsRunning = true;
        return sessionId;
    }

    /// <summary>Clears tracking for the session identified by sessionId once GameSessionWatcher
    /// confirms the game exited (or it was superseded by a newer launch) - see MarkGameRunning. Two
    /// cases:
    /// - sessionId still owns the active session (a genuine, non-superseded exit): clears
    ///   _runningGameId/_runningSessionId and the badge, both on game itself and on whichever entry
    ///   in the *current* library actually shares its id (a rescan replaces every GameEntry wholesale,
    ///   so that may be a different instance than game itself).
    /// - sessionId has been superseded by a newer session: tracking state is left untouched (the newer
    ///   session already owns it), and the badge is cleared *only* if the newer session is for a
    ///   different game id. If it's the same id - a relaunch of this exact game, which is what makes
    ///   the superseded and current sessions share a game id despite being different sessions - the
    ///   badge belongs to that newer session and must be left alone, whether game is a stale pre-
    ///   refresh instance or (see MarkGameRunning's remarks) the very same instance reused for the new
    ///   session.</summary>
    public void MarkGameNotRunning(GameEntry game, int sessionId)
    {
        if (sessionId == _runningSessionId)
        {
            _runningGameId = null;
            _runningSessionId = 0;
            ClearBadge(game);
            return;
        }

        if (_runningGameId != game.Id)
            ClearBadge(game);
    }

    /// <summary>Clears game's own badge, plus whichever entry in the *current* library shares its id
    /// if that's a different instance (see MarkGameNotRunning).</summary>
    private void ClearBadge(GameEntry game)
    {
        game.IsRunning = false;

        var current = _allGames.FirstOrDefault(g => g.Id == game.Id);
        if (current is not null && !ReferenceEquals(current, game))
            current.IsRunning = false;
    }

    /// <summary>Reapplies the running badge to whichever entry in _allGames matches the tracked
    /// session - called after RefreshAsync replaces every GameEntry wholesale, so an active session's
    /// badge doesn't vanish just because a rescan happened mid-game. The update guard itself never
    /// needs this: DownloadUpdateAsync checks _runningGameId directly, which isn't tied to any
    /// particular GameEntry instance.</summary>
    private void ReapplyRunningBadge()
    {
        if (_runningGameId is not { } runningId)
            return;

        var running = _allGames.FirstOrDefault(g => g.Id == runningId);
        if (running is not null)
            running.IsRunning = true;
    }

    /// <summary>The one place _allGames is ever replaced wholesale - RefreshAsync (a real scan) and
    /// SimulateRefreshResult (the test seam standing in for one) both route through this, so the test
    /// seam can never drift from what a real refresh actually does to running-game tracking.</summary>
    private void ReplaceAllGames(List<GameEntry> games)
    {
        _allGames = games;
        ReapplyRunningBadge();
    }

    /// <summary>Test seam: applies exactly the _allGames-replacement + running-badge-reconciliation a
    /// real scan performs (see ReplaceAllGames), without requiring GameScannerService's real
    /// filesystem/registry work behind it. Production code only ever reaches ReplaceAllGames via
    /// RefreshAsync. See LibraryViewModelRunningGameTests.</summary>
    internal void SimulateRefreshResult(List<GameEntry> games) => ReplaceAllGames(games);

    /// <summary>Exposed for tests that need to assert on running-game tracking directly - exercising
    /// it through DownloadUpdateCommand would also require faking a real update check just to
    /// populate _pendingUpdate first. See LibraryViewModelRunningGameTests.</summary>
    internal string? RunningGameId => _runningGameId;

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
        if (_runningGameId is not null)
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

    partial void OnDetectSteamChanged(bool value) => SaveDetectSetting(v => _settings.DetectSteam = v, value);
    partial void OnDetectEpicChanged(bool value) => SaveDetectSetting(v => _settings.DetectEpic = v, value);
    partial void OnDetectGogChanged(bool value) => SaveDetectSetting(v => _settings.DetectGog = v, value);
    partial void OnDetectXboxChanged(bool value) => SaveDetectSetting(v => _settings.DetectXbox = v, value);
    partial void OnDetectEaChanged(bool value) => SaveDetectSetting(v => _settings.DetectEa = v, value);
    partial void OnDetectUbisoftChanged(bool value) => SaveDetectSetting(v => _settings.DetectUbisoft = v, value);
    partial void OnDetectBattleNetChanged(bool value) => SaveDetectSetting(v => _settings.DetectBattleNet = v, value);
    partial void OnDetectRockstarChanged(bool value) => SaveDetectSetting(v => _settings.DetectRockstar = v, value);
    partial void OnDetectAmazonGamesChanged(bool value) => SaveDetectSetting(v => _settings.DetectAmazonGames = v, value);

    // Scanning always runs for every source now (see GameScannerService), so a toggle only needs to
    // re-filter the already-scanned library, not trigger a fresh scan - instant instead of a
    // multi-second rescan for what's really just a visibility change.
    private void SaveDetectSetting(Action<bool> apply, bool value)
    {
        apply(value);
        _settingsService.Save(_settings);
        ApplyFilter();
    }

    partial void OnMinimizeToTrayWhileGamingChanged(bool value)
    {
        _settings.MinimizeToTrayWhileGaming = value;
        _settingsService.Save(_settings);
    }

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        _settings.SidebarExpanded = value;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void ToggleShowHidden() => ShowHiddenGames = !ShowHiddenGames;

    partial void OnSteamGridDbApiKeyChanged(string value)
    {
        _settings.SteamGridDbApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settingsService.Save(_settings);
    }

    /// <summary>Raised when the backdrop preference changes so open windows can re-apply it live.</summary>
    public event Action<bool>? VibrantBackgroundChanged;

    partial void OnVibrantBackgroundChanged(bool value)
    {
        _settings.VibrantBackground = value;
        _settingsService.Save(_settings);
        VibrantBackgroundChanged?.Invoke(value);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortOptionChanged(GameSortOption value) => ApplyFilter();

    partial void OnShowHiddenGamesChanged(bool value) => ShowHiddenSection = value && HasHiddenGames;

    /// <summary>Raised at the end of a successful scan, once Games/FavoriteGames/HiddenGames/Drives
    /// all reflect the new results - MainWindow uses it to grow the window to fit the sidebar's
    /// actual content instead of leaving the user to resize it by hand every time a new drive or
    /// launcher shows up.</summary>
    public event Action? LibraryRefreshed;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Cancel-and-replace: AddFolder/RemoveFolder and a manual Refresh can each trigger a scan
        // while a previous one is still running (e.g. adding a folder before the initial startup scan
        // finishes). Both used to run to completion and write _allGames/Games/Drives/IsLoading in
        // whichever order they happened to finish, so a slower-but-older scan could silently overwrite
        // a newer one's results. Cancelling the previous token here, and only ever applying the
        // results of whichever scan is current when it completes (see the finally block below), means
        // exactly one scan's output ever reaches the UI.
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        IsLoading = true;
        StatusText = "Scanning...";
        try
        {
            var result = await _scannerService.ScanAllAsync(_settings, token);

            // GameScannerService checks the token internally too, but only cooperatively - a
            // cancelled scan can still be mid-flight on a background thread pool thread when this
            // await resumes (e.g. blocked in a synchronous cover-art HTTP call) and, depending on
            // exactly where cancellation landed, can complete "successfully" with a result that's
            // already stale. This is the one check that actually matters: nothing below may touch
            // _allGames, _settings, the Games/FavoriteGames/HiddenGames/Drives collections, StatusText,
            // or LibraryRefreshed unless this call is still the current refresh.
            if (!ReferenceEquals(_refreshCts, cts))
                return;

            ReplaceAllGames(result.Games);

            // Merged here, on the UI thread, rather than written straight into _settings.Overrides
            // from the background scan thread - see GameScannerService.ScanAllAsync's remarks on why
            // that's a real Dictionary-corruption risk, not just a staleness one.
            foreach (var (id, dateAdded) in result.NewDateAddedByGameId)
            {
                if (!_settings.Overrides.TryGetValue(id, out var over))
                {
                    over = new GameOverride();
                    _settings.Overrides[id] = over;
                }
                over.DateAdded ??= dateAdded;
            }

            // Same idea for any watched folder WatchedFolderResolver healed during this scan (a
            // first-time volume anchor, or a re-derived path after a drive-letter change) - the scan
            // only ever touched its own private copies (see GameScannerService.ScanAllAsync), so the
            // healed values are applied to the live, UI-bound object here instead. Matched by the path
            // the folder had when this scan started, since the healed Path may itself have changed.
            foreach (var healed in result.HealedWatchedFolders)
            {
                var live = _settings.WatchedFolders.FirstOrDefault(w =>
                    string.Equals(w.Path, healed.OriginalPath, StringComparison.OrdinalIgnoreCase));
                if (live is null)
                    continue; // removed while this scan was running - nothing to heal anymore

                live.Path = healed.HealedPath;
                live.VolumeSerialNumber = healed.VolumeSerialNumber;
                live.RelativePath = healed.RelativePath;
            }

            // Re-applied here, on the UI thread, rather than trusting the Hidden/Favorite/CustomName
            // GameScannerService already baked into each GameEntry: those came from an Overrides
            // snapshot taken when this scan started, and ToggleFavorite/ToggleHidden stay usable the
            // whole time a scan is running. Without this, a toggle made mid-scan would visibly revert
            // the instant this scan's results are published, even though the live settings (and so a
            // later refresh) were correct the entire time.
            foreach (var game in _allGames)
            {
                _settings.Overrides.TryGetValue(game.Id, out var over);
                if (!string.IsNullOrWhiteSpace(over?.CustomName))
                    game.Name = over.CustomName;
                game.Hidden = over?.Hidden ?? false;
                game.Favorite = over?.Favorite ?? false;
            }

            _settingsService.Save(_settings); // persists the DateAdded/watched-folder changes merged above
            ApplyFilter();
            RefreshDrives();

            // Count from the filtered collections, not _allGames directly - scanning now always
            // covers every source (see GameScannerService), so _allGames includes games from
            // sources the user has toggled off. Games/FavoriteGames already reflect that filter.
            var shown = Games.Count + FavoriteGames.Count;
            var totalFound = _allGames.Count(g => !g.Hidden);
            var shownWord = shown == 1 ? "game" : "games";
            StatusText = shown == totalFound
                ? $"{shown} {shownWord} found"
                : $"{shown} of {totalFound} {shownWord} shown ({totalFound - shown} hidden by disabled sources)";

            LibraryRefreshed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh - that one owns IsLoading/StatusText/the results now.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Scan failed.", ex);

            // A superseded scan can still fault after being cancelled (it's cooperative, not
            // instant) - without this check, an old scan's failure could stomp "Scan failed" over
            // whatever the newer, still-running refresh has already put in StatusText.
            if (ReferenceEquals(_refreshCts, cts))
                StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            // Only the still-current refresh clears IsLoading/trims memory - a superseded refresh
            // reaching this point after being cancelled must not stomp on the state of whichever
            // refresh superseded it and is still in flight.
            if (ReferenceEquals(_refreshCts, cts))
            {
                IsLoading = false;

                // A scan is a burst of allocation (file/registry walking, decoding cover art) and the
                // app goes idle straight after. Hand back what that burst left resident.
                MemoryTrimmer.Trim("after scan");
            }
        }
    }

    // Async (and awaiting the refresh below) rather than firing RefreshCommand and discarding the
    // task: a discarded task's exceptions only ever surface via App.xaml.cs's global
    // UnobservedTaskException logging, well after the fact and with no way to reflect the failure in
    // this command's own state. Awaiting it here means a failure is observed at the actual call site,
    // and the generated AddFolderCommand/RemoveFolderCommand stay IAsyncRelayCommand - the same
    // ICommand-compatible type XAML already binds to (see MainWindow.xaml / SettingsWindow.xaml).
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select a games folder" };
        if (dialog.ShowDialog() != true)
            return;

        if (WatchedFolders.Any(w => string.Equals(w.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
            return;

        var watched = new WatchedFolder { Path = dialog.FolderName };
        WatchedFolderResolver.CaptureAnchor(watched); // so a later drive-letter change can self-heal

        WatchedFolders.Add(watched);
        _settings.WatchedFolders.Add(watched);
        _settingsService.Save(_settings);

        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(WatchedFolder? folder)
    {
        if (folder is null)
            return;

        WatchedFolders.Remove(folder);
        _settings.WatchedFolders.Remove(folder);
        _settingsService.Save(_settings);

        await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDir);
            Process.Start(new ProcessStartInfo(Logger.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            StatusText = $"Couldn't open logs folder: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateDesktopShortcut))]
    private void CreateDesktopShortcut()
    {
        try
        {
            ShortcutService.CreateDesktopShortcut();
            StatusText = "Desktop shortcut created";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                        or COMException)
        {
            Logger.Error("Couldn't create desktop shortcut.", ex);
            StatusText = $"Couldn't create shortcut: {ex.Message}";
            return;
        }

        RefreshShortcutState();
    }

    private void RefreshShortcutState()
    {
        if (ShortcutService.DesktopShortcutExists())
        {
            DesktopShortcutButtonText = "Shortcut Already on Desktop";
            CanCreateDesktopShortcut = false;
        }
        else
        {
            DesktopShortcutButtonText = "Create Desktop Shortcut";
            CanCreateDesktopShortcut = true;
        }
    }

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

        try
        {
            Logger.Info($"Launching '{game.Name}' ({game.Source}) - {game.LaunchUri ?? game.ExecutablePath}");
            var started = GameLauncherService.Launch(game);
            GameLaunched?.Invoke(game, started);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            Logger.Error($"Failed to launch '{game.Name}'.", ex);
            StatusText = $"Failed to launch {game.Name}: {ex.Message}";
        }
    }

    /// <summary>Rebuilds the Drives list from the drive letters games actually live on. Re-run after
    /// every scan, not just once, since free space changes from other activity even when the set of
    /// drives games live on doesn't.</summary>
    private void RefreshDrives()
    {
        Drives.Clear();

        var driveLetters = _allGames
            .Select(g => Path.GetPathRoot(g.InstallDir))
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
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Warn($"Couldn't read space for drive '{root}'.", ex);
            }
        }

        HasDrives = Drives.Count > 0;
    }

    private bool IsSourceEnabled(GameSource source) => source switch
    {
        GameSource.Steam => DetectSteam,
        GameSource.Epic => DetectEpic,
        GameSource.Gog => DetectGog,
        GameSource.Xbox => DetectXbox,
        GameSource.Ea => DetectEa,
        GameSource.Ubisoft => DetectUbisoft,
        GameSource.BattleNet => DetectBattleNet,
        GameSource.Rockstar => DetectRockstar,
        GameSource.AmazonGames => DetectAmazonGames,
        _ => true, // Manual folders have no toggle - always shown.
    };

    private void ApplyFilter()
    {
        // Computed from the full, un-filtered scan result (not the Detect-filtered list below), so
        // disabling a source can never make its own sidebar row disappear.
        HasSteamGames = _allGames.Any(g => g.Source == GameSource.Steam);
        HasEpicGames = _allGames.Any(g => g.Source == GameSource.Epic);
        HasGogGames = _allGames.Any(g => g.Source == GameSource.Gog);
        HasXboxGames = _allGames.Any(g => g.Source == GameSource.Xbox);
        HasEaGames = _allGames.Any(g => g.Source == GameSource.Ea);
        HasUbisoftGames = _allGames.Any(g => g.Source == GameSource.Ubisoft);
        HasBattleNetGames = _allGames.Any(g => g.Source == GameSource.BattleNet);
        HasRockstarGames = _allGames.Any(g => g.Source == GameSource.Rockstar);
        HasAmazonGames = _allGames.Any(g => g.Source == GameSource.AmazonGames);
        HasAnySourceGames = HasSteamGames || HasEpicGames || HasGogGames || HasXboxGames || HasEaGames
            || HasUbisoftGames || HasBattleNetGames || HasRockstarGames || HasAmazonGames;

        IEnumerable<GameEntry> filtered = _allGames.Where(g => !g.Hidden && IsSourceEnabled(g.Source));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(g =>
                g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        filtered = SortOption switch
        {
            GameSortOption.NameDesc => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.Source => filtered.OrderBy(g => g.Source)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.FavoritesFirst => filtered.OrderByDescending(g => g.Favorite)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.RecentlyAdded => filtered.OrderByDescending(g => g.DateAdded),
            _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Favourites are pulled into their own section, so they aren't repeated in the main grid.
        var ordered = filtered.ToList();

        FavoriteGames.Clear();
        foreach (var game in ordered.Where(g => g.Favorite))
            FavoriteGames.Add(game);

        Games.Clear();
        foreach (var game in ordered.Where(g => !g.Favorite))
            Games.Add(game);

        HasFavorites = FavoriteGames.Count > 0;

        // Hidden games: same source filter and search text as everything else, but sourced from
        // g.Hidden directly rather than the `filtered` sequence above, since that sequence already
        // excludes them by design (they must never leak into Games/FavoriteGames).
        IEnumerable<GameEntry> hidden = _allGames.Where(g => g.Hidden && IsSourceEnabled(g.Source));
        if (!string.IsNullOrWhiteSpace(SearchText))
            hidden = hidden.Where(g => g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        HiddenGames.Clear();
        foreach (var game in hidden.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            HiddenGames.Add(game);

        HasHiddenGames = HiddenGames.Count > 0;
        ShowHiddenSection = ShowHiddenGames && HasHiddenGames;

        // Driven by what's actually on screen (Games + FavoriteGames), not the raw scan count -
        // toggling off every source leaves _allGames non-empty but nothing visible, and the empty
        // state (with its "Scan Now" / "Add Folder" actions) should show exactly when the grid is
        // genuinely blank, whatever the reason. HiddenGames counts too: a library that's entirely
        // hidden games shouldn't tell the user to go scan or add a folder - it should just show the
        // (reachable via the header toggle) Hidden section instead.
        HasNoGames = Games.Count == 0 && FavoriteGames.Count == 0 && HiddenGames.Count == 0;
        LibraryHeaderText = $"My Library ({Games.Count} {(Games.Count == 1 ? "Game" : "Games")})";
    }
}
