using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Win32;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly GameScannerService _scannerService = new();
    private readonly AppSettings _settings;
    private List<GameEntry> _allGames = new();

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

    [ObservableProperty]
    private string _steamGridDbApiKey = string.Empty;

    [ObservableProperty]
    private bool _vibrantBackground = true;

    [ObservableProperty]
    private bool _minimizeToTrayWhileGaming = true;

    public ObservableCollection<GameEntry> Games { get; } = new();

    public ObservableCollection<GameEntry> FavoriteGames { get; } = new();

    public ObservableCollection<WatchedFolder> WatchedFolders { get; } = new();

    public List<SortOptionItem> SortOptions { get; } =
    [
        new("Name (A-Z)", GameSortOption.NameAsc),
        new("Name (Z-A)", GameSortOption.NameDesc),
        new("Source", GameSortOption.Source),
        new("Favorites First", GameSortOption.FavoritesFirst),
        new("Recently Added", GameSortOption.RecentlyAdded),
    ];

    public LibraryViewModel()
    {
        _settings = _settingsService.Load();
        foreach (var folder in _settings.WatchedFolders)
            WatchedFolders.Add(folder);
        _steamGridDbApiKey = _settings.SteamGridDbApiKey ?? string.Empty;
        _vibrantBackground = _settings.VibrantBackground;
        _minimizeToTrayWhileGaming = _settings.MinimizeToTrayWhileGaming;

        RefreshShortcutState();
    }

    partial void OnMinimizeToTrayWhileGamingChanged(bool value)
    {
        _settings.MinimizeToTrayWhileGaming = value;
        _settingsService.Save(_settings);
    }

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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Scanning...";
        try
        {
            _allGames = await _scannerService.ScanAllAsync(_settings);
            _settingsService.Save(_settings); // persists any newly-assigned DateAdded values
            ApplyFilter();
            StatusText = $"{_allGames.Count(g => !g.Hidden)} games found";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Scan failed.", ex);
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;

            // A scan is a burst of allocation (file/registry walking, decoding cover art) and the
            // app goes idle straight after. Hand back what that burst left resident.
            MemoryTrimmer.Trim();
        }
    }

    [RelayCommand]
    private void AddFolder()
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

        _ = RefreshAsync();
    }

    [RelayCommand]
    private void RemoveFolder(WatchedFolder? folder)
    {
        if (folder is null)
            return;

        WatchedFolders.Remove(folder);
        _settings.WatchedFolders.Remove(folder);
        _settingsService.Save(_settings);

        _ = RefreshAsync();
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

        if (!_settings.Overrides.TryGetValue(game.ExecutablePath, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[game.ExecutablePath] = over;
        }

        over.Favorite = game.Favorite;
        _settingsService.Save(_settings);

        // Always re-filter: the game has to move between the Favorites section and the main grid.
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

    private void ApplyFilter()
    {
        IEnumerable<GameEntry> filtered = _allGames.Where(g => !g.Hidden);

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
        HasNoGames = _allGames.Count(g => !g.Hidden) == 0;
        LibraryHeaderText = $"My Library ({Games.Count} {(Games.Count == 1 ? "Game" : "Games")})";
    }
}
