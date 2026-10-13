using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>One row of the game tweaks list (Optimize page and Settings > Performance): a tweak, whether it is applied when a game starts, and what Windows has for it
/// right now. The switch is "apply this when a game starts"; the line under it is the live state, so a person can see both.</summary>
public sealed partial class TweakRow : ObservableObject
{
    private readonly Action<string, bool> _changed;

    internal TweakRow(GameTweak tweak, bool on, Action<string, bool> changed)
    {
        Tweak = tweak;
        _changed = changed;
        _quiet = true;
        IsOn = on;
        _quiet = false;
    }

    private bool _quiet;

    public GameTweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Effect => Tweak.Effect;
    public string Group => Tweak.Group;

    /// <summary>"Windows asks permission once per game" for the service tweaks, "No permission needed" for the others.</summary>
    public string Requirement => Tweak.Requirement;

    /// <summary>Switched on in the launcher: it is applied when a game starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownOn))]
    private bool _isOn;

    /// <summary>False when the switch cannot be turned on here (Focus play on a PC that cannot switch power plans). It can always be turned off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _canSwitch = true;

    /// <summary>What Windows has for this tweak right now ("Already on in Windows"); empty until it has been read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWindowsState))]
    private string _windowsState = string.Empty;

    /// <summary>True when the tweak's own target is already how Windows is, so switching it on would change nothing. The switch then shows ON (it is on, while a game
    /// runs) and cannot be moved - there is nothing to choose - and the line under it is drawn in the accent colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownOn), nameof(CanToggle))]
    private bool _alreadyApplied;

    public bool HasWindowsState => WindowsState.Length > 0;

    /// <summary>What the switch shows: on when it is switched on here, or when Windows already has it that way.</summary>
    public bool ShownOn
    {
        get => IsOn || AlreadyApplied;
        set
        {
            if (!AlreadyApplied)
                IsOn = value;
            else
                OnPropertyChanged(); // Windows has it on: the switch stays where it is
        }
    }

    /// <summary>The switch can be moved: it can be switched here, and Windows does not already have it that way.</summary>
    public bool CanToggle => CanSwitch && !AlreadyApplied;

    /// <summary>The longer text (what it is, what it does, what it needs) is open under the row.</summary>
    [ObservableProperty]
    private bool _showDetails;

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet)
            _changed(Tweak.Id, value);
    }

    internal void Set(bool value)
    {
        // Set without calling back: used when the list is rebuilt from the settings (a backup was imported, everything was reset).
        _quiet = true;
        IsOn = value;
        _quiet = false;
    }

    internal void ShowState(TweakWindowsState? state)
    {
        WindowsState = state?.Text ?? string.Empty;
        AlreadyApplied = state?.Applied == true;
    }
}

/// <summary>Game tweaks: temporary, reversible changes that can be applied automatically when a game starts and are undone when it ends (see GameTweakCatalog). The list
/// also holds the two things the launcher itself does before a game (free memory, switch the power plan), so everything that can happen before a game starts is picked in
/// one place, for every game or for one.</summary>
public partial class LibraryViewModel
{
    /// <summary>Makes the registry access the tweaks use. Tests replace it, so no test can ever edit a real registry.</summary>
    internal static Func<IUserRegistry> UserRegistryFactory { get; set; } = () => new CurrentUserRegistry();

    /// <summary>Makes what reads whether a Windows service is running. Tests replace it, so none asks the real service manager.</summary>
    internal static Func<IServiceStates> ServiceStatesFactory { get; set; } = () => new SystemServiceStates();

    private GameTweakEngine? _tweakEngine;
    private IDisposable? _serviceGuard;

    private IUserRegistry? _userRegistry;

    /// <summary>The one registry access both the engine (which writes) and the state reader (which only reads) use.</summary>
    private IUserRegistry UserRegistry => _userRegistry ??= UserRegistryFactory();

    private GameTweakEngine TweakEngine => _tweakEngine ??= new GameTweakEngine(UserRegistry, _settings, SaveSettingsOnUiThread);

    /// <summary>Every tweak with its switch. The same rows are shown on the Optimize page and in Settings.</summary>
    public ObservableCollection<TweakRow> GameTweakRows { get; } = new();

    /// <summary>The rows by heading, for the page: memory and power, Windows settings, background services.</summary>
    public IReadOnlyList<TweakRow> MemoryAndPowerRows => GameTweakRows.Where(r => r.Tweak.Kind == TweakKind.Launcher).ToList();

    public IReadOnlyList<TweakRow> WindowsSettingRows => GameTweakRows.Where(r => r.Tweak.Kind == TweakKind.UserSetting).ToList();

    public IReadOnlyList<TweakRow> ServiceRows => GameTweakRows.Where(r => r.Tweak.Kind == TweakKind.Service).ToList();

    private void InitializeGameTweaks()
    {
        _settings.GameTweaksOnLaunch.RemoveAll(id => GameTweakCatalog.ById(id) is not { } t || t.Kind == TweakKind.Launcher); // a tweak an earlier version had (Error Reporting) is simply gone
        foreach (var tweak in GameTweakCatalog.All)
            GameTweakRows.Add(new TweakRow(tweak, IsOnByDefault(tweak.Id), OnTweakSwitched));

        UpdatePowerRow();
        TweakEngine.End(); // changes an earlier crash or power cut never undid: put them back now
    }

    /// <summary>On in the default list. Memory and the power plan keep their own settings (they existed before the list did); the rest are in the list.</summary>
    private bool IsOnByDefault(string id) => id switch
    {
        GameTweakCatalog.MemoryTrimId => OptimizeBeforeLaunch,
        GameTweakCatalog.PowerPlanId => FocusPlayEnabled,
        _ => _settings.GameTweaksOnLaunch.Contains(id),
    };

    private void OnTweakSwitched(string id, bool on)
    {
        Logger.Info($"Game tweaks: '{id}' switched {(on ? "on" : "off")} in Settings > Performance.");
        switch (id)
        {
            case GameTweakCatalog.MemoryTrimId:
                OptimizeBeforeLaunch = on; // saves itself
                break;
            case GameTweakCatalog.PowerPlanId:
                FocusPlayEnabled = on;
                break;
            default:
                _settings.GameTweaksOnLaunch.RemoveAll(i => i == id);
                if (on)
                    _settings.GameTweaksOnLaunch.Add(id);

                _settingsService.Save(_settings);
                break;
        }

        OnPropertyChanged(nameof(GameTweaksOnCount));
        OnPropertyChanged(nameof(GameTweaksSummary));
    }

    /// <summary>Puts the rows back in step with the settings (after an import or a reset, or when the Settings switches for memory and Focus play change).</summary>
    internal void RefreshGameTweakRows()
    {
        foreach (var row in GameTweakRows)
            row.Set(IsOnByDefault(row.Tweak.Id));

        UpdatePowerRow();
        OnPropertyChanged(nameof(GameTweaksOnCount));
        OnPropertyChanged(nameof(GameTweaksSummary));
    }

    /// <summary>The default list: the tweaks that are on, plus memory and the power plan when those are on.</summary>
    internal IReadOnlyList<string> DefaultTweakIds
    {
        get
        {
            var ids = _settings.GameTweaksOnLaunch.ToList();
            if (OptimizeBeforeLaunch)
                ids.Add(GameTweakCatalog.MemoryTrimId);
            if (FocusPlayEnabled)
                ids.Add(GameTweakCatalog.PowerPlanId);

            return ids;
        }
    }

    /// <summary>The power plan row can be switched on only where Windows lets the launcher switch plans (it can always be switched off), and says why when it cannot.</summary>
    private void UpdatePowerRow()
    {
        if (GameTweakRows.FirstOrDefault(r => r.Tweak.Id == GameTweakCatalog.PowerPlanId) is not { } row)
            return;

        var cannot = FocusPlayChecked && !FocusPlayAvailable;
        row.CanSwitch = !cannot || row.IsOn;
        if (cannot)
            row.ShowState(new(null, "Not available on this PC: " + _focusPlayProblem));
    }

    /// <summary>How many are on when a game starts: the ones switched on here, and the ones Windows already has that way.</summary>
    public int GameTweaksOnCount => GameTweakRows.Count(r => r.ShownOn);

    public string GameTweaksSummary
    {
        get
        {
            var on = GameTweaksOnCount;
            if (on == 0)
                return "None are applied when a game starts.";

            var already = GameTweakRows.Count(r => r.AlreadyApplied && !r.IsOn);
            var windows = already == 0 ? "" : already == 1 ? " (1 is already on in Windows)" : $" ({already} are already on in Windows)";
            return $"{on} of {GameTweakRows.Count} are on when a game starts{windows}.";
        }
    }

    [RelayCommand]
    private void TurnAllGameTweaksOn()
    {
        foreach (var row in GameTweakRows.Where(r => r.CanSwitch))
            row.IsOn = true;
    }

    [RelayCommand]
    private void TurnAllGameTweaksOff()
    {
        foreach (var row in GameTweakRows)
            row.IsOn = false;
    }

    // ---- What Windows has right now, shown beside each switch --------------------------------------------------------------------

    /// <summary>Reads what Windows has for every tweak (read-only, off the UI thread because the service list is asked of Windows) and shows it on the rows. The Optimize page
    /// does this when it opens and again after "Check again".</summary>
    public async Task RefreshTweakStatesAsync()
    {
        try
        {
            var registry = UserRegistry;
            var services = ServiceStatesFactory();
            var memory = MemoryOptimizerService;
            var plans = PowerPlansService;
            var ids = GameTweakCatalog.All.Select(t => t.Id).ToList();

            var states = await Task.Run(() =>
            {
                var read = new Dictionary<string, TweakWindowsState?>();
                foreach (var id in ids)
                {
                    try
                    {
                        read[id] = id switch
                        {
                            GameTweakCatalog.MemoryTrimId => MemoryState(memory),
                            GameTweakCatalog.PowerPlanId => PowerState(plans),
                            _ => TweakStateReader.Read(id, registry, services),
                        };
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Game tweaks: could not read the Windows state of '{id}'.", ex);
                    }
                }

                return read;
            });

            foreach (var row in GameTweakRows)
                row.ShowState(states.GetValueOrDefault(row.Tweak.Id));

            UpdatePowerRow();
            OnPropertyChanged(nameof(GameTweaksOnCount));
            OnPropertyChanged(nameof(GameTweaksSummary));
        }
        catch (Exception ex)
        {
            Logger.Warn("Game tweaks: reading the Windows state failed.", ex);
        }
    }

    private static TweakWindowsState? MemoryState(IMemoryOptimizer memory)
    {
        var status = memory.GetStatus();
        if (status.TotalBytes <= 0)
            return null;

        var plenty = (double)status.AvailableBytes / status.TotalBytes >= MemoryPressureThreshold;
        var free = $"{ByteFormat.Size(status.AvailableBytes)} free of {ByteFormat.Size(status.TotalBytes)}";
        return new(null, plenty ? $"{free} - plenty, so a trim would be skipped" : $"{free} - getting short, so a trim would run");
    }

    private static TweakWindowsState? PowerState(IPowerPlans plans) => plans.GetActive() switch
    {
        { } active when active == FocusPlayService.HighPerformance || active == FocusPlayService.UltimatePerformance =>
            new(true, "Already on a high performance plan"),
        not null => new(false, "On another plan - switched while a game runs, put back after"),
        _ => null,
    };

    /// <summary>Called just before a game is launched from here: applies the tweaks that are switched on. The user-setting ones are applied at once; the service ones start
    /// the elevated copy (Windows asks permission) without making the game wait. Never throws: a tweak problem must not stop a game from starting.</summary>
    internal void BeginGameTweaks(GameEntry game) => BeginGameTweaks(game.Name, game.ExecutablePath, TweaksForGame(game.Id));

    private void BeginGameTweaks(string gameName, string? executable, IReadOnlyList<string> wanted)
    {
        try
        {
            if (wanted.Count == 0)
                return;

            var applied = TweakEngine.Begin(wanted, executable);
            var services = wanted.Where(i => GameTweakCatalog.ById(i) is { Kind: TweakKind.Service }).ToList();
            if (services.Count > 0 && _serviceGuard is null)
                _serviceGuard = ServiceGuard.Start(services);

            if (applied.Count > 0 || _serviceGuard is not null)
                Logger.Info($"Game tweaks for '{gameName}': {string.Join(", ", applied)}{(_serviceGuard is not null ? " + services helper requested (see below for what it paused)" : "")}.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Game tweaks failed; launching anyway.", ex);
        }
    }

    /// <summary>Undoes the tweaks when the game has ended (or could not be found), the app closes, or at start (a crash's leftovers). Does nothing when none were applied.</summary>
    public void EndGameTweaks()
    {
        try
        {
            var something = TweakEngine.IsActive || _serviceGuard is not null;
            if (something)
                Logger.Info("Game tweaks: the game has ended - putting everything back.");

            TweakEngine.End();
            _serviceGuard?.Dispose();
            _serviceGuard = null;
            if (something)
                Logger.Info("Game tweaks: everything is back (settings read again above; services are being started again).");
        }
        catch (Exception ex)
        {
            Logger.Warn("Game tweaks: putting things back failed.", ex);
        }
    }

    // ---- One list for every game ---------------------------------------------------------------------------------------------------
    // (A game used to be able to have its own list. That window is gone: the tweaks are set once, in Settings > Performance, and apply to every game. A list a game
    // was given earlier is left in the saved data, untouched and no longer used.)

    /// <summary>What happens before a game starts: the list from Settings > Performance (memory and the power plan included), the same for every game.</summary>
    internal IReadOnlyList<string> TweaksForGame(string gameId) => DefaultTweakIds;

    /// <summary>Whether games get the high performance power plan.</summary>
    internal bool UsesPowerPlan(string gameId) => TweaksForGame(gameId).Contains(GameTweakCatalog.PowerPlanId);

    /// <summary>Every "Game tweaks..." button and menu row: opens Settings > Performance, where the game tweaks are set for every game.</summary>
    [RelayCommand]
    private void EditGameTweaks(GameEntry? game)
    {
        OpenSettingsAt(PerformanceSettingsCategory);
        StatusText = "Game tweaks are set here, for every game.";
    }

    /// <summary>The Optimize page's "Try them now": applies the switched-on tweaks without a game, so they can be seen working; "Undo" puts everything back.</summary>
    [RelayCommand]
    private void TryGameTweaksNow()
    {
        if (_settings.GameTweaksOnLaunch.Count == 0)
        {
            StatusText = DefaultTweakIds.Count == 0
                ? "Switch some tweaks on first."
                : "Memory and the power plan are applied as a game starts, so there is nothing to try without one. Switch a Windows setting or a service on to try it.";
            return;
        }

        BeginGameTweaks("(a test)", null, _settings.GameTweaksOnLaunch.ToList());
        StatusText = "Game tweaks are on. They stay on until you press Undo (or a game ends).";
    }

    [RelayCommand]
    private void UndoGameTweaks()
    {
        EndGameTweaks();
        StatusText = "Game tweaks are undone.";
    }
}
