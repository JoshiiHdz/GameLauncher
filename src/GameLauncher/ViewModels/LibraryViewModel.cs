using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using Microsoft.Win32;
using Velopack;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly GameScannerService _scannerService;
    private readonly IgdbCredentialStore _credentialStore;

    /// <summary>Test-only visibility, so a test can prove the store this view model reads credentials from
    /// lives under the SettingsService directory it was handed (see the constructor's remarks) rather than
    /// the real one.</summary>
    internal IgdbCredentialStore CredentialStoreForTest => _credentialStore;
    private readonly UpdateService _updateService = new();
    private readonly PendingUpdateNotesService _pendingUpdateNotesService;
    private readonly AppSettings _settings;
    private List<GameEntry> _allGames = new();
    private CancellationTokenSource? _refreshCts;

    /// <summary>Read once at startup from AppSettings.EnableWindowExitDiagnostics - no UI toggle yet (see
    /// its own remarks), so a settings.json edit needs a restart to take effect. Passed to
    /// GameSessionOrchestrator, which is the only thing that ever acts on it.</summary>
    public bool EnableWindowExitDiagnostics { get; }

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

    // Every session's own CURRENT tracked game id, from MarkGameRunning until its own MarkGameNotRunning
    // call removes it - not just the single "whichever session is canonical right now" pair above.
    // Needed for a real, confirmed case: a session can be MERGED (see ReconcileRunningGameId - a Manual
    // entry's id folded into a launcher-detected one mid-play, e.g. EA's "A Way Out") and THEN
    // superseded by a newer, unrelated session before its own cleanup call ever runs. By the time that
    // cleanup arrives, _runningGameId/_runningSessionId above have already moved on to the newer
    // session, so the merged session's own reconciled identity would otherwise be lost - MarkGameNotRunning
    // would fall back to the caller's own (stale, pre-merge) GameEntry.Id, which no longer names anything
    // in the current library, and the merged-into entry's badge would never get cleared. Every session
    // that starts here gets an entry; ReconcileRunningGameId keeps ALL of them (not just the current one)
    // up to date as merges happen, and MarkGameNotRunning removes its own entry exactly once, when that
    // session is finally cleaned up.
    private readonly Dictionary<int, string> _sessionGameIds = new();

    // Guards overlapping Apply/Reset for the SAME game - a second request for a game already mid-commit
    // is rejected outright rather than allowed to race the first's revision bump/asset write. This alone
    // does not protect the shared settings.json against two DIFFERENT games committing "concurrently" -
    // that's guaranteed instead by CommitArtworkChange being fully synchronous (no await inside it),
    // which WPF's single UI-thread dispatcher already serializes on its own; see CommitArtworkChange's
    // own remarks.
    private readonly HashSet<string> _artworkOperationsInFlight = new();

    // Test-only seams - production always leaves these null, in which case every artwork code path
    // resolves to the exact same real dependency (ArtworkAssetStore's own default directory,
    // IconService.GetIcon, CoverArtService.Apply) it would use without this indirection at all. Needed
    // because ArtworkAssetStore/CoverArtService have no other injectable seam reachable from
    // LibraryViewModel: without these, artwork tests would have to write into real %AppData%\GameLauncher
    // and Reset tests would depend on whether this checkout happens to have a real embedded SteamGridDB
    // key (default-api-key.txt) - both real, confirmed problems in an earlier version of this test suite.
    internal string? AssetStoreDirOverrideForTest { get; set; }
    internal Func<GameEntry, BitmapImage?>? IconFallbackForTest { get; set; }
    internal Func<GameEntry, string?, ArtworkSelection?>? AutomaticCoverArtLookupForTest { get; set; }

    /// <summary>Test-only: invoked (and awaited) by ApplyScanResultAsync right after its PREPARE phase
    /// (off-thread cover decode) completes and before PUBLISH starts - the exact window in which a real
    /// concurrent action (a Change Cover/Reset commit, a ToggleFavorite click, a superseding refresh)
    /// could land in production. Lets tests deterministically exercise that window without real threading
    /// - see LibraryViewModelArtworkTests' controlled-interleaving tests. Always null in production.
    /// Invoked unconditionally, even when PREPARE had nothing to decode (no real await occurred) - a test
    /// simulating a plain UI action during a scan publish doesn't require an artwork decode to be in
    /// flight to be meaningful.</summary>
    internal Func<Task>? DuringCoverDecodeForTest { get; set; }

    /// <summary>Test-only: invoked by ApplyLocalCoverImageAsync right after the newly-staged asset file
    /// is written, before CommitArtworkChange applies it - lets a test delete/corrupt that file to prove
    /// CommitArtworkChange's `preparedIcon` path actually displays the already-decoded, in-memory bitmap
    /// from validation rather than re-reading the file it's about to hand this callback the chance to
    /// remove. Always null in production.</summary>
    internal Action<string>? AfterAssetWrittenForTest { get; set; }

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

    /// <summary>How far along the running scan is, 0-100, for the progress bar (see ScanProgress). Meaningless while nothing is scanning.</summary>
    [ObservableProperty]
    private double _scanProgressPercent;

    /// <summary>What the running scan is doing right now ("Identifying games... 12 of 40 - Apex Legends"), shown under the bar.</summary>
    [ObservableProperty]
    private string _scanProgressText = "";

    /// <summary>True until the scan reports its first step, so the bar animates instead of sitting at an empty 0%.</summary>
    [ObservableProperty]
    private bool _isScanIndeterminate = true;

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
    private string _emptyStateTitle = "No games found yet";

    [ObservableProperty]
    private string _emptyStateDescription = "Games are auto-detected from Steam, Epic, GOG and more, or add a folder to scan.";

    [ObservableProperty]
    private bool _showEmptyDiscoveryActions = true;

    [ObservableProperty]
    private string _libraryHeaderText = "All games";

    /// <summary>The count line under the grid heading ("6 games").</summary>
    [ObservableProperty]
    private string _librarySubheaderText = "";

    /// <summary>Status-bar left side ("6 of 6 games shown").</summary>
    [ObservableProperty]
    private string _footerCountText = "";

    /// <summary>The horizontal "Recently played" strip above the grid - only games with real tracked
    /// sessions, newest first. See ApplyFilter.</summary>
    public ObservableCollection<GameEntry> RecentlyPlayedGames { get; } = new();

    [ObservableProperty]
    private bool _hasRecentlyPlayed;

    /// <summary>Drives the Favorites section and its separator - both vanish when nothing is starred.</summary>
    [ObservableProperty]
    private bool _hasFavorites;

    /// <summary>The "Jump back in" hero's subject: whichever visible game was added to the library most
    /// recently. DateAdded is real, persisted data (unlike playtime, which this app has no way to know
    /// for a game that was already installed before being added here) - "recently added" is therefore
    /// the one honest "which game matters right now" signal available, not a stand-in for "most
    /// played." Null only when the library is genuinely empty (recomputed every ApplyFilter pass, so
    /// it already respects the source toggles, drive filter, and search text the rest of the grid
    /// does).</summary>
    [ObservableProperty]
    private GameEntry? _featuredGame;

    /// <summary>FeaturedGame != null, as a plain bool - XAML Visibility needs BoolToVisibilityConverter
    /// for the "visible when true" polarity the hero wants, which NullToVisibilityConverter (used
    /// elsewhere for the opposite "visible when null" case, e.g. the fallback icon glyph) doesn't give.</summary>
    [ObservableProperty]
    private bool _hasFeaturedGame;

    /// <summary>A soft accent colour sampled from FeaturedGame's own cover art, for the hero's ambient
    /// background tint - see DominantColorExtractor. Falls back to the app's own accent colour (never
    /// null) when there's no cover yet to sample (a fallback exe icon, or still loading).</summary>
    private static readonly Color DefaultHeroAccentColor = (Color)ColorConverter.ConvertFromString("#FF6B86")!;

    [ObservableProperty]
    private Color _heroAccentColor = DefaultHeroAccentColor;

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

    /// <summary>Expanded sidebar (labels, launcher switches) vs. the collapsed icon rail. Persisted.</summary>
    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        _settings.SidebarExpanded = value;
        _settingsService.Save(_settings);
    }

    /// <summary>The launchers found on this PC, as one list for both sidebar layouts. Built in the
    /// constructor (it closes over the Detect properties); entries show only once a scan finds games.</summary>
    public IReadOnlyList<SourceToggleItem> SourceItems { get; private set; } = Array.Empty<SourceToggleItem>();

    private IReadOnlyList<SourceToggleItem> BuildSourceItems() => new[]
    {
        new SourceToggleItem(GameSource.Steam, "Steam", "S", () => DetectSteam, v => DetectSteam = v),
        new SourceToggleItem(GameSource.Epic, "Epic Games", "E", () => DetectEpic, v => DetectEpic = v),
        new SourceToggleItem(GameSource.Gog, "GOG", "G", () => DetectGog, v => DetectGog = v),
        new SourceToggleItem(GameSource.Xbox, "Xbox", "X", () => DetectXbox, v => DetectXbox = v),
        new SourceToggleItem(GameSource.Ea, "EA app", "EA", () => DetectEa, v => DetectEa = v),
        new SourceToggleItem(GameSource.Ubisoft, "Ubisoft Connect", "U", () => DetectUbisoft, v => DetectUbisoft = v),
        new SourceToggleItem(GameSource.BattleNet, "Battle.net", "B", () => DetectBattleNet, v => DetectBattleNet = v),
        new SourceToggleItem(GameSource.Rockstar, "Rockstar Games", "R", () => DetectRockstar, v => DetectRockstar = v),
        new SourceToggleItem(GameSource.AmazonGames, "Amazon Games", "A", () => DetectAmazonGames, v => DetectAmazonGames = v),
    };

    /// <summary>Which rail view is active. Selecting one always clears any drive filter - they're two
    /// ways of scoping the same grid, and leaving a drive filter silently applied underneath a freshly
    /// clicked view would show an unexplained subset of it.</summary>
    [ObservableProperty]
    private LibraryView _selectedView = LibraryView.All;

    [ObservableProperty]
    private bool _isAllViewSelected = true;

    [ObservableProperty]
    private bool _isFavoritesViewSelected;

    [ObservableProperty]
    private bool _isRecentViewSelected;

    partial void OnSelectedViewChanged(LibraryView value)
    {
        IsAllViewSelected = value == LibraryView.All;
        IsFavoritesViewSelected = value == LibraryView.Favorites;
        IsRecentViewSelected = value == LibraryView.Recent;
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectView(string? view)
    {
        var parsed = view switch
        {
            "favorites" => LibraryView.Favorites,
            "recent" => LibraryView.Recent,
            _ => LibraryView.All,
        };

        // Order matters: clearing the drive re-runs ApplyFilter on its own, so setting the view second
        // means the pass that actually paints the grid is the one that already sees both changes.
        SelectedDriveLetter = null;
        if (SelectedView == parsed)
            ApplyFilter(); // same view re-clicked: still honour the drive clear above
        else
            SelectedView = parsed;
    }

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

    /// <summary>True once a scan has found a game from any launcher with a sidebar switch (computed from the full,
    /// unfiltered scan result in ApplyFilter, so switching a launcher off can never remove its own switch). Hides the
    /// LAUNCHERS header when there is nothing under it.</summary>
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
        new("Name A-Z", GameSortOption.NameAsc),
        new("Name Z-A", GameSortOption.NameDesc),
        new("Largest installed", GameSortOption.LargestInstalled),
        new("Most played", GameSortOption.MostPlayed),
        new("Recently added", GameSortOption.RecentlyAdded),
        new("Source", GameSortOption.Source),
        new("Favorites first", GameSortOption.FavoritesFirst),
    ];

    public LibraryViewModel() : this(new SettingsService(), new PendingUpdateNotesService(), WindowsStartupRegistration.ForCurrentUser())
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
    internal LibraryViewModel(SettingsService settingsService, PendingUpdateNotesService pendingUpdateNotesService,
        IStartupRegistration? startupRegistration = null)
    {
        _startupRegistration = startupRegistration;
        _settingsService = settingsService;
        _pendingUpdateNotesService = pendingUpdateNotesService;
        // Derived from the SettingsService's own directory, never defaulted independently - the same
        // reason there is no settings-only overload above: isolating settings must isolate everything
        // else this class reads or writes on disk, including the IGDB credential file.
        _credentialStore = new IgdbCredentialStore(settingsService.DataDir);
        _scannerService = new GameScannerService(_credentialStore);
        _settings = _settingsService.Load();
        _startWithWindows = startupRegistration?.IsEnabled ?? false;
        foreach (var folder in _settings.WatchedFolders)
            WatchedFolders.Add(folder);
        _steamGridDbApiKey = _settings.SteamGridDbApiKey ?? string.Empty;
        _igdbClientId = _settings.IgdbClientId ?? string.Empty;
        _igdbSecretSaved = _credentialStore.HasSecret();
        _vibrantBackground = _settings.VibrantBackground;
        _minimizeToTrayWhileGaming = _settings.MinimizeToTrayWhileGaming;
        _trackExternalGames = _settings.TrackExternalGames;
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
        _isSidebarExpanded = _settings.SidebarExpanded;
        SourceItems = BuildSourceItems();

        EnableWindowExitDiagnostics = _settings.EnableWindowExitDiagnostics;

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
        ++_sessionActivityGeneration;
        StopPassiveTrackingForGame(game.Id);
        var sessionId = ++_sessionCounter;
        _runningGameId = game.Id;
        _runningSessionId = sessionId;
        _sessionGameIds[sessionId] = game.Id;
        _sessionStartedUtc[sessionId] = SessionClock();
        game.IsRunning = true;
        return sessionId;
    }

    /// <summary>Test seam: the clock play-time accumulation is measured against. Real time by default;
    /// a test can advance it deliberately instead of sleeping.</summary>
    internal Func<DateTime> SessionClock { get; set; } = () => DateTime.UtcNow;

    // Start timestamp per session id, so a session that gets superseded (or whose game is merged into
    // a different entry mid-play) still accumulates against the right id - same reasoning as
    // _sessionGameIds, which this mirrors entry-for-entry.
    private readonly Dictionary<int, DateTime> _sessionStartedUtc = new();

    /// <summary>Adds this session's elapsed time to the game's persisted total and stamps LastPlayed.
    /// Keyed by the session's CURRENT tracked id (not game.Id), so a mid-session rescan that merged
    /// this install into a different entry credits the surviving one - the same hazard
    /// MarkGameNotRunning's own remarks describe for the badge.</summary>
    private void RecordPlayTime(int sessionId, string trackedId)
    {
        if (!_sessionStartedUtc.Remove(sessionId, out var startedUtc))
            return;

        var elapsed = SessionClock() - startedUtc;

        // A negative span (clock change) or an implausibly short one isn't play time worth recording -
        // a failed launch that exits instantly would otherwise accumulate noise onto a real total.
        if (elapsed < TimeSpan.FromSeconds(10))
            return;

        AddTrackedPlayTime(trackedId, (long)elapsed.TotalSeconds, SessionClock());
    }

    private void AddTrackedPlayTime(string trackedId, long seconds, DateTime lastPlayedUtc)
    {
        if (!_settings.Overrides.TryGetValue(trackedId, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[trackedId] = over;
        }

        over.TotalPlaySeconds += seconds;
        over.LastPlayedUtc = lastPlayedUtc;
        _settingsService.Save(_settings);

        if (_allGames.FirstOrDefault(g => g.Id == trackedId) is { } entry)
        {
            entry.TotalPlaySeconds = over.TotalPlaySeconds;
            entry.LastPlayedUtc = over.LastPlayedUtc;
        }

        ApplyFilter(); // the hero and the Recently played row are both ordered by what just changed
    }

    /// <summary>Clears tracking for the session identified by sessionId once GameSessionWatcher
    /// confirms the game exited (or it was superseded by a newer launch) - see MarkGameRunning. Two
    /// cases:
    /// - sessionId still owns the active session (a genuine, non-superseded exit): clears
    ///   _runningGameId/_runningSessionId and the badge, both on game itself and on whichever entry
    ///   in the *current* library actually shares this session's CURRENT tracked id.
    /// - sessionId has been superseded by a newer session: tracking state is left untouched (the newer
    ///   session already owns it), and the badge is cleared *only* if the newer session is for a
    ///   different game id. If it's the same id - a relaunch of this exact game, which is what makes
    ///   the superseded and current sessions share a game id despite being different sessions - the
    ///   badge belongs to that newer session and must be left alone.
    ///
    /// Neither case clears by `game.Id` directly: `game` is whatever GameEntry instance the caller
    /// launched and has held onto ever since (MainWindow holds it for the whole session), whose own Id
    /// never changes - but if a rescan merged THIS session's game into a different surviving entry while
    /// it was still running (see ReconcileRunningGameId, a real confirmed case for EA's "A Way Out"),
    /// this session's tracked id was redirected to that NEW one. _sessionGameIds (not the single
    /// _runningGameId pair, which only ever reflects whichever session is CURRENTLY canonical) is what
    /// keeps that redirected identity available for THIS specific session's own cleanup, even after a
    /// newer, different session has already superseded it and moved _runningGameId on. A real, confirmed
    /// case this fixes: session A (Manual) gets merged into entry B (EA) mid-play, then session C (an
    /// unrelated game) starts before A's own exit is ever observed - clearing by game.Id (A's original,
    /// now-gone id) would find nothing in the current library, leaving B's badge stuck on forever.</summary>
    public void MarkGameNotRunning(GameEntry game, int sessionId)
    {
        ++_sessionActivityGeneration;
        var trackedId = _sessionGameIds.Remove(sessionId, out var id) ? id : game.Id;
        RecordPlayTime(sessionId, trackedId);

        if (sessionId == _runningSessionId)
        {
            _runningGameId = null;
            _runningSessionId = 0;
            ClearBadge(game, trackedId);
            return;
        }

        if (_runningGameId != trackedId)
            ClearBadge(game, trackedId);
    }

    /// <summary>Clears game's own badge, plus whichever entry in the *current* library shares
    /// `idToClear` if that's a different instance (see MarkGameNotRunning - `idToClear` is the
    /// session's CURRENT tracked id, which is not always game.Id).</summary>
    private void ClearBadge(GameEntry game, string? idToClear)
    {
        var stillObserved = _passiveSessions.Keys.Any(t => _sessionGameIds.GetValueOrDefault(t) == idToClear);
        game.IsRunning = stillObserved;

        var current = _allGames.FirstOrDefault(g => g.Id == idToClear);
        if (current is not null && !ReferenceEquals(current, game))
            current.IsRunning = stillObserved;
    }

    /// <summary>Reapplies the running badge to whichever entry in _allGames matches the tracked
    /// session - called after RefreshAsync replaces every GameEntry wholesale, so an active session's
    /// badge doesn't vanish just because a rescan happened mid-game. The update guard itself never
    /// needs this: DownloadUpdateAsync checks _runningGameId directly, which isn't tied to any
    /// particular GameEntry instance.</summary>
    private void ReapplyRunningBadge()
    {
        var active = _passiveSessions.Keys.Select(t => _sessionGameIds.GetValueOrDefault(t)).ToHashSet();
        if (_runningGameId is { } id) active.Add(id);
        foreach (var game in _allGames) game.IsRunning = active.Contains(game.Id);
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

    /// <summary>Test seam mirroring exactly what RefreshAsync itself does to establish ownership of the
    /// current refresh (cancel-and-replace _refreshCts) - lets a test drive ApplyScanResultAsync's
    /// `ownershipToken` parameter directly, and simulate a superseding refresh mid-decode, without
    /// needing a real GameScannerService scan behind it. Returns the new token.</summary>
    internal CancellationTokenSource SetRefreshOwnershipForTest()
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        return cts;
    }

    /// <summary>How many sessions are currently tracked in _sessionGameIds - every MarkGameRunning call
    /// adds one, every MarkGameNotRunning call (for that exact session) removes it. Exposed so tests can
    /// directly assert that a session's entry is genuinely retired, not merely that the badge looks
    /// right - a real, confirmed leak (MainWindow skipping cleanup entirely for a same-instance relaunch)
    /// left the badge looking correct while still leaking a stale map entry underneath it.</summary>
    internal int TrackedSessionCount => _sessionGameIds.Count;

    /// <summary>Migrates a superseded entry's override onto the surviving entry it was merged into -
    /// see GameScannerService.DeduplicateByInstallLocation (e.g. a Manual entry reconciled into a
    /// launcher-detected one for the same install, a real confirmed case for EA's "A Way Out"). Extracted
    /// as its own method (called from ApplyScanResult) so it can be exercised directly against a
    /// scripted id mapping in tests, without needing a real scan. ApplyScanResultAsync also feeds this
    /// the very same map for a ScanResult.LegacyIdRemap transition (a scanner's own detection-method
    /// change giving the SAME install a new id, not a within-scan merge) - the "old id's state moves onto
    /// the new id" logic below is identical either way.
    ///
    /// FIELD-LEVEL merge, not "loser wins outright" or "winner wins outright": a real, confirmed gap in
    /// an earlier version of this method removed the loser's override unconditionally, then only
    /// migrated it if the winner had NO override at all - but a winner CAN already have an override that
    /// is purely automatic metadata (GameScannerService's own NewDateAddedByGameId stamps a bare
    /// DateAdded-only GameOverride the first time any new id is seen, with no explicit user choice
    /// behind it at all), and that bare record was enough to block migration while the loser's own
    /// explicit Favorite/Hidden/CustomName was already gone. GameOverride's Favorite/Hidden are plain
    /// bools, though, with no way to represent "explicitly set to false" separately from "never
    /// touched" - so this can only ever ADD a true value or fill in an unset field, never take a true
    /// value AWAY from either side. That is a real, accepted limitation of the current data model (an
    /// explicit un-favorite on the winner, immediately followed by a merge with a loser that still has
    /// Favorite=true from its own history, would resurrect it) - not something this method can perfectly
    /// resolve without GameOverride itself becoming tri-state, which is a larger change than this fix
    /// warrants on its own.</summary>
    internal void MigrateMergedOverrides(Dictionary<string, string> mergedGameIds)
    {
        foreach (var (loserId, winnerId) in mergedGameIds)
        {
            if (!_settings.Overrides.Remove(loserId, out var loserOverride))
                continue; // nothing to migrate

            if (!_settings.Overrides.TryGetValue(winnerId, out var winnerOverride))
            {
                // Still bumped past its own prior value, not carried over as-is - see the ArtworkRevision
                // bump below for why a merge can never simply copy a revision number across untouched. If
                // exhausted, the loser's own revision is already at the terminal value (see
                // TryGetNextRevision) - adopted as-is rather than reused-but-relabeled; every future
                // artwork-touching operation on this survivor id will itself be safely rejected from here
                // on, exactly as it already would have been under the loser's own id.
                if (TryGetNextRevision(loserOverride.ArtworkRevision, out var adoptedRevision))
                    loserOverride.ArtworkRevision = adoptedRevision;
                else
                    Logger.Error($"Merge migration for '{winnerId}': artwork revision counter exhausted for the loser's history - adopting it as-is.");
                _settings.Overrides[winnerId] = loserOverride;
                AdoptMergedIdentityRevisions(winnerId, loserOverride);
                continue;
            }

            winnerOverride.Favorite = winnerOverride.Favorite || loserOverride.Favorite;
            winnerOverride.Hidden = winnerOverride.Hidden || loserOverride.Hidden;
            winnerOverride.CustomName ??= loserOverride.CustomName;
            winnerOverride.DateAdded ??= loserOverride.DateAdded;

            // Both sides are the SAME install, so their tracked time is time spent on one game - summed,
            // not "whichever side had more". Last-played takes the later of the two for the same reason.
            winnerOverride.TotalPlaySeconds += loserOverride.TotalPlaySeconds;
            if (loserOverride.LastPlayedUtc is { } loserLastPlayed
                && (winnerOverride.LastPlayedUtc is not { } winnerLastPlayed || loserLastPlayed > winnerLastPlayed))
            {
                winnerOverride.LastPlayedUtc = loserLastPlayed;
            }

            // Identity merges independently of artwork: its own counters, its own conflicts (design 8.2).
            MigrateMergedIdentity(loserId, winnerId, loserOverride, winnerOverride);

            // Strictly greater than EITHER side's prior value, never a copy of one side's number - a
            // scan result computed against either side's PRE-merge revision (on this id or, by numeric
            // coincidence, some entirely unrelated game) must never be able to validate against the
            // post-merge id just because the numbers happen to match. See ScanResult.ArtworkApplyResult
            // and ApplyScanResultAsync's reconciliation below for the other half of this guarantee.
            //
            // Only migrate the ARTWORK data when a genuinely new, distinguishing revision can actually be
            // produced - if TryGetNextRevision can't (exhausted), migrating the data anyway would be an
            // unsignaled change: an outstanding scan/lookup that already captured this exact revision
            // would wrongly keep treating itself as current even though the winner's artwork just changed
            // underneath it. Skipping the migration (Favorite/Hidden/CustomName/DateAdded above are
            // unaffected and still apply) is the safe choice at a boundary real usage cannot reach anyway.
            if (TryGetNextRevision(Math.Max(winnerOverride.ArtworkRevision, loserOverride.ArtworkRevision), out var nextRevision))
            {
                MigrateMergedArtwork(loserId, winnerId, loserOverride, winnerOverride);
                winnerOverride.ArtworkRevision = nextRevision;
            }
            else
            {
                // The winner's active artwork/revision are left completely untouched here - not a
                // partial or best-effort migration, since that's exactly the unsignaled-change risk this
                // branch exists to avoid (see the remarks above). But the loser's override was already
                // removed at the top of this loop, and if it held an EXPLICIT user selection, silently
                // letting it fall out of ArtworkConflicts too would lose that selection's identity forever
                // with nothing left even referencing it - a real, if extraordinarily unlikely, gap
                // distinct from the winner-side skip above. Recording it costs nothing (no revision
                // change, no active-artwork change) and matches exactly what MigrateMergedArtwork already
                // does for the ordinary user-selection-conflict case.
                if (loserOverride.Artwork is { IsUserSelected: true } loserSelection)
                {
                    _settings.ArtworkConflicts.Add(new ArtworkConflict
                    {
                        WinnerGameId = winnerId,
                        LoserGameId = loserId,
                        LoserSelection = loserSelection,
                        DetectedAt = DateTime.UtcNow,
                    });
                }

                Logger.Error($"Merge migration for '{winnerId}': artwork revision counter exhausted - skipping artwork migration for this merge.");
            }
        }
    }

    /// <summary>The one place ArtworkRevision is ever incremented - and the one place a mutation is
    /// REJECTED outright once the counter is exhausted, rather than reusing a value that's already been
    /// handed out. An earlier version saturated at long.MaxValue instead of rejecting: repeatedly
    /// returning the same long.MaxValue meant a mutation performed AFTER saturation was indistinguishable,
    /// by revision alone, from the state that existed BEFORE it - an outstanding scan/lookup that had
    /// already captured long.MaxValue as "current" would keep matching it forever, even across a real,
    /// later change it never actually saw. Rejecting instead guarantees the invariant every caller of this
    /// method actually depends on: if a mutation is accepted, the resulting revision is provably distinct
    /// from anything captured before it; if it can't be, nothing about the live state changes at all, so
    /// whatever was already correctly "current" simply stays correctly current. Every caller must check
    /// the `out` result and refuse to change/remove the override it was about to touch when this returns
    /// false. SettingsService.Load applies a separate clamp for a hand-edited or corrupted settings.json -
    /// that protects against untrusted persisted input, not this method's own in-session correctness, so
    /// both are kept.</summary>
    private static bool TryGetNextRevision(long current, out long next)
    {
        if (current >= long.MaxValue)
        {
            next = default;
            return false;
        }

        next = current + 1;
        return true;
    }

    /// <summary>Artwork merge precedence, explicit: a user selection always beats an automatic one (or
    /// none at all); when the winner already has a DIFFERENT explicit user selection, that conflict is
    /// not resolved by picking one and discarding the other - the winner's stays active (least
    /// disruptive - it's already what's showing), and the loser's is recorded in
    /// AppSettings.ArtworkConflicts instead of becoming unreferenced and eventually reclaimed. That is
    /// not a resolution UI (deliberately out of scope for now) - just a guarantee that "preserved" is
    /// actually true rather than "logged, then silently lost" the moment the loser's own GameOverride
    /// is removed above.</summary>
    private void MigrateMergedArtwork(string loserId, string winnerId, GameOverride loserOverride, GameOverride winnerOverride)
    {
        if (loserOverride.Artwork is not { } loserArt)
            return;

        if (winnerOverride.Artwork is not { IsUserSelected: true })
        {
            if (loserArt.IsUserSelected || winnerOverride.Artwork is null)
                winnerOverride.Artwork = loserArt;
            return;
        }

        if (loserArt.IsUserSelected && winnerOverride.Artwork.AssetId != loserArt.AssetId)
        {
            _settings.ArtworkConflicts.Add(new ArtworkConflict
            {
                WinnerGameId = winnerId,
                LoserGameId = loserId,
                LoserSelection = loserArt,
                DetectedAt = DateTime.UtcNow,
            });
            Logger.Warn($"Dedup merge: '{loserId}' and its winner '{winnerId}' both had a user-selected "
                + $"cover (loser asset {loserArt.AssetId} vs winner asset {winnerOverride.Artwork.AssetId}); "
                + "keeping the winner's, recording the loser's in ArtworkConflicts instead of discarding it.");
        }
    }

    /// <summary>Redirects running-game tracking from a merged-away id onto the surviving id it was
    /// folded into - without this, a game that's actively running at the exact moment a rescan merges
    /// its id away (e.g. EA detection for "A Way Out" starts succeeding mid-session, superseding the
    /// Manual entry that was running) would lose its "Running" badge: ReapplyRunningBadge looks up
    /// _runningGameId against the freshly-replaced _allGames, which no longer contains ANY entry under
    /// the old id at all. The session-tracking fields themselves (_runningSessionId, the update guard)
    /// are untouched - only WHICH id they're associated with changes, so MarkGameNotRunning's later call
    /// for this exact session still correctly owns and clears it.</summary>
    private void ReconcileRunningGameId(Dictionary<string, string> mergedGameIds)
    {
        if (mergedGameIds.Count == 0)
            return;

        if (_runningGameId is { } runningId && mergedGameIds.TryGetValue(runningId, out var newRunningId))
            _runningGameId = newRunningId;

        // Every OTHER still-tracked session too, not just whichever one is currently canonical - a
        // session that's already been superseded (but hasn't had its own MarkGameNotRunning call yet)
        // still needs its own tracked identity kept correct, or its eventual cleanup would resolve
        // against a stale, already-merged-away id. See MarkGameNotRunning's own remarks.
        foreach (var sessionId in _sessionGameIds.Keys.ToList())
        {
            if (mergedGameIds.TryGetValue(_sessionGameIds[sessionId], out var winnerId))
                _sessionGameIds[sessionId] = winnerId;
        }
    }

    /// <summary>Publishes a completed ScanResult to live state - the one place RefreshAsync (a real
    /// scan) and tests both route through, so test coverage of merge/reconciliation behavior (id
    /// migration, override migration, DateAdded, the running-game badge) exercises the actual production
    /// path rather than a parallel copy of it that could silently drift from what RefreshAsync really
    /// does.
    ///
    /// Two strictly separated publication phases: PREPARE decodes every
    /// live user-selected cover off the UI thread, touching NO live state at all - not _allGames, not
    /// _settings, not Games/FavoriteGames/HiddenGames. PUBLISH runs through ApplyFilter: fully
    /// synchronous, no yield point anywhere in it, so WPF's single UI-thread dispatcher alone guarantees
    /// no user action (ToggleFavorite/ToggleHidden, another refresh, a Change Cover commit) can ever run
    /// while it's partway through. A real, confirmed bug in an earlier version split _allGames/override
    /// reconciliation (before the await) from Games/FavoriteGames/HiddenGames population (ApplyFilter,
    /// after it) - a card still bound to the OLD GameEntry during that gap could have ToggleFavorite
    /// mutate the old instance and settings, then see ApplyFilter immediately afterward repopulate the
    /// grid from the NEW instances (already reconciled before the click, so untouched by it), making the
    /// click appear to silently revert. Folding ApplyFilter into this same synchronous block closes that
    /// gap entirely. After that block, a two-sided LegacyId migration that discarded a worker cover
    /// can run a fresh, cancellable automatic lookup against the merged state. It never replays the
    /// stale pre-merge identity and never separates publication of the library collections.
    ///
    /// `ownershipToken`, when given (RefreshAsync's own `cts`), is re-checked immediately before PUBLISH
    /// starts - as close to the mutation as the code structure allows - so a refresh superseded while
    /// PREPARE was decoding never publishes its now-stale result. Returns false if superseded before
    /// publication or during subsequent cover recovery;
    /// RefreshAsync uses this instead of re-checking ownership itself afterward, which would already be
    /// too late (the check needs to gate entry to PUBLISH, not run after it). Null (the default, used by
    /// every test that isn't exercising RefreshAsync's own cancel-and-replace machinery) means "always
    /// publish" - there is no competing refresh to be superseded by.
    ///
    /// `identityGenerationsAtScanStart`, when given (RefreshAsync's own SnapshotIdentityGenerations(), taken
    /// before the scan it's now publishing even started), is what lets a verified LegacyId transition's
    /// same-publish unit be trusted DESPITE the markers migration changes - see the re-stamping block below
    /// for why matching the unit's own IdentityRevision/AsOfRevision against the old override's CURRENT
    /// state is not enough by itself: a decision that doesn't move the active identity (rejecting a
    /// non-active candidate, say) advances DecisionRevision and the old id's own identity GENERATION without
    /// touching either revision the unit already checks. Null (every caller that isn't RefreshAsync's real
    /// scan path) means that proof is unavailable, so re-stamping never fires - fails closed, not open.</summary>
    internal async Task<bool> ApplyScanResultAsync(ScanResult result, CancellationTokenSource? ownershipToken = null,
        IReadOnlyDictionary<string, long>? identityGenerationsAtScanStart = null)
    {
        // ---- PREPARE: off the UI thread, read-only against live state ----
        // Snapshot of which games currently have a live user-selected cover, and exactly which asset/
        // revision it was AT SNAPSHOT TIME - re-validated against the LIVE override again, synchronously,
        // immediately before being applied in PUBLISH below. A newer Change Cover/Reset/merge landing on
        // this same game during the decode below is expected and handled there, not prevented here.
        var toDecode = new List<(GameEntry Game, ArtworkSelection Selection, long AsOfRevision)>();
        foreach (var game in result.Games)
        {
            if (_settings.Overrides.TryGetValue(game.Id, out var over) && over.Artwork is { IsUserSelected: true } selection)
                toDecode.Add((game, selection, over.ArtworkRevision));
        }

        var decodedById = toDecode.Count > 0
            ? await DecodePendingCoverRestoresAsync(toDecode)
            : new Dictionary<string, PreparedCoverRestore>();

        if (DuringCoverDecodeForTest is not null)
            await DuringCoverDecodeForTest();

        // ---- PUBLISH: synchronous through ApplyFilter; optional recovery follows afterward ----
        if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
            return false; // superseded while PREPARE was decoding - the newer refresh owns publication now

        // LegacyIdRemap (a scanner's own detection-method change giving the SAME install a new id - see
        // GameEntry.LegacyId) is folded into the very same "this id's state now lives under that other
        // id" map MergedGameIds already represents for a within-scan dedup merge, so it gets migrated
        // through the exact same machinery below rather than a parallel copy of it - running-game
        // tracking, in-flight identity dialogs, and persisted overrides all need to move for either kind
        // of transition, identically.
        var idTransitions = result.MergedGameIds;
        if (result.LegacyIdRemap is { Count: > 0 } legacyIdRemap)
        {
            idTransitions = new Dictionary<string, string>(result.MergedGameIds);
            foreach (var (oldId, newId) in legacyIdRemap)
                idTransitions[oldId] = newId;
        }

        // Before ReplaceAllGames (which reapplies the running badge against the new _allGames) - a
        // stale _runningGameId at that point would find nothing and silently lose the badge.
        ReconcileRunningGameId(idTransitions);

        // loser -> winner, so an identity dialog (or in-flight unit) still holding a merged-away id can find its game (6.4).
        foreach (var (mergedLoserId, mergedWinnerId) in idTransitions)
            _mergeAliases[mergedLoserId] = mergedWinnerId;

        // Captured BEFORE ReplaceAllGames discards these instances - ReconcileArtwork's stale/missing
        // branch needs the LAST live GameEntry for a given id, not the brand-new one this scan just
        // produced, to recover a display result a concurrent commit already applied to it (see
        // ReconcileArtwork's own remarks for the exact scenario this exists to fix).
        var previousGamesById = new Dictionary<string, GameEntry>();
        foreach (var g in _allGames)
            previousGamesById[g.Id] = g; // ids are unique by construction; last-wins is only a defensive fallback

        // A merge/LegacyId transition means the game ReconcileArtwork/CommitAutomaticUnit are about to
        // look up by its NEW id was, until this exact publish, still filed above under its OLD one - so
        // the GetValueOrDefault(game.Id) lookup below would find nothing for it. Without this, a
        // currently-displayed automatic cover can't be carried forward through the very same
        // ArtworkRevision bump MigrateMergedOverrides is about to apply to invalidate the pre-migration
        // worker result (see both methods' own "carried-forward pixels" remarks) - it would incorrectly
        // drop to the exe icon for one refresh even though nothing about the actual artwork changed.
        //
        // Only ever FILLS IN a missing entry - never overwrites one that's already there. The real,
        // confirmed bug this guards against: an ordinary dedup merge's winner is very often NOT brand
        // new - it can already be an existing, previously-displayed game with its own correct cover,
        // merging a loser that ALSO happens to already have its own (different) previously-displayed
        // cover from an earlier scan, before the merge relationship was recognized. Overwriting
        // previousGamesById[newId] unconditionally would clobber the winner's own, more authoritative
        // entry with the loser's - and since MigrateMergedOverrides/MigrateMergedArtwork correctly KEEP
        // the winner's own metadata active in exactly that case, publication would end up authorizing the
        // winner's metadata while displaying the loser's pixels. A transition target with no prior entry
        // at all (the ordinary LegacyId case: the new id has never been displayed before) still gets
        // filled in from the old id, exactly as before.
        foreach (var (oldId, newId) in idTransitions)
        {
            if (previousGamesById.ContainsKey(newId))
                continue;
            if (previousGamesById.TryGetValue(oldId, out var previous))
                previousGamesById[newId] = previous;
        }

        // Captured BEFORE migration, using the exact same condition MigrateMergedOverrides itself branches
        // on ("the winner has no override at all" -> ADOPT the loser's whole override unchanged, vs. both
        // sides having one -> MERGE their data together). Used below to let a same-publish unit for one of
        // these ids validate correctly despite the revision/generation bump adoption still (correctly,
        // unchanged) applies - see that loop's own remarks for why this is safe ONLY for a pure adoption,
        // never for a merge that actually combines two sides' identity/artwork data.
        var adoptedWithNoPriorOverride = new HashSet<string>(idTransitions.Values.Where(id => !_settings.Overrides.ContainsKey(id)));

        // Specifically a LegacyId transition (never an ordinary MergedGameIds dedup) whose worker-produced
        // unit can be PROVEN to have actually been computed against the exact old override now being
        // adopted - an ordinary dedup winner has no such tie: its own unit (if any) was resolved from the
        // WINNER's own prior state, which has nothing to do with whatever a merging loser happens to carry,
        // so "the winner lacks an override" alone is never enough to trust re-stamping it (a loser with its
        // own CONFIRMED identity must still beat the winner's unrelated automatic one, exactly as an
        // ordinary merge already requires). oldByNewId is 1:1 - ComputeLegacyIdRemap only ever reports an
        // old id unambiguously claimed by exactly one new id, in either direction, within one scan.
        var oldByNewId = result.LegacyIdRemap is { Count: > 0 } legacyRemap
            ? legacyRemap.ToDictionary(kv => kv.Value, kv => kv.Key)
            : new Dictionary<string, string>();

        // Previously duplicated Xbox entries can BOTH have persisted overrides. Their merge must
        // invalidate the worker's pre-merge result, but on restart there are no displayed pixels to
        // carry forward. Resolve afresh against the merged state after publication, never re-stamp
        // that stale result. Only actual two-sided migrations need this recovery.
        var mergedLegacyIds = oldByNewId.Where(pair =>
                _settings.Overrides.ContainsKey(pair.Key) && _settings.Overrides.ContainsKey(pair.Value))
            .Select(pair => pair.Key).ToHashSet();
        var coversToRecover = new List<string>();

        // The old override's OWN identity/artwork revision, read RIGHT NOW - before migration consumes it
        // (MigrateMergedOverrides below removes it outright) - so it can be compared, per game, against
        // exactly what that game's own unit captured from it. A live edit landing on the OLD entry AFTER
        // the worker computed its unit but BEFORE this publish runs (a user confirming a different identity
        // via a dialog while this scan was still finishing, say) bumps these on the OLD override - and
        // nothing else does, since the scan's own snapshot never touches the live dictionary. A mismatch
        // here means the worker's result no longer reflects what's actually about to be adopted, and must
        // never be re-stamped into looking current regardless.
        //
        // Revision alone is not enough, though: a decision that doesn't move the ACTIVE identity (rejecting
        // a candidate that wasn't the active one, say) advances DecisionRevision and the old id's own
        // identity GENERATION without touching IdentityRevision at all (see CommitIdentityHalf/
        // CommitIdentityChange - BumpIdentityGeneration runs on ANY change to the record, keyChanged or
        // not). ResolveGameUnit's own IdentityGeneration is captured relative to the NEW id (always 0 for a
        // transition target that's never existed before), so it says nothing about whether the OLD id moved
        // - that's exactly what identityGenerationsAtScanStart's own value for the old id, compared against
        // the old id's CURRENT generation here, closes: if they still match, nothing touched the old entry's
        // identity record - confirmed, rejected, or otherwise - for the whole scan window.
        var preMigrationOldState = new Dictionary<string, (long IdentityRevision, long ArtworkRevision, long Generation)>();
        foreach (var oldId in oldByNewId.Values)
        {
            var o = _settings.Overrides.GetValueOrDefault(oldId);
            preMigrationOldState[oldId] = (o?.IdentityRevision ?? 0, o?.ArtworkRevision ?? 0, IdentityGenerationOf(oldId));
        }

        ReplaceAllGames(result.Games);

        // Before the NewDateAddedByGameId loop below, so its "??=" sees the real, migrated DateAdded
        // already in place rather than treating the surviving id as brand-new and stamping today's date
        // over the game's actual add date.
        MigrateMergedOverrides(idTransitions);

        // Merged here, on the UI thread, rather than written straight into _settings.Overrides from the
        // background scan thread - see GameScannerService.ScanAllAsync's remarks on why that's a real
        // Dictionary-corruption risk, not just a staleness one.
        foreach (var (id, dateAdded) in result.NewDateAddedByGameId)
        {
            if (!_settings.Overrides.TryGetValue(id, out var over))
            {
                over = new GameOverride();
                _settings.Overrides[id] = over;
            }
            over.DateAdded ??= dateAdded;
        }

        // Same idea for any watched folder WatchedFolderResolver healed during this scan (a first-time
        // volume anchor, or a re-derived path after a drive-letter change) - the scan only ever touched
        // its own private copies (see GameScannerService.ScanAllAsync), so the healed values are applied
        // to the live, UI-bound object here instead. Matched by the path the folder had when this scan
        // started, since the healed Path may itself have changed.
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

        // Re-applied here, on the UI thread, rather than trusting the Hidden/Favorite/CustomName/
        // DateAdded GameScannerService already baked into each GameEntry: those came from an Overrides
        // snapshot taken when this scan started, and ToggleFavorite/ToggleHidden stay usable the whole
        // time a scan is running - and, for DateAdded specifically, a merged entry's real historical date
        // only becomes available via MigrateMergedOverrides above, which runs on the UI thread AFTER the
        // scanner already baked its own (wrong, "today") guess into the GameEntry for a brand-new
        // surviving id. Without re-applying DateAdded here too - a real, confirmed gap in an earlier
        // version of this method - "Recently Added" would show the wrong date for a merged game until
        // another, later refresh finally saw the migrated value in its own Overrides snapshot.
        foreach (var game in _allGames)
        {
            _settings.Overrides.TryGetValue(game.Id, out var over);
            if (!string.IsNullOrWhiteSpace(over?.CustomName))
                game.Name = over.CustomName;
            game.Hidden = over?.Hidden ?? false;
            game.Favorite = over?.Favorite ?? false;
            if (over?.DateAdded is { } dateAdded)
                game.DateAdded = dateAdded;
            game.TotalPlaySeconds = over?.TotalPlaySeconds ?? 0;
            game.LastPlayedUtc = over?.LastPlayedUtc;

            var scanned = result.ArtworkResultsByGameId.GetValueOrDefault(game.Id);

            // Re-stamping a same-publish unit's own captured markers to the LIVE, post-migration values is
            // trustworthy ONLY when ALL of these hold together:
            //  1. This is a verified LegacyId transition (oldByNewId), never an ordinary dedup merge - a
            //     dedup winner's own unit (if any) was resolved from the WINNER's own prior state, which has
            //     no connection to whatever a merging loser happens to carry; "the winner lacks an override"
            //     alone proves nothing about what the submitted unit was actually computed against (a real,
            //     confirmed gap: a loser's own confirmed identity must still beat the winner's unrelated
            //     automatic one here, exactly as an ordinary merge already requires elsewhere).
            //  2. It's still a pure ADOPTION (adoptedWithNoPriorOverride) - the same existing requirement.
            //  3. The OLD override's identity/artwork revision, read JUST NOW before migration consumed it
            //     (preMigrationOldState), still EXACTLY matches what this unit captured when the worker
            //     computed it.
            //  4. The OLD id's own identity GENERATION, read the SAME way, still matches what it was at
            //     SCAN START (identityGenerationsAtScanStart) - revision equality alone (point 3) misses a
            //     decision that never moves the active identity (rejecting a non-active candidate, say):
            //     DecisionRevision and the generation both advance, IdentityRevision does not. Without this
            //     check, that rejection would be silently erased by the worker's older, pre-rejection record.
            // A live edit to the old entry AFTER the worker ran but BEFORE this publish (a user confirming or
            // rejecting a candidate via a dialog while the scan was still finishing, say) breaks point 3, 4,
            // or both - the unit no longer reflects what's actually about to be adopted, and must be left to
            // fail its own ordinary, unmodified validation rather than being trusted. Only when all four hold
            // is the unit PROVABLY computed from exactly the override migration is about to adopt, with
            // nothing else having touched it since - safe to translate the markers migration itself changed,
            // without weakening the defensive bump for any other caller.
            if (scanned?.Unit is { } rawUnit
                && adoptedWithNoPriorOverride.Contains(game.Id)
                && oldByNewId.TryGetValue(game.Id, out var sourceOldId)
                && preMigrationOldState.TryGetValue(sourceOldId, out var sourceState)
                && rawUnit.IdentityRevision == sourceState.IdentityRevision
                && scanned.AsOfRevision == sourceState.ArtworkRevision
                && identityGenerationsAtScanStart is not null
                && identityGenerationsAtScanStart.GetValueOrDefault(sourceOldId) == sourceState.Generation)
            {
                var adjustedUnit = rawUnit with { IdentityGeneration = IdentityGenerationOf(game.Id), IdentityRevision = over?.IdentityRevision ?? 0 };
                scanned = scanned with { AsOfRevision = over?.ArtworkRevision ?? 0, Unit = adjustedUnit };
            }

            if (scanned?.Unit is { } unit)
            {
                // The identity pipeline's automatic unit: identity half first, then the artwork half through the gates.
                CommitAutomaticUnit(game, scanned, unit, previousGamesById.GetValueOrDefault(game.Id), decodedById, inPlace: false);
                if (mergedLegacyIds.Contains(game.Id) && !game.IsCoverArt
                    && over?.Artwork is not { IsUserSelected: true })
                    coversToRecover.Add(game.Id);
            }
            else
            {
                ReconcileArtwork(game, over, scanned, previousGamesById.GetValueOrDefault(game.Id), decodedById);
            }
        }

        // Populates Games/FavoriteGames/HiddenGames from the now-fully-reconciled _allGames - in the same
        // synchronous block as everything above, not left for a caller to run after its own later await
        // (see this method's own remarks for why that split was the actual bug).
        ApplyFilter();

        // The library collections are already published together. Recovery is an ordinary guarded
        // automatic lookup against LIVE post-merge identity/revisions, with refresh cancellation
        // carried through the worker and final commit. Explicit choices still take precedence.
        foreach (var gameId in coversToRecover)
        {
            if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
                return false;
            Logger.Info($"Artwork: resolving '{gameId}' against its merged identity after Xbox legacy-id reconciliation.");
            await RunAutomaticUnitAsync(gameId, "Legacy merge", force: false,
                ownershipToken?.Token ?? CancellationToken.None);
        }
        if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
            return false;

        return true;
    }

    /// <summary>One PREPARE-phase decode result for one game - see DecodePendingCoverRestoresAsync/
    /// ApplyScanResultAsync. Selection/AsOfRevision are exactly what was live when the decode was
    /// REQUESTED, not necessarily what's live now - ReconcileArtwork re-validates both against the
    /// CURRENT override before ever applying Decoded, so a selection that changed again while this was
    /// decoding is detected and discarded rather than silently applied over something newer.</summary>
    private sealed record PreparedCoverRestore(ArtworkSelection Selection, long AsOfRevision, BitmapImage? Decoded);

    /// <summary>Decodes every queued live user selection off the UI thread in one batch - pure read/decode
    /// work, touching no live state (not even the GameEntry each request came from; only its Id is used
    /// as the result key). Each item is isolated from every other: one corrupt/missing asset decodes to
    /// null for just that game, never aborting the batch or throwing out of this method.</summary>
    private Task<Dictionary<string, PreparedCoverRestore>> DecodePendingCoverRestoresAsync(
        List<(GameEntry Game, ArtworkSelection Selection, long AsOfRevision)> pending)
    {
        var storeDirOverride = AssetStoreDirOverrideForTest;
        return Task.Run(() =>
        {
            var result = new Dictionary<string, PreparedCoverRestore>();
            foreach (var (game, selection, asOfRevision) in pending)
            {
                BitmapImage? decoded;
                try
                {
                    decoded = CoverArtService.TryDecodeStored(selection, storeDirOverride);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"'{game.Name}': decoding the selected cover art failed unexpectedly.", ex);
                    decoded = null;
                }

                result[game.Id] = new PreparedCoverRestore(selection, asOfRevision, decoded);
            }

            return result;
        });
    }

    /// <summary>Reconciles ONE game's displayed Icon/IsCoverArt (and, for a current automatic match, its
    /// persisted metadata) against the LIVE override - the scan worker computed game.Icon/IsCoverArt
    /// against whatever state existed when it STARTED, which can be stale by the time this publish
    /// actually runs (a Change Cover, Reset, or dedup merge that landed on this exact game while the
    /// scan was still in flight - see ScanResult.ArtworkApplyResult's own remarks). Three explicit
    /// outcomes, never a default of "nothing recorded, trust the worker":
    ///  - A live user selection is authoritative regardless of what the worker computed. If
    ///    `decodedById` has an entry for this game whose Selection/AsOfRevision STILL exactly match the
    ///    CURRENT override (asset identity AND revision, both re-checked HERE, synchronously, at the
    ///    actual moment of applying it - not merely at the moment PREPARE decided what to decode), its
    ///    already-decoded, frozen bitmap is applied directly. Otherwise - no entry at all (this game
    ///    wasn't part of the PREPARE batch, e.g. its selection only became live afterward via a merge
    ///    migration), or a newer Change Cover/Reset changed the selection/revision while PREPARE was
    ///    decoding - the prepared result is discarded as stale and this falls back to a single,
    ///    synchronous, single-file decode instead (CoverArtService.ApplyStoredSafely): a small, bounded
    ///    cost for the rare case, never a reason to risk displaying the wrong image.
    ///  - A live automatic result whose revision still matches what the scan captured at start is
    ///    current: the worker's own game.Icon is already correct on this exact GameEntry and is left
    ///    alone, but its match evidence is always synced into the live override to match what's actually
    ///    displayed - replaced OR cleared, never left stale just because some earlier automatic result
    ///    happened to already be recorded there.
    ///  - Anything else (a revision mismatch, or no scan result for this game at all) is untrustworthy:
    ///    neither the worker's pixels nor its metadata for THIS GameEntry instance are used. But this is
    ///    NOT automatically "fall back to the exe icon" - the live override may already correctly
    ///    describe something newer than what this stale scan captured (a real, confirmed case: Reset's
    ///    own immediate single-game lookup lands and is applied to the PREVIOUS live GameEntry instance
    ///    for this id, and THEN an older, still-in-flight scan publishes and would otherwise silently
    ///    replace that already-correct result with a bare icon on the brand-new instance this scan
    ///    produced - ReplaceAllGames having just discarded the old instance entirely). So: if the live
    ///    override still has ANY current (non-user-selected - a user selection already returned above)
    ///    artwork, the last known-good display for this exact id (`previousLiveGame`, captured before
    ///    ReplaceAllGames ran) is what's trusted instead - its already-decoded, frozen pixels are carried
    ///    forward directly (an automatic result has no local asset to re-decode from). Only when the live
    ///    override has NOTHING selected at all does this fall back to the exe icon.</summary>
    private void ReconcileArtwork(GameEntry game, GameOverride? over, ArtworkApplyResult? scanResult,
        GameEntry? previousLiveGame, Dictionary<string, PreparedCoverRestore> decodedById)
    {
        if (over?.Artwork is { IsUserSelected: true } userSelection)
        {
            if (decodedById.TryGetValue(game.Id, out var prepared)
                && prepared.AsOfRevision == over.ArtworkRevision
                && prepared.Selection.AssetId == userSelection.AssetId
                && prepared.Selection.AssetExtension == userSelection.AssetExtension)
            {
                if (prepared.Decoded is not null)
                {
                    game.Icon = prepared.Decoded;
                    game.IsCoverArt = true;
                }
                else
                {
                    Logger.Warn($"'{game.Name}': selected cover art is missing or corrupt - showing the exe icon; the selection itself is kept.");
                    CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
                }
            }
            else
            {
                CoverArtService.ApplyStoredSafely(game, userSelection, IconFallbackForTest, AssetStoreDirOverrideForTest);
            }

            return;
        }

        var liveRevision = over?.ArtworkRevision ?? 0;
        // A missing scan result (this game has no entry in ArtworkResultsByGameId at all) is represented
        // as null, never as a numeric sentinel - a persisted ArtworkRevision could otherwise coincide
        // with whatever magic number meant "missing" and be wrongly treated as current. See
        // SettingsService.Load for the corresponding defense against a corrupted/out-of-range persisted
        // revision.
        var asOfRevision = scanResult?.AsOfRevision;

        if (asOfRevision == liveRevision)
        {
            // Worker's own pixels for this exact GameEntry are current and correct - left alone. Metadata
            // is still always synced to match exactly what's displayed: over.Artwork here can only be
            // null or a previous AUTOMATIC result (a live user selection already returned above), so
            // replacing or clearing it unconditionally can never discard a user's choice.
            var automaticResult = scanResult?.AutomaticResult;
            if (over is not null)
                over.Artwork = automaticResult;
            else if (automaticResult is not null)
            {
                over = new GameOverride { ArtworkRevision = liveRevision };
                _settings.Overrides[game.Id] = over;
                over.Artwork = automaticResult;
            }

            return;
        }

        if (over?.Artwork is not null)
        {
            if (previousLiveGame is { Icon: not null, IsCoverArt: true })
            {
                // Cheap and synchronous: reusing an already-decoded, frozen BitmapImage reference, not
                // re-reading or re-decoding anything. An automatic result has no local asset to restore
                // from at all - this is the only way to recover it without a fresh provider call during
                // reconciliation, which must stay network-free.
                game.Icon = previousLiveGame.Icon;
                game.IsCoverArt = previousLiveGame.IsCoverArt;
            }
            else
            {
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
            }

            return;
        }

        CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
    }

    /// <summary>Read-only test seam mirroring RunningGameId - lets tests assert directly on an
    /// override's state (e.g. after MigrateMergedOverrides) without a public Overrides accessor.</summary>
    internal GameOverride? GetOverride(string gameId) => _settings.Overrides.GetValueOrDefault(gameId);

    /// <summary>Write counterpart to GetOverride - sets up merge/reconciliation test scenarios directly
    /// (an existing user selection with a specific revision) without needing a full async Apply/Reset
    /// round-trip for every setup step.</summary>
    internal void SetArtworkForTest(string gameId, ArtworkSelection? artwork, long revision)
    {
        if (!_settings.Overrides.TryGetValue(gameId, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        over.Artwork = artwork;
        over.ArtworkRevision = revision;
    }

    /// <summary>Read-only test seam mirroring GetOverride, for AppSettings.ArtworkConflicts.</summary>
    internal IReadOnlyList<ArtworkConflict> ArtworkConflictsForTest => _settings.ArtworkConflicts;

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
        ResetScanProgress();
        var scanProgress = new Progress<ScanProgress>(p => ApplyScanProgress(p, cts)); // created here, on the UI thread: reports arrive here too
        try
        {
            // Captured once and reused for both calls below - ApplyScanResultAsync needs the EXACT same
            // scan-start snapshot ScanAllAsync's own units were resolved against, to prove (not merely
            // assume) a LegacyId transition's same-publish unit still reflects the old entry's identity
            // untouched - see its own remarks on identityGenerationsAtScanStart.
            var identityGenerationsAtScanStart = SnapshotIdentityGenerations();
            var result = await _scannerService.ScanAllAsync(_settings, token, identityGenerationsAtScanStart, ResolutionContextForTest?.Invoke(), SnapshotDisplayedCovers(), scanProgress);

            // GameScannerService checks the token internally too, but only cooperatively - a
            // cancelled scan can still be mid-flight on a background thread pool thread when this
            // await resumes (e.g. blocked in a synchronous cover-art HTTP call) and, depending on
            // exactly where cancellation landed, can complete "successfully" with a result that's
            // already stale. Cheap early exit before spending time on ApplyScanResultAsync's own
            // decode-prep phase - not the check that actually matters (that one lives INSIDE
            // ApplyScanResultAsync, immediately before it publishes; see its own remarks for why a
            // check made only here, after its await returns, would already be too late).
            if (!ReferenceEquals(_refreshCts, cts))
                return;

            var published = await ApplyScanResultAsync(result, cts, identityGenerationsAtScanStart);
            if (!published)
                return; // superseded while decoding - the newer refresh already owns everything below

            _settingsService.Save(_settings); // persists the DateAdded/watched-folder changes merged above
            RefreshDrives();

            // Sizes support the "Largest installed" sort library-wide, even though their visual rows
            // are shown only in the selected-drive view. Fire-and-forget: a background refinement
            // of already-usable results, and it cancels itself if another pass supersedes it.
            _ = EstimateInstallSizesAsync();

            // Count from the filtered collection, not _allGames directly - scanning now always covers
            // every source (see GameScannerService), so _allGames includes games from sources the user
            // has toggled off. Games alone is the whole on-screen grid: favourites are a VIEW of it
            // now, not a separate section lifted out of it, so adding FavoriteGames here double-counted
            // every starred game ("9 of 6 games shown").
            var shown = Games.Count;
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
                ResetScanProgress();

                // A scan is a burst of allocation (file/registry walking, decoding cover art) and the
                // app goes idle straight after. Hand back what that burst left resident.
                MemoryTrimmer.Trim("after scan");
            }
        }
    }

    private void ResetScanProgress()
    {
        ScanProgressPercent = 0;
        ScanProgressText = "";
        IsScanIndeterminate = true;
    }

    /// <summary>Shows one step of the running scan. A report that arrives after the scan finished, or from a scan that has since been
    /// superseded, is ignored - it must not repaint a bar that now belongs to the newer scan (or to nothing). The bar never goes backwards.</summary>
    internal void ApplyScanProgress(ScanProgress progress, CancellationTokenSource? owner)
    {
        if (!IsLoading || (owner is not null && !ReferenceEquals(_refreshCts, owner)))
            return;

        IsScanIndeterminate = false;
        ScanProgressPercent = Math.Max(ScanProgressPercent, progress.Percent);
        ScanProgressText = progress.Text;
        StatusText = progress.Text;
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

    // ---- Change Cover / Reset to Automatic -----------------------------------------------------------
    //
    // ApplyLocalCoverImageAsync/ResetCoverToAutomaticAsync take a bare gameId and return an outcome enum
    // precisely so they're callable both directly by tests (see LibraryViewModelArtworkTests) and by the
    // two UI-facing commands below, without either caller needing to know about the other.

    /// <summary>Change Cover's validation-only half: reads and decodes a candidate file purely in
    /// memory - nothing on disk or in settings is touched. Split out from the old, single-shot
    /// ApplyLocalCoverImageAsync so the Preview -> Apply/Cancel dialog can validate and decode a
    /// candidate ONCE, show it, and only pass it on to ApplyValidatedCoverImageAsync (the side-effecting
    /// half) if and when the user actually clicks Apply - cancelling or closing the dialog simply
    /// discards the returned ValidatedImage, which is the entire "no persistent change until Apply"
    /// contract; there is nothing else to roll back because nothing was ever written.</summary>
    public Task<ArtworkImageValidator.ValidatedImage?> ValidateLocalCoverImageAsync(string localFilePath, CancellationToken ct = default)
        // Explicitly offloaded, not just awaited - ValidateLocalFileAsync's own internal awaits (a plain
        // bounded file read) resume on whatever context called it, which is the UI thread's dispatcher
        // when this runs from a UI action. Its real work - BitmapDecoder.Create with DelayCreation, plus
        // a full CoverArtDecoder.Decode - is genuine codec work (see ArtworkImageValidator's own
        // remarks), so relying on internal ConfigureAwait behavior alone would NOT guarantee it stays
        // off the UI thread; Task.Run does.
        => Task.Run(() => ArtworkImageValidator.ValidateLocalFileAsync(localFilePath, ct), ct);

    /// <summary>Change Cover's side-effecting half: stages `validated`'s bytes into ArtworkAssetStore and
    /// commits the selection - the same write-then-commit tail ApplyLocalCoverImageAsync always ran, now
    /// reusable by the Preview dialog's Apply step. `gameId`, not a GameEntry reference, is what a caller
    /// must hold across this call - a refresh can replace or merge the target game while staging is
    /// running; see CommitArtworkChange's re-resolution.
    ///
    /// Manages _artworkOperationsInFlight itself - ApplyLocalCoverImageAsync below does NOT call this
    /// method for that reason: it needs the guard held across ITS OWN validate step too (see its own
    /// remarks and ApplyValidatedCoverImageCoreAsync), and HashSet.Add is not reentrant - a second Add
    /// for a gameId already held by the SAME logical call would incorrectly report AlreadyInProgress
    /// against itself.</summary>
    public async Task<ArtworkChangeOutcome> ApplyValidatedCoverImageAsync(string gameId, ArtworkImageValidator.ValidatedImage validated, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>Runs both halves back-to-back, exactly as Change Cover behaved before the Preview
    /// dialog existed - kept for every direct caller that doesn't need a preview step (every existing
    /// test, and any future non-interactive caller). The guard is taken here, ONCE, covering both the
    /// validate and the write/commit steps - the same window the original single-method implementation
    /// held it for - so two overlapping calls for the same gameId are still deterministically resolved
    /// to exactly one Success and the rest AlreadyInProgress, regardless of how validation's own timing
    /// happens to interleave.</summary>
    public async Task<ArtworkChangeOutcome> ApplyLocalCoverImageAsync(string gameId, string localFilePath, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            var validated = await ValidateLocalCoverImageAsync(localFilePath, ct);
            if (validated is null)
                return ArtworkChangeOutcome.InvalidImage;

            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>The actual write+commit work, shared by both guarded entry points above - neither adds
    /// nor removes _artworkOperationsInFlight itself, so it can safely run under whichever of the two
    /// callers' own guard window is already held.</summary>
    private async Task<ArtworkChangeOutcome> ApplyValidatedCoverImageCoreAsync(string gameId, ArtworkImageValidator.ValidatedImage validated, CancellationToken ct,
        ArtworkSelection? catalogProvenance = null, long? expectedArtworkRevision = null)
    {
        // A stale preview is refused BEFORE anything is staged (design 6.7, D3): a newer cover, a Reset or a merge that landed
        // while the user was choosing must not be silently overwritten. CommitArtworkChange re-checks it at the commit itself.
        if (expectedArtworkRevision is { } expected
            && (_settings.Overrides.TryGetValue(gameId, out var current) ? current.ArtworkRevision : 0) != expected)
        {
            return ArtworkChangeOutcome.StaleSelection;
        }

        string assetId;
        try
        {
            assetId = await Task.Run(
                () => ArtworkAssetStore.Write(validated.Bytes, validated.Extension, AssetStoreDirOverrideForTest), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing was ever staged - the previous selection (if any) is simply left exactly as it
            // was; there is nothing to roll back, unlike CommitArtworkChange's SaveFailed case.
            Logger.Warn($"Couldn't stage the selected cover image for game '{gameId}'.", ex);
            return ArtworkChangeOutcome.StorageFailed;
        }

        AfterAssetWrittenForTest?.Invoke(assetId);

        // A pinned cover is a DISPLAY decision. When it came from a catalog, ProviderGameId/ProviderArtworkRef record where
        // the IMAGE came from (provenance) - never which game is installed, and it has no DerivedFrom: identity never
        // authorizes or removes it (I1, I3).
        return CommitArtworkChange(gameId, over => over.Artwork = new ArtworkSelection
        {
            Provider = catalogProvenance?.Provider ?? ArtworkProvider.UserLocalFile,
            RetrievedFrom = catalogProvenance is null ? ArtworkRetrievalMethod.UserSuppliedFile : ArtworkRetrievalMethod.NetworkDownload,
            ProviderGameId = catalogProvenance?.ProviderGameId,
            ProviderArtworkRef = catalogProvenance?.ProviderArtworkRef,
            ProviderTitle = catalogProvenance?.ProviderTitle,
            AssetId = assetId,
            AssetExtension = validated.Extension,
            MatchMethod = catalogProvenance is null ? "UserLocalFile" : "UserChosenCatalogCover",
            IsUserSelected = true,
            SelectedAt = DateTime.UtcNow,
            DerivedFrom = null,
        }, preparedIcon: validated.DecodedImage, expectedRevision: expectedArtworkRevision, ct: ct);
    }

    /// <summary>Choose Cover from a catalog: pins an image the user picked among a catalog entry's covers (already downloaded and
    /// validated) with its provenance. `expectedArtworkRevision` is the revision the dialog captured when it opened: if a newer
    /// cover, a Reset or a merge landed while the user was choosing, this returns StaleSelection and applies NOTHING. Identity is
    /// never read or written here.</summary>
    public async Task<ArtworkChangeOutcome> ApplyCatalogCoverAsync(string gameId, ArtworkImageValidator.ValidatedImage validated,
        ArtworkSelection provenance, long expectedArtworkRevision, CancellationToken ct = default)
    {
        gameId = ResolveAlias(gameId);
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct, provenance, expectedArtworkRevision);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>Clears the selection (synchronously, so the card immediately shows the exe icon rather
    /// than stale pixels), THEN kicks off an immediate, single-game automatic lookup - "the next scan
    /// will eventually pick it up" is not an acceptable implementation of Reset on its own; the user
    /// shouldn't have to manually refresh to see it take effect. The lookup's provider/network work runs
    /// off the UI thread against a private scratch GameEntry, never the live bound one (mutating that
    /// from a background thread would be the exact anti-pattern GameScannerService's own scan-snapshot
    /// discipline exists to avoid) - only once back on the UI thread, and only if nothing else has
    /// changed this game's artwork state in the meantime, is the result actually applied.</summary>
    public async Task<ArtworkChangeOutcome> ResetCoverToAutomaticAsync(string gameId, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            var outcome = CommitArtworkChange(gameId, over => over.Artwork = null);
            if (outcome != ArtworkChangeOutcome.Success)
                return outcome;

            // Reset is an ARTWORK transaction only (identity untouched, I2). What follows is a SEPARATE, ordinary automatic
            // unit flagged Trigger=Reset (cooldown bypassed, rejections never): with an active identity it fetches by id
            // (zero title searches); with none it is the ordinary full resolution (design 6.6). The test seam below keeps the
            // older name-based lookup path for the tests that predate the identity pipeline.
            if (AutomaticCoverArtLookupForTest is null)
            {
                try
                {
                    await RunAutomaticUnitAsync(gameId, "Reset", force: true, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"'{gameId}': automatic cover lookup after Reset failed unexpectedly.", ex);
                }

                return outcome;
            }

            _settings.Overrides.TryGetValue(gameId, out var overAfterReset);
            var asOfRevision = overAfterReset?.ArtworkRevision ?? 0;

            var game = _allGames.FirstOrDefault(g => g.Id == gameId);
            if (game is null)
                return outcome; // vanished immediately after the reset itself succeeded - nothing more to do

            var apiKey = _settings.SteamGridDbApiKey;
            // Defaults to the real CoverArtService.Apply - overridable so tests can prove the immediate
            // lookup ran (or force a deterministic result) without depending on whether this checkout
            // happens to have a real embedded SteamGridDB key (default-api-key.txt) or reaching the
            // network either way. An explicit lambda, not a bare method group, because CoverArtService.
            // Apply has more optional parameters than AutomaticCoverArtLookupForTest's own two-argument
            // seam type.
            //
            // The IGDB credentials are captured HERE, on the UI thread, BEFORE Task.Run below, as value
            // snapshots the lambda closes over - a real, confirmed bug in an earlier version read
            // _settings.IgdbClientId from INSIDE the lambda, i.e. on the background thread, retaining a
            // live reference to shared mutable state (see GameScannerService.ScanAllAsync's own remarks).
            // Loaded only on the real path: when a test supplies AutomaticCoverArtLookupForTest, no
            // credential file is read at all.
            Func<GameEntry, string?, ArtworkSelection?> applyCoverArt;
            if (AutomaticCoverArtLookupForTest is { } lookupSeam)
            {
                applyCoverArt = lookupSeam;
            }
            else
            {
                var igdbClientId = _settings.IgdbClientId;
                var igdbClientSecret = _credentialStore.LoadSecret();
                applyCoverArt = (g, key) => CoverArtService.Apply(g, key, igdbClientId, igdbClientSecret, ct: ct);
            }
            (BitmapImage? Icon, bool IsCoverArt, ArtworkSelection? Automatic) computed;
            try
            {
                computed = await Task.Run(() =>
                {
                    // Private, unbound scratch copy - CoverArtService.Apply mutates whatever GameEntry
                    // it's given directly, and that must never be the live, UI-bound `game` from a
                    // background thread.
                    var scratch = new GameEntry
                    {
                        Id = game.Id, Name = game.Name, CatalogName = game.CatalogName, ExecutablePath = game.ExecutablePath,
                        InstallDir = game.InstallDir, Source = game.Source, LaunchUri = game.LaunchUri,
                    };
                    var automatic = applyCoverArt(scratch, apiKey);
                    return (scratch.Icon, scratch.IsCoverArt, automatic);
                }, ct);
            }
            catch (Exception ex)
            {
                // Never let a network/provider failure here surface as a Reset failure - the reset
                // itself already succeeded and is durably saved; this lookup is a best-effort
                // improvement layered on top of it.
                Logger.Warn($"'{game.Name}': automatic cover lookup after Reset failed unexpectedly.", ex);
                return outcome;
            }

            CommitAutomaticArtworkIfCurrent(gameId, asOfRevision, computed.Icon, computed.IsCoverArt, computed.Automatic);
            return outcome;
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    // Test-only seams for the two UI commands below - production always leaves both null, in which case
    // ChangeCoverAsync resolves to the real OpenFileDialog and the real ChangeCoverWindow. Split into two
    // separate delegates (rather than one seam standing in for "the whole command") so a test can
    // exercise each stage's own cancellation path independently - picker-cancelled vs preview-cancelled
    // are different user actions with the same required outcome (nothing changes), and collapsing them
    // into one seam couldn't tell those two tests apart.
    internal Func<string, string?>? ChangeCoverFilePickerForTest { get; set; }
    internal Func<string, BitmapImage, bool>? ChangeCoverPreviewDialogForTest { get; set; }

    /// <summary>Change Cover's UI entry point. `game` is read only for its Id/Name, captured into local
    /// variables BEFORE any await - a refresh can replace this exact GameEntry instance while the file
    /// dialog or preview window is open (both block on real user input for however long the user takes),
    /// so nothing past this point may dereference `game` again.
    ///
    /// Picking a file only VALIDATES and PREVIEWS it (ValidateLocalCoverImageAsync touches nothing on
    /// disk or in settings) - the existing cover and settings are only ever touched by
    /// ApplyValidatedCoverImageAsync, reached below if and only if the preview dialog's own Apply button
    /// was clicked. Closing the file picker, or closing/cancelling the preview dialog, returns out of
    /// this method with nothing changed either way.
    ///
    /// Wrapped in a single try/catch: CommunityToolkit's generated async commands otherwise let an
    /// unhandled exception propagate back onto the UI thread's SynchronizationContext, which this app's
    /// dispatcher-level handler treats as fatal and shuts down on - turning a recoverable failure (a
    /// locked file, a picker COM hiccup) into a full crash. Every awaited/called step here already
    /// handles its OWN expected failure modes and returns an ArtworkChangeOutcome instead of throwing;
    /// this catch is strictly a last-resort net for whatever gets past that, logged and surfaced as a
    /// StatusText message instead.</summary>
    [RelayCommand]
    private async Task ChangeCoverAsync(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        var gameName = game.Name;

        try
        {
            var pickFile = ChangeCoverFilePickerForTest ?? PickCoverFileFromDisk;
            var path = pickFile(gameName);
            if (path is null)
                return; // picker cancelled/closed - nothing was ever touched

            var validated = await ValidateLocalCoverImageAsync(path);
            if (validated is null)
            {
                StatusText = $"That file couldn't be used as {gameName}'s cover - check its format and dimensions.";
                return;
            }

            var showPreview = ChangeCoverPreviewDialogForTest ?? ShowChangeCoverPreviewDialog;
            var applied = showPreview(gameName, validated.DecodedImage);
            if (!applied)
                return; // preview cancelled/closed - the validated bytes are simply discarded, nothing staged or persisted

            var outcome = await ApplyValidatedCoverImageAsync(gameId, validated);
            StatusText = outcome switch
            {
                ArtworkChangeOutcome.Success => $"Cover updated for {gameName}.",
                ArtworkChangeOutcome.AlreadyInProgress => $"Already updating {gameName}'s cover - try again in a moment.",
                ArtworkChangeOutcome.StorageFailed => "Couldn't save that image to disk.",
                ArtworkChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only.",
                ArtworkChangeOutcome.GameNoLongerExists => $"{gameName} is no longer in your library.",
                ArtworkChangeOutcome.RevisionExhausted => $"{gameName}'s cover has been changed too many times to update again.",
                _ => StatusText,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Change Cover failed unexpectedly for '{gameName}'.", ex);
            StatusText = $"Couldn't change the cover for {gameName} - see the log for details.";
        }
    }

    private static string? PickCoverFileFromDisk(string gameName)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a cover image for {gameName}",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Shows the local-only Preview -> Apply/Cancel dialog and blocks (ShowDialog, same as
    /// SettingsWindow/WhatsNewWindow) until the user picks one. Returns whether Apply was clicked - the
    /// dialog itself never touches settings or disk; see ChangeCoverDialogViewModel.</summary>
    private static bool ShowChangeCoverPreviewDialog(string gameName, BitmapImage previewImage)
    {
        var dialogViewModel = new ChangeCoverDialogViewModel(gameName, previewImage);
        var window = new ChangeCoverWindow(dialogViewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return dialogViewModel.Applied;
    }

    /// <summary>Reset's UI entry point - see ChangeCoverAsync's remarks on why `game` is only ever read
    /// for its Id/Name up front, and on why the whole body is wrapped in a single catch-all.</summary>
    [RelayCommand]
    private async Task ResetCoverAsync(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        var gameName = game.Name;

        try
        {
            var outcome = await ResetCoverToAutomaticAsync(gameId);
            StatusText = outcome switch
            {
                ArtworkChangeOutcome.Success => $"Cover reset to automatic for {gameName}.",
                ArtworkChangeOutcome.AlreadyInProgress => $"Already updating {gameName}'s cover - try again in a moment.",
                ArtworkChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only.",
                ArtworkChangeOutcome.GameNoLongerExists => $"{gameName} is no longer in your library.",
                ArtworkChangeOutcome.RevisionExhausted => $"{gameName}'s cover has been changed too many times to reset again.",
                _ => StatusText,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Reset Cover failed unexpectedly for '{gameName}'.", ex);
            StatusText = $"Couldn't reset the cover for {gameName} - see the log for details.";
        }
    }

    /// <summary>The one place Change Cover/Reset actually mutate live settings - fully synchronous (no
    /// await anywhere in this method's body), which is what lets WPF's single UI-thread dispatcher
    /// serialize it against every other call to this same method for a DIFFERENT game, without needing a
    /// separate lock: there is no yield point inside this method at which the dispatcher could
    /// interleave another commit. A per-game guard (_artworkOperationsInFlight) alone would not protect
    /// the shared settings.json if two DIFFERENT games' commits could genuinely interleave - this is
    /// what actually prevents that.
    ///
    /// Re-resolves `gameId` against the CURRENT _allGames rather than trusting any reference a caller
    /// might have held across its own earlier await - a refresh can replace or merge the target game
    /// while an async prepare phase (image validation, asset write) was running. If the id no longer
    /// exists at all, this aborts WITHOUT creating/resurrecting an override for a dead id.
    ///
    /// `preparedIcon` lets a caller that already decoded and froze the exact image being committed
    /// (ApplyLocalCoverImageAsync's own full-decode validation step - see ArtworkImageValidator.
    /// ValidatedImage.DecodedImage) hand it straight to the display, instead of this method re-reading
    /// the just-written asset file and re-decoding it a second time on the UI thread. Only meaningful
    /// together with a `mutate` that sets a NEW Artwork selection matching those exact bytes - Reset's
    /// own `mutate` clears Artwork instead and never passes this.
    ///
    /// The revision check runs FIRST, before `existing`/`over` are touched at all - see
    /// TryGetNextRevision's own remarks for why a caller must reject an exhausted mutation before
    /// changing or removing anything, not fall back to reusing a stale value afterward.
    ///
    /// `ct` is the caller's own cancellation (the Choose Cover dialog closing). It is checked HERE, as the very first statement, so
    /// there is no await between the check and the mutation/save that follows: a cancellation that arrived during ANY earlier await
    /// (validation, download, asset staging) commits nothing and throws OperationCanceledException. A staged asset may stay on disk
    /// under the deferred-cleanup policy; that is never a reason to save a selection the user walked away from.</summary>
    private ArtworkChangeOutcome CommitArtworkChange(string gameId, Action<GameOverride> mutate, BitmapImage? preparedIcon = null,
        long? expectedRevision = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return ArtworkChangeOutcome.GameNoLongerExists;

        _settings.Overrides.TryGetValue(gameId, out var existing);
        var previousRevision = existing?.ArtworkRevision ?? 0;

        // Re-checked HERE, synchronously, at the commit: the asset write above is async, so a newer change may have landed.
        if (expectedRevision is { } expected && expected != previousRevision)
            return ArtworkChangeOutcome.StaleSelection;

        if (!TryGetNextRevision(previousRevision, out var nextRevision))
        {
            Logger.Error($"'{game.Name}': artwork revision counter exhausted - rejecting this change rather than reusing a non-unique revision.");
            return ArtworkChangeOutcome.RevisionExhausted;
        }

        var hadNoOverride = existing is null;
        var previousArtwork = existing?.Artwork;

        var over = existing ?? new GameOverride();
        mutate(over);
        over.ArtworkRevision = nextRevision;
        if (hadNoOverride)
            _settings.Overrides[gameId] = over;

        if (_settingsService.Save(_settings))
        {
            if (over.Artwork is { } selection)
            {
                if (preparedIcon is not null)
                {
                    game.Icon = preparedIcon;
                    game.IsCoverArt = true;
                }
                else
                    CoverArtService.ApplyStoredSafely(game, selection, IconFallbackForTest, AssetStoreDirOverrideForTest);
            }
            else
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);

            return ArtworkChangeOutcome.Success;
        }

        // Settings failed to write - roll back exactly what was there before. Nothing durable changed,
        // so nothing displayed should change either; the newly staged asset file (if any) is simply left
        // orphaned on disk, same as every other deferred-cleanup case.
        if (hadNoOverride)
            _settings.Overrides.Remove(gameId);
        else
        {
            over.Artwork = previousArtwork;
            over.ArtworkRevision = previousRevision;
        }

        return ArtworkChangeOutcome.SaveFailed;
    }

    /// <summary>Commits an automatic lookup's result ONLY if the live ArtworkRevision still equals what
    /// was captured when the lookup started - the same optimistic-concurrency guard ApplyScanResultAsync's
    /// reconciliation uses, reused here for the identical reason: if a newer Change Cover, Reset, or
    /// scan already changed this game's artwork state while this lookup was running, the result is stale
    /// and must not overwrite something newer. Deliberately does NOT bump ArtworkRevision itself - this
    /// records what a CURRENT-revision lookup found, not a new user action; bumping would immediately
    /// invalidate the very check that just confirmed the result is current.
    ///
    /// Metadata is synced unconditionally once currency is confirmed - success OR no-match - mirroring
    /// ReconcileArtwork's own "current" branch. A real, confirmed gap in an earlier version only synced
    /// on success: if a CONCURRENT scan at this exact same revision published its own automatic metadata
    /// while this lookup was still running, and this lookup itself then found no match, the card would
    /// correctly fall back to the exe icon here but over.Artwork would still describe the scan's earlier
    /// (no-longer-displayed) match - a metadata/display mismatch. Clearing it here too keeps them in sync
    /// regardless of which of the two concurrent writers happens to finish first.</summary>
    private void CommitAutomaticArtworkIfCurrent(string gameId, long asOfRevision, BitmapImage? icon, bool isCoverArt, ArtworkSelection? automatic)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return;

        _settings.Overrides.TryGetValue(gameId, out var over);
        if ((over?.ArtworkRevision ?? 0) != asOfRevision)
            return; // stale - something else already changed this game's artwork state

        game.Icon = icon;
        game.IsCoverArt = isCoverArt;

        if (over is null)
        {
            if (automatic is null)
                return; // nothing to persist, and nothing was recorded before - no override needed at all
            over = new GameOverride { ArtworkRevision = asOfRevision };
            _settings.Overrides[gameId] = over;
        }

        over.Artwork = automatic; // replace or clear - always matches what's actually displayed above
        _settingsService.Save(_settings); // best-effort - game.Icon is already correct regardless of whether this persists
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
    private void SelectDrive(string? letter) =>
        SelectedDriveLetter = string.Equals(SelectedDriveLetter, letter, StringComparison.OrdinalIgnoreCase) ? null : letter;

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
    private async Task EstimateInstallSizesAsync()
    {
        _sizeEstimationCts?.Cancel();

        var cts = new CancellationTokenSource();
        _sizeEstimationCts = cts;
        var token = cts.Token;

        var letter = SelectedDriveLetter;
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

    /// <summary>ApplyFilter runs on every keystroke, sort change and refresh, and clearing then re-adding a collection
    /// rebuilds every card even when nothing changed - so a collection is only touched when its contents really differ.</summary>
    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
            return;

        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    // The hero tint is sampled from pixels, so it is worked out once per cover image, not once per ApplyFilter.
    private BitmapSource? _accentSource;
    private Color? _accentColor;

    private Color? AccentFor(BitmapSource? icon)
    {
        if (icon is null)
            return null;

        if (!ReferenceEquals(icon, _accentSource))
        {
            _accentColor = DominantColorExtractor.Extract(icon);
            _accentSource = icon;
        }

        return _accentColor;
    }

    private void ApplyFilter()
    {
        // Computed from the full, un-filtered scan result (not the Detect-filtered list below), so
        // disabling a source can never make its own sidebar row disappear. One pass, not one scan per launcher.
        var present = _allGames.Select(g => g.Source).ToHashSet();
        HasAnySourceGames = SourceItems.Any(i => present.Contains(i.Source));
        foreach (var item in SourceItems)
            item.Refresh(present.Contains(item.Source));

        // The drive and search narrowing applies to visible and hidden games alike.
        var selectedDrive = SelectedDriveLetter;
        var search = SearchText;
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        bool Matches(GameEntry g) => IsSourceEnabled(g.Source)
            && (selectedDrive is null || string.Equals(DriveLetterOf(g.InstallDir), selectedDrive, StringComparison.OrdinalIgnoreCase))
            && (!hasSearch || g.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        IEnumerable<GameEntry> filtered = _allGames.Where(g => !g.Hidden && Matches(g));

        filtered = SortOption switch
        {
            GameSortOption.NameDesc => filtered.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.Source => filtered.OrderBy(g => g.Source)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.FavoritesFirst => filtered.OrderByDescending(g => g.Favorite)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.RecentlyAdded => filtered.OrderByDescending(g => g.DateAdded),
            // Unknown sizes sort last rather than as zero, so a library still being measured doesn't
            // shuffle unmeasured games to the bottom and then re-shuffle them as results land.
            GameSortOption.LargestInstalled => filtered
                .OrderByDescending(g => g.InstallSizeBytes ?? -1)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            GameSortOption.MostPlayed => filtered
                .OrderByDescending(g => g.TotalPlaySeconds)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
        };

        // The rail's view narrows what the grid shows; Favorites is a VIEW here, not a separate
        // always-on section above the grid, so a favourited game appears in All too (with its star
        // badge) rather than being lifted out of it.
        filtered = SelectedView switch
        {
            LibraryView.Favorites => filtered.Where(g => g.Favorite),
            LibraryView.Recent => filtered.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc),
            _ => filtered,
        };

        var ordered = filtered.ToList();

        // The hero only shows on the unfiltered All view - searching, filtering to a drive, or
        // switching views hides it rather than pinning a game the grid no longer shows. "Jump back in"
        // means most recently PLAYED; until anything has been played, the newest addition stands in,
        // since that's the only real signal available then.
        var isHomeView = SelectedView == LibraryView.All
            && SelectedDriveLetter is null
            && string.IsNullOrWhiteSpace(SearchText);

        FeaturedGame = isHomeView
            ? ordered.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).FirstOrDefault()
                ?? ordered.OrderByDescending(g => g.DateAdded).FirstOrDefault()
            : null;
        HasFeaturedGame = FeaturedGame is not null;
        HeroAccentColor = (FeaturedGame?.IsCoverArt == true ? AccentFor(FeaturedGame.Icon) : null) ?? DefaultHeroAccentColor;

        // "Recently played": only games with real tracked sessions, newest first, capped so the strip
        // stays a strip. Empty (and hidden) until something has actually been played through the app.
        ReplaceIfChanged(RecentlyPlayedGames, isHomeView
            ? ordered.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).Take(10).ToList()
            : new List<GameEntry>());
        HasRecentlyPlayed = RecentlyPlayedGames.Count > 0;

        ReplaceIfChanged(FavoriteGames, ordered.Where(g => g.Favorite).ToList());
        ReplaceIfChanged(Games, ordered);
        HasFavorites = FavoriteGames.Count > 0;

        // Hidden games: same narrowing as everything else, but sourced from g.Hidden directly rather than
        // the `filtered` sequence above, since that sequence already excludes them by design (they must
        // never leak into Games/FavoriteGames).
        ReplaceIfChanged(HiddenGames, _allGames.Where(g => g.Hidden && Matches(g))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList());

        HasHiddenGames = HiddenGames.Count > 0;
        ShowHiddenSection = ShowHiddenGames && HasHiddenGames;

        // An empty view is not an empty install library. Discovery actions belong only to the latter.
        HasNoGames = Games.Count == 0 && !ShowHiddenSection;
        ShowEmptyDiscoveryActions = SelectedView == LibraryView.All && _allGames.Count == 0
            && string.IsNullOrWhiteSpace(SearchText) && SelectedDriveLetter is null;
        var hasViewEntries = _allGames.Any(g => !g.Hidden && (SelectedView switch
        {
            LibraryView.Favorites => g.Favorite,
            LibraryView.Recent => g.HasPlayTime,
            _ => true,
        }));
        (EmptyStateTitle, EmptyStateDescription) = SelectedView switch
        {
            LibraryView.Favorites when !hasViewEntries => ("No favorites yet",
                "Click the star on any game in All games to keep it here."),
            LibraryView.Recent when !hasViewEntries => ("Nothing played yet",
                "Play a game while Axis Game Launcher is running and it will appear here. Earlier play history isn't tracked."),
            LibraryView.Favorites => ("No favorites match your filters",
                "Try another search, drive, or launcher filter to see your favorites."),
            LibraryView.Recent => ("No recently played games match your filters",
                "Try another search, drive, or launcher filter to see your recently played games."),
            _ when ShowEmptyDiscoveryActions => ("No games found yet",
                "Games are auto-detected from Steam, Epic, GOG and more, or add a folder to scan."),
            _ => ("No games match this view",
                "Try another search, drive, or launcher filter, or check your hidden games."),
        };

        LibraryHeaderText = SelectedDriveLetter is { } headerDrive
            ? $"Games on {headerDrive}"
            : SelectedView switch
            {
                LibraryView.Favorites => "Favorites",
                LibraryView.Recent => "Recently played",
                _ => "All games",
            };
        LibrarySubheaderText = $"{Games.Count} {(Games.Count == 1 ? "game" : "games")}";

        // Footer counts what's on screen against the whole library, so "6 of 6" vs "2 of 6" is itself
        // the signal that something (a view, a drive, a search, a disabled source) is narrowing it.
        var totalGames = _allGames.Count;
        FooterCountText = $"{Games.Count} of {totalGames} {(totalGames == 1 ? "game" : "games")} shown";
    }
}
