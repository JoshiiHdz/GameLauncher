using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>Which launchers' games are shown: the per-launcher switches and the sidebar's launcher list.</summary>
public partial class LibraryViewModel
{
    /// <summary>A sidebar switch is a library action: flipping one while a page is open returns to the library so the change is visible.</summary>
    private Action<bool> Leaving(Action<bool> set) => v =>
    {
        set(v);
        ClosePage();
    };

    /// <summary>The launchers found on this PC, as one list for both sidebar layouts. Built in the
    /// constructor (it closes over the Detect properties); entries show only once a scan finds games.</summary>
    public IReadOnlyList<SourceToggleItem> SourceItems { get; private set; } = Array.Empty<SourceToggleItem>();

    private IReadOnlyList<SourceToggleItem> BuildSourceItems() => new[]
    {
        new SourceToggleItem(GameSource.Steam, "Steam", "S", () => DetectSteam, Leaving(v => DetectSteam = v)),
        new SourceToggleItem(GameSource.Epic, "Epic Games", "E", () => DetectEpic, Leaving(v => DetectEpic = v)),
        new SourceToggleItem(GameSource.Gog, "GOG", "G", () => DetectGog, Leaving(v => DetectGog = v)),
        new SourceToggleItem(GameSource.Xbox, "Xbox", "X", () => DetectXbox, Leaving(v => DetectXbox = v)),
        new SourceToggleItem(GameSource.Ea, "EA app", "EA", () => DetectEa, Leaving(v => DetectEa = v)),
        new SourceToggleItem(GameSource.Ubisoft, "Ubisoft Connect", "U", () => DetectUbisoft, Leaving(v => DetectUbisoft = v)),
        new SourceToggleItem(GameSource.BattleNet, "Battle.net", "B", () => DetectBattleNet, Leaving(v => DetectBattleNet = v)),
        new SourceToggleItem(GameSource.Rockstar, "Rockstar Games", "R", () => DetectRockstar, Leaving(v => DetectRockstar = v)),
        new SourceToggleItem(GameSource.AmazonGames, "Amazon Games", "A", () => DetectAmazonGames, Leaving(v => DetectAmazonGames = v)),
        // Games added from a folder that no launcher owns (or that carry no launcher marker): their own row, last in the list.
        new SourceToggleItem(GameSource.Manual, "No launcher", "M", () => DetectManual, Leaving(v => DetectManual = v)),
    };

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
    private bool _detectManual = true;

    partial void OnDetectSteamChanged(bool value) => SaveDetectSetting(v => _settings.DetectSteam = v, value);

    partial void OnDetectEpicChanged(bool value) => SaveDetectSetting(v => _settings.DetectEpic = v, value);

    partial void OnDetectGogChanged(bool value) => SaveDetectSetting(v => _settings.DetectGog = v, value);

    partial void OnDetectXboxChanged(bool value) => SaveDetectSetting(v => _settings.DetectXbox = v, value);

    partial void OnDetectEaChanged(bool value) => SaveDetectSetting(v => _settings.DetectEa = v, value);

    partial void OnDetectUbisoftChanged(bool value) => SaveDetectSetting(v => _settings.DetectUbisoft = v, value);

    partial void OnDetectBattleNetChanged(bool value) => SaveDetectSetting(v => _settings.DetectBattleNet = v, value);

    partial void OnDetectRockstarChanged(bool value) => SaveDetectSetting(v => _settings.DetectRockstar = v, value);

    partial void OnDetectAmazonGamesChanged(bool value) => SaveDetectSetting(v => _settings.DetectAmazonGames = v, value);

    partial void OnDetectManualChanged(bool value) => SaveDetectSetting(v => _settings.DetectManual = v, value);

    // Scanning always runs for every source now (see GameScannerService), so a toggle only needs to
    // re-filter the already-scanned library, not trigger a fresh scan - instant instead of a
    // multi-second rescan for what's really just a visibility change.
    private void SaveDetectSetting(Action<bool> apply, bool value)
    {
        apply(value);
        _settingsService.Save(_settings);
        ApplyFilter();
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
        GameSource.Manual => DetectManual,
        _ => true,
    };
}
