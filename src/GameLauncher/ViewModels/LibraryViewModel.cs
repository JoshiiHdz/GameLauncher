using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

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
    private readonly AppSettings _settings;
    private List<GameEntry> _allGames = new();

    /// <summary>Read once at startup from AppSettings.EnableWindowExitDiagnostics - no UI toggle yet (see
    /// its own remarks), so a settings.json edit needs a restart to take effect. Passed to
    /// GameSessionOrchestrator, which is the only thing that ever acts on it.</summary>
    public bool EnableWindowExitDiagnostics { get; }

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

    [ObservableProperty]
    private bool _minimizeToTrayOnMinimize;

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
        IsNeverPlayedViewSelected = value == LibraryView.NeverPlayed;
        IsDuplicatesViewSelected = value == LibraryView.Duplicates;
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectView(string? view)
    {
        ClosePage();
        var parsed = view switch
        {
            "favorites" => LibraryView.Favorites,
            "recent" => LibraryView.Recent,
            "unplayed" => LibraryView.NeverPlayed,
            "duplicates" => LibraryView.Duplicates,
            _ => LibraryView.All,
        };

        // Order matters: clearing the drive re-runs ApplyFilter on its own, so setting the view second
        // means the pass that actually paints the grid is the one that already sees both changes.
        SelectedDriveLetter = null;
        SelectedCollection = null;
        if (SelectedView == parsed)
            ApplyFilter(); // same view re-clicked: still honour the drive clear above
        else
            SelectedView = parsed;
    }

    /// <summary>True once a scan has found a game from any launcher with a sidebar switch (computed from the full,
    /// unfiltered scan result in ApplyFilter, so switching a launcher off can never remove its own switch). Hides the
    /// LAUNCHERS header when there is nothing under it.</summary>
    [ObservableProperty]
    private bool _hasAnySourceGames;

    public ObservableCollection<GameEntry> Games { get; } = new();

    public ObservableCollection<GameEntry> FavoriteGames { get; } = new();

    public ObservableCollection<GameEntry> HiddenGames { get; } = new();

    public ObservableCollection<WatchedFolder> WatchedFolders { get; } = new();

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
        if (ApplyFirstRunStartupDefault(startupRegistration))
            _startWithWindows = true;
        _startMaximized = _settings.StartMaximized;
        _appearanceTheme = ThemeManager.Parse(_settings.AppearanceTheme);
        _findGamesWithoutLauncher = _settings.FindGamesWithoutLauncher;
        _isToolsExpanded = _settings.SidebarToolsExpanded;
        _isLaunchersExpanded = _settings.SidebarLaunchersExpanded;
        _isDrivesExpanded = _settings.SidebarDrivesExpanded;
        foreach (var folder in _settings.WatchedFolders)
            WatchedFolders.Add(folder);
        _steamGridDbApiKey = _settings.SteamGridDbApiKey ?? string.Empty;
        _igdbClientId = _settings.IgdbClientId ?? string.Empty;
        _igdbSecretSaved = _credentialStore.HasSecret();
        _vibrantBackground = _settings.VibrantBackground;
        _minimizeToTrayWhileGaming = _settings.MinimizeToTrayWhileGaming;
        _minimizeToTrayOnMinimize = _settings.MinimizeToTrayOnMinimize;
        InitializeControllerMap();
        _optimizeBeforeLaunch = _settings.OptimizeBeforeLaunch;
        _focusPlayEnabled = _settings.FocusPlay;
        _focusPlayOnlyWhenPluggedIn = _settings.FocusPlayOnlyWhenPluggedIn;
        EndFocusPlay(); // a switch an earlier crash or power cut never undid: put the power plan back now
        _globalHotkeyEnabled = _settings.GlobalHotkeyEnabled;
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
        _detectManual = _settings.DetectManual;
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

    /// <summary>Read-only test seam mirroring RunningGameId - lets tests assert directly on an
    /// override's state (e.g. after MigrateMergedOverrides) without a public Overrides accessor.</summary>
    internal GameOverride? GetOverride(string gameId) => _settings.Overrides.GetValueOrDefault(gameId);

    partial void OnMinimizeToTrayWhileGamingChanged(bool value)
    {
        _settings.MinimizeToTrayWhileGaming = value;
        _settingsService.Save(_settings);
    }

    partial void OnMinimizeToTrayOnMinimizeChanged(bool value)
    {
        _settings.MinimizeToTrayOnMinimize = value;
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

    partial void OnSearchTextChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            ClosePage(); // searching is about the library: leave Settings/Stats/etc. so the results are visible

        ApplyFilter();
    }

    partial void OnSortOptionChanged(GameSortOption value) => ApplyFilter();

    partial void OnShowHiddenGamesChanged(bool value) => ShowHiddenSection = value && HasHiddenGames;

    /// <summary>ApplyFilter runs on every keystroke, sort change and refresh, and clearing then re-adding a collection
    /// rebuilds every card even when nothing changed - so a collection is only touched when its contents really differ.</summary>
    /// <summary>Raised once after a filter pass that changed the Games or Recently played list. A list is rebuilt one game at a time, so listening to its own
    /// change events would run the ribbon's whole rebuild once per game.</summary>
    internal event Action? ListsChanged;

    private static bool ReplaceIfChanged<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items))
            return false;

        target.Clear();
        foreach (var item in items)
            target.Add(item);

        return true;
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
        // A collection that no longer has any game clears itself, which re-enters here and paints; this pass is then stale.
        if (SyncCollections())
            return;

        // The same: leaving a Duplicates view that has emptied re-enters here and paints.
        if (SyncDuplicates())
            return;

        UpdateViewCounts();

        // Computed from the full, un-filtered scan result (not the Detect-filtered list below), so
        // disabling a source can never make its own sidebar row disappear. One pass, not one scan per launcher.
        var present = _allGames.Select(g => g.Source).ToHashSet();
        HasAnySourceGames = SourceItems.Any(i => present.Contains(i.Source));
        HasMultipleLaunchers = SourceItems.Count(i => present.Contains(i.Source)) > 1;
        foreach (var item in SourceItems)
            item.Refresh(present.Contains(item.Source));

        // The drive and search narrowing applies to visible and hidden games alike.
        var selectedDrive = SelectedDriveLetter;
        var search = SearchText;
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var selectedCollection = SelectedCollection;
        bool Matches(GameEntry g) => IsSourceEnabled(g.Source)
            && (selectedCollection is null || InCollection(g, selectedCollection))
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
            LibraryView.NeverPlayed => filtered.Where(g => !g.HasPlayTime),
            // The copies of one game sit next to each other, whatever sort is chosen.
            LibraryView.Duplicates => filtered.Where(g => g.HasDuplicate)
                .OrderBy(g => DuplicateDetector.KeyFor(g.DetectedTitle), StringComparer.Ordinal).ThenBy(g => g.Source),
            _ => filtered,
        };

        var ordered = filtered.ToList();

        // The hero only shows on the unfiltered All view - searching, filtering to a drive, or
        // switching views hides it rather than pinning a game the grid no longer shows. "Jump back in"
        // means most recently PLAYED; until anything has been played, the newest addition stands in,
        // since that's the only real signal available then.
        var isHomeView = SelectedView == LibraryView.All
            && SelectedDriveLetter is null
            && SelectedCollection is null
            && string.IsNullOrWhiteSpace(SearchText);

        FeaturedGame = isHomeView
            ? ordered.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).FirstOrDefault()
                ?? ordered.OrderByDescending(g => g.DateAdded).FirstOrDefault()
            : null;
        HasFeaturedGame = FeaturedGame is not null;
        HeroAccentColor = (FeaturedGame?.IsCoverArt == true ? AccentFor(FeaturedGame.Icon) : null) ?? DefaultHeroAccentColor;

        // "Recently played": only games with real tracked sessions, newest first, capped so the strip
        // stays a strip. Empty (and hidden) until something has actually been played through the app.
        var listsChanged = ReplaceIfChanged(RecentlyPlayedGames, isHomeView
            ? ordered.Where(g => g.HasPlayTime).OrderByDescending(g => g.LastPlayedUtc).Take(10).ToList()
            : new List<GameEntry>());
        HasRecentlyPlayed = RecentlyPlayedGames.Count > 0;

        ReplaceIfChanged(FavoriteGames, ordered.Where(g => g.Favorite).ToList());
        listsChanged |= ReplaceIfChanged(Games, ordered);
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
            && string.IsNullOrWhiteSpace(SearchText) && SelectedDriveLetter is null && SelectedCollection is null;
        var hasViewEntries = _allGames.Any(g => !g.Hidden && (SelectedView switch
        {
            LibraryView.Favorites => g.Favorite,
            LibraryView.Recent => g.HasPlayTime,
            LibraryView.NeverPlayed => !g.HasPlayTime,
            LibraryView.Duplicates => g.HasDuplicate,
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
            LibraryView.NeverPlayed when !hasViewEntries => ("Every game has been played",
                "All of your games have play time tracked by Axis Game Launcher."),
            LibraryView.NeverPlayed => ("No unplayed games match your filters",
                "Try another search, drive, or launcher filter to see the games you haven't played yet."),
            LibraryView.Duplicates => ("No duplicates match your filters",
                "Try another search, drive, or launcher filter to see games installed through more than one launcher."),
            _ when SelectedCollection is not null => ("No games match this collection",
                "Try another search, drive, or launcher filter, or add more games to it from a game's right-click menu."),
            _ when ShowEmptyDiscoveryActions => ("No games found yet",
                "Games are auto-detected from Steam, Epic, GOG and more, or add a folder to scan."),
            _ => ("No games match this view",
                "Try another search, drive, or launcher filter, or check your hidden games."),
        };

        LibraryHeaderText = (SelectedCollection, SelectedDriveLetter) switch
        {
            ({ } collection, { } drive) => $"{collection} on {drive}",
            ({ } collection, null) => collection,
            (null, { } headerDrive) => $"Games on {headerDrive}",
            _ => SelectedView switch
            {
                LibraryView.Favorites => "Favorites",
                LibraryView.Recent => "Recently played",
                LibraryView.NeverPlayed => "Not played yet",
                LibraryView.Duplicates => "Installed twice",
                _ => "All games",
            },
        };
        LibrarySubheaderText = $"{Games.Count} {(Games.Count == 1 ? "game" : "games")}"
            + (SelectedView == LibraryView.NeverPlayed ? " with no tracked play time" : "")
            + (SelectedView == LibraryView.Duplicates ? " installed through more than one launcher" : "");

        // Footer counts what's on screen against the whole library, so "6 of 6" vs "2 of 6" is itself
        // the signal that something (a view, a drive, a search, a disabled source) is narrowing it.
        var totalGames = _allGames.Count;
        FooterCountText = $"{Games.Count} of {totalGames} {(totalGames == 1 ? "game" : "games")} shown";

        if (listsChanged)
            ListsChanged?.Invoke();
    }
}
