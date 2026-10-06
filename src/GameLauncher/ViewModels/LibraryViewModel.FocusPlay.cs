using CommunityToolkit.Mvvm.ComponentModel;
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

    partial void OnFocusPlayAvailableChanged(bool value) => OnPropertyChanged(nameof(HasFocusPlayStatus));

    partial void OnFocusPlayCheckedChanged(bool value) => OnPropertyChanged(nameof(HasFocusPlayStatus));

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
    internal bool BeginFocusPlay()
    {
        if (!FocusPlayEnabled)
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

    /// <summary>Called with the ids of the game's processes once they are found: raises them to High priority when Focus play is on and the
    /// game is set to it. Best effort - a game that refuses (run as administrator, anti-cheat) is reported, not treated as an error.</summary>
    internal void OnGameProcessesFound(GameEntry game, IReadOnlySet<int> processIds)
    {
        if (!FocusPlayEnabled || processIds.Count == 0 || !GetRaiseGamePriority(game.Id))
            return;

        var raised = GamePriority.Raise(processIds);
        Logger.Info($"Focus play: raised {raised} of {processIds.Count} process(es) of '{game.Name}' to High priority.");
        if (raised == 0)
            StatusText = $"Focus play couldn't raise {game.Name}'s priority - Windows or the game's protection didn't allow it.";
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
    }
}
