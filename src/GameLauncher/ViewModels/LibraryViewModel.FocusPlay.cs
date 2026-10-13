using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Focus play: a high performance power plan while a game launched from here runs (put back afterwards), and an optional per-game High
/// priority for the game's own processes. Off by default; see FocusPlayService for how the original plan is kept safe.</summary>
public partial class LibraryViewModel
{
    /// <summary>Makes the Windows power-plan access the library uses. Tests replace it, so no test can ever change a real PC's power plan.</summary>
    internal static Func<IPowerPlans> PowerPlansFactory { get; set; } = () => new PowerPlans();

    private IPowerPlans _powerPlans = PowerPlansFactory();
    private FocusPlayService? _focusPlay;

    /// <summary>The Windows power plans; replaced in tests so no real power plan is ever changed.</summary>
    internal IPowerPlans PowerPlansService
    {
        get => _powerPlans;
        set
        {
            _powerPlans = value;
            _focusPlay = null;
            _focusPlayCheck = null;
            FocusPlayChecked = false;
            FocusPlayAvailable = false;
        }
    }

    private FocusPlayService FocusPlayControl => _focusPlay ??= new FocusPlayService(_powerPlans, _settings, SaveSettingsOnUiThread);

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    /// <summary>The availability check runs on a worker thread, but the settings object and its file belong to the UI thread (play time is saved there), so a
    /// save asked for from the worker is carried out on the UI thread: two saves at once would collide on the file, and one while the UI edits a list would fail.</summary>
    private void SaveSettingsOnUiThread()
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            _settingsService.Save(_settings);
        else
            _uiContext.Send(_ => _settingsService.Save(_settings), null);
    }

    // ---- Is Focus play possible on this PC? Found out by trying, once per run, when Settings > Performance is first opened. ----

    /// <summary>False until the check finds that this PC can switch to a high performance plan; the Focus play switch stays off-limits until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusPlayStatusText))]
    private bool _focusPlayAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusPlayStatusText))]
    private bool _focusPlayChecked;

    private string _focusPlayProblem = "";
    private Task? _focusPlayCheck;

    /// <summary>"Checking this PC...", the reason Focus play can't be used here, or nothing when it can.</summary>
    public string FocusPlayStatusText => !FocusPlayChecked ? "Checking whether this PC can use it..." : FocusPlayAvailable ? "" : _focusPlayProblem;

    public bool HasFocusPlayStatus => FocusPlayStatusText.Length > 0;

    partial void OnFocusPlayAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(HasFocusPlayStatus));
        UpdatePowerRow();
    }

    partial void OnFocusPlayCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasFocusPlayStatus));
        UpdatePowerRow();
    }

    /// <summary>Starts the check (once; asking again while it runs or after it has run does nothing). It runs off the UI thread, because switching the
    /// power plan can take a moment.</summary>
    public Task CheckFocusPlayAvailabilityAsync()
    {
        if (_focusPlayCheck is not null)
            return _focusPlayCheck;

        return _focusPlayCheck = RunFocusPlayCheckAsync();
    }

    private async Task RunFocusPlayCheckAsync()
    {
        FocusPlayAvailability result;
        try
        {
            var service = FocusPlayControl;
            result = await Task.Run(service.CheckAvailability);
        }
        catch (Exception ex)
        {
            Logger.Warn("Focus play: the availability check failed.", ex);
            result = new(false, "Focus play couldn't check this PC's power plans.");
        }

        _focusPlayProblem = result.Message;
        FocusPlayAvailable = result.Available;
        FocusPlayChecked = true;
        if (!result.Available)
            Logger.Info($"Focus play isn't available on this PC: {result.Message}");
    }

    [ObservableProperty]
    private bool _focusPlayEnabled;

    partial void OnFocusPlayEnabledChanged(bool value)
    {
        _settings.FocusPlay = value;
        _settingsService.Save(_settings);
        RefreshGameTweakRows(); // the list shows the same switch
        if (!value)
            EndFocusPlay(); // turned off mid-game: the plan goes back now
    }

    [ObservableProperty]
    private bool _focusPlayOnlyWhenPluggedIn = true;

    partial void OnFocusPlayOnlyWhenPluggedInChanged(bool value)
    {
        _settings.FocusPlayOnlyWhenPluggedIn = value;
        _settingsService.Save(_settings);
    }

    /// <summary>Called just before a game is launched from here. True when this launch switched the power plan (so it, and only it, may undo the switch if the
    /// launch fails: a switch already in force belongs to a game that is still running).</summary>
    internal bool BeginFocusPlay() => BeginFocusPlay(FocusPlayEnabled);

    /// <summary>Same, for a launch that was asked for the plan (a game's own list can ask for it while the default list does not).</summary>
    internal bool BeginFocusPlay(bool wanted)
    {
        if (!wanted)
            return false;

        try
        {
            var result = FocusPlayControl.Begin(FocusPlayOnlyWhenPluggedIn);
            Logger.Info($"Focus play: {result.Outcome}. {result.Message}".TrimEnd());
            if (result.Outcome is FocusPlayOutcome.Switched or FocusPlayOutcome.NotAvailable && !string.IsNullOrEmpty(result.Message))
                StatusText = result.Message;

            return result.Outcome == FocusPlayOutcome.Switched;
        }
        catch (Exception ex)
        {
            // Never let a power-plan problem stop a game from starting.
            Logger.Warn("Focus play failed; launching anyway.", ex);
            return false;
        }
    }

    /// <summary>Puts the original power plan back when the game has ended (or could not be found), the app closes, or the setting is turned off.
    /// Also runs at start, which undoes a switch an earlier crash or power cut never got to undo. Does nothing when nothing was switched.</summary>
    public void EndFocusPlay()
    {
        try
        {
            FocusPlayControl.End();
        }
        catch (Exception ex)
        {
            Logger.Warn("Focus play: putting the power plan back failed.", ex);
        }
    }

    /// <summary>Called with the ids of the game's processes as they are found: raises them to High priority when the game is set to it. Best effort - a game that
    /// refuses (run as administrator, anti-cheat) is reported, not treated as an error. Every process is named in the log with what happened to it.</summary>
    internal void OnGameProcessesFound(GameEntry game, IReadOnlySet<int> processIds)
    {
        // The per-game switch is the person's own choice for this game: it does not wait on the power plan being on as well.
        if (processIds.Count == 0 || !GetRaiseGamePriority(game.Id))
            return;

        var results = GamePriority.RaiseEach(processIds);
        LogRaise(game, results);
        if (results.All(r => !r.Raised))
            StatusText = $"High priority: {game.Name}'s processes didn't allow it - Windows or the game's protection refused.";
    }

    private static void LogRaise(GameEntry game, IReadOnlyList<RaiseResult> results)
    {
        foreach (var r in results)
        {
            Logger.Info(r.Raised
                ? $"High priority: '{game.Name}' {r.Name} (pid {r.Id}) is on High."
                : $"High priority: '{game.Name}' {r.Name} (pid {r.Id}) would not take it ({r.Why}).");
        }
    }

    /// <summary>The game's own process is often not there yet when it is first noticed (a launcher or an anti-cheat stub comes first, the game itself a few seconds
    /// later), so raising only what was running at that moment left the game on Normal. This keeps looking while the session lasts - every two seconds for the first
    /// three minutes, then every fifteen - and raises any process of the game it has not tried yet. Ends with <paramref name="ct"/> (the session).</summary>
    internal async Task KeepGamePriorityAsync(GameEntry game, Func<IReadOnlyCollection<int>> findRunning, CancellationToken ct,
        TimeSpan? quick = null, TimeSpan? slow = null, TimeSpan? quickFor = null)
    {
        var every = quick ?? TimeSpan.FromSeconds(2);
        var then = slow ?? TimeSpan.FromSeconds(15);
        var quickWindow = quickFor ?? TimeSpan.FromMinutes(3);
        var started = DateTime.UtcNow;
        var tried = new HashSet<int>();
        var raisedOk = new HashSet<int>();
        var lastLook = DateTime.UtcNow;
        var told = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DateTime.UtcNow - started < quickWindow ? every : then, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!GetRaiseGamePriority(game.Id))
                continue;

            IReadOnlyCollection<int> running;
            try
            {
                running = await Task.Run(findRunning, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"High priority: could not look for '{game.Name}' processes this time.", ex);
                continue;
            }

            // Every 20 seconds, ask Windows again what the raised processes are on: a game (or its anti-cheat) can put its priority back, and the log should show it.
            if (DateTime.UtcNow - lastLook > TimeSpan.FromSeconds(20) && raisedOk.Count > 0)
            {
                lastLook = DateTime.UtcNow;
                foreach (var id in raisedOk.ToList())
                {
                    var now = GamePriority.Current(id);
                    if (now is null)
                    {
                        raisedOk.Remove(id); // gone
                    }
                    else if (now is not ("High" or "RealTime"))
                    {
                        Logger.Warn($"High priority: '{game.Name}' pid {id} is on {now} again (it was put back) - raising it again.");
                        LogRaise(game, GamePriority.RaiseEach([id]));
                    }
                }
            }

            var fresh = running.Where(tried.Add).ToList();
            if (fresh.Count == 0)
                continue;

            var results = GamePriority.RaiseEach(fresh);
            LogRaise(game, results);
            foreach (var r in results.Where(r => r.Raised))
                raisedOk.Add(r.Id);

            if (!told && results.Any(r => r.Raised))
            {
                told = true;
                StatusText = $"{game.Name} is running at high priority.";
            }
        }
    }

    internal bool GetRaiseGamePriority(string gameId) => _settings.Overrides.GetValueOrDefault(gameId)?.RaiseGamePriority ?? false;

    internal void SetRaiseGamePriority(string gameId, bool value)
    {
        if (!_settings.Overrides.TryGetValue(gameId, out var over))
        {
            if (!value)
                return;

            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        over.RaiseGamePriority = value;
        _settingsService.Save(_settings);
        foreach (var game in _allGames.Where(g => g.Id == gameId))
            game.HighPriority = value;
    }

    /// <summary>The "High priority" item of a game's menu (Axis's card menu, the More menus, the home cards): one click, no page to open first.</summary>
    [RelayCommand]
    private void ToggleHighPriority(GameEntry? game)
    {
        if (game is null)
            return;

        var on = !GetRaiseGamePriority(game.Id);
        SetRaiseGamePriority(game.Id, on);
        StatusText = on
            ? $"{game.Name} will run at high priority while it plays (some games refuse it)."
            : $"{game.Name} runs at normal priority.";
    }
}
