using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Controller mode: the PlayStation theme driven from a gamepad (see docs/design/controller-mode-plan.md). It is a mode of that theme, not a
/// separate screen and not a theme of its own; this holds only whether it is on. The window reads the pad, goes full screen and scales things up.</summary>
public partial class LibraryViewModel
{
    /// <summary>Controller mode exists only in the PlayStation theme; in any other theme there is no way into it.</summary>
    public bool ControllerModeAvailable => IsConsoleRibbonTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConsoleCaption))]
    private bool _isControllerMode;

    /// <summary>The thin caption (title and window buttons) the console themes draw at the top. Controller mode has no caption: it is full screen.</summary>
    public bool ShowConsoleCaption => IsConsoleTheme && !IsControllerMode;

    /// <summary>A game is running somewhere (one launched from here, or one the launcher noticed). It does NOT stop the pad: what stops the launcher reading
    /// the pad is not being the window in front, which is true while a game has the screen. A game left running behind the launcher must not lock the pad
    /// out. Used for the log only.</summary>
    internal bool HasRunningGame => _allGames.Any(g => g.IsRunning);

    /// <summary>The theme the user was in (Axis) when controller mode was entered from somewhere other than the PlayStation theme. Controller mode only exists in the PlayStation
    /// theme, so entering it from Axis puts that theme on for the session - never saved - and leaving the mode puts this one back. Null when the user was already in the
    /// PlayStation theme (or has since chosen a theme themselves).</summary>
    private ThemeId? _themeBeforeController;

    /// <summary>True while this code is changing the theme for controller mode (as opposed to the user choosing one).</summary>
    private bool _themeChangeIsForController;

    /// <summary>Controller mode was started from another theme and will hand it back when it ends.</summary>
    public bool ControllerModeBorrowsTheme => _themeBeforeController is not null;

    partial void OnIsControllerModeChanged(bool value)
    {
        StatusText = value ? "Controller mode on" : "Controller mode off";
        if (!value)
            ReturnTheme();
    }

    private void ReturnTheme()
    {
        if (_themeBeforeController is not { } before)
            return;

        _themeBeforeController = null;
        _themeChangeIsForController = true;
        try { AppearanceTheme = before; }
        finally { _themeChangeIsForController = false; }
    }

    /// <summary>Turns controller mode on. From the PlayStation theme that is all it does; from Axis it first puts the PlayStation theme on for the session (see
    /// <see cref="_themeBeforeController"/>), so there is no trip through Settings.</summary>
    internal void EnterControllerMode()
    {
        if (IsControllerMode)
            return;

        if (!ControllerModeAvailable)
        {
            _themeBeforeController = AppearanceTheme;
            _themeChangeIsForController = true;
            try { AppearanceTheme = ThemeId.ConsoleRibbon; }
            finally { _themeChangeIsForController = false; }
            Logger.Info($"Controller mode: the PlayStation theme is on for this session ({_themeBeforeController} comes back when it ends).");
        }

        IsControllerMode = true;
    }

    /// <summary>Axis's way in: the title bar button, F11, the palette. Only offered in Axis (the PlayStation theme has its own button and no shortcuts).</summary>
    [RelayCommand(CanExecute = nameof(IsAxisTheme))]
    private void EnterControllerModeFromAxis() => EnterControllerMode();

    internal void ExitControllerMode(string reason = "asked to")
    {
        if (IsControllerMode)
            Logger.Info($"Leaving controller mode: {reason}.");

        IsControllerMode = false;
    }

    [RelayCommand]
    private void ToggleControllerMode()
    {
        if (IsControllerMode)
            ExitControllerMode();
        else
            EnterControllerMode();
    }

    // ---- Is a controller connected? (the status pill: Axis and PlayStation) ---------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControllerPillText), nameof(ControllerPillTooltip), nameof(ControllerBatteryLow))]
    private bool _controllerConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControllerPillText), nameof(ControllerPillTooltip), nameof(ControllerBatteryLow))]
    private PadBattery? _controllerBattery;

    /// <summary>Set by the window from the pad reader: whether a controller is plugged in, and its battery when it reports one.</summary>
    internal void SetControllerStatus(bool connected, PadBattery? battery)
    {
        ControllerConnected = connected;
        ControllerBattery = connected ? battery : null;
    }

    /// <summary>The pill's words: "Not connected", "Connected", or "Connected · Medium" (the battery level, when the controller reports one).</summary>
    public string ControllerPillText => !ControllerConnected ? "Not connected"
        : ControllerBattery is { Wired: false } battery ? $"Connected · {battery.Level}" : "Connected";

    public string ControllerPillTooltip => !ControllerConnected
        ? "No controller connected. Plug one in, or pair a wireless Xbox controller. A PlayStation controller needs Steam Input or DS4Windows."
        : ControllerBattery is { Wired: false } battery
            ? $"Controller connected · {battery.Describe()}. Windows does not report a controller's signal strength."
            : "Controller connected. It reports no battery: it is on a cable, or its receiver (dongle) does not pass the battery on. Windows does not report signal strength either.";

    /// <summary>The battery is low or empty: the pill's dot turns amber.</summary>
    public bool ControllerBatteryLow => ControllerConnected && ControllerBattery is { NeedsCharging: true };

    // ---- Button mapping (Settings > Appearance > Controller mode) ---------------------------------------------------------

    /// <summary>What each remappable button does. A and B are fixed; see <see cref="ControllerMap"/>.</summary>
    public ControllerMap ControllerMap { get; } = new();

    /// <summary>One line per remappable button for the settings list.</summary>
    public ObservableCollection<ControllerButtonRow> ControllerButtonRows { get; } = new();

    private bool _loadingControllerMap;

    private void InitializeControllerMap()
    {
        foreach (var button in ControllerMap.Remappable)
            ControllerButtonRows.Add(new ControllerButtonRow(button));

        _loadingControllerMap = true;
        ControllerMap.Load(_settings.ControllerButtons);
        _loadingControllerMap = false;
        RefreshControllerRows();

        ControllerMap.Changed += () =>
        {
            RefreshControllerRows();
            if (_loadingControllerMap)
                return;

            _settings.ControllerButtons = ControllerMap.ToSettings();
            _settingsService.Save(_settings);
        };
    }

    private void RefreshControllerRows()
    {
        foreach (var row in ControllerButtonRows)
            row.Refresh(ControllerMap);

        ResetControllerButtonsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A press on a button's row moves it to its next job: its default, then Settings, Library, Optimize, Rescan library, Nothing, and round again.</summary>
    [RelayCommand]
    private void CycleControllerButton(ControllerButtonRow? row)
    {
        if (row is not null)
            ControllerMap.Cycle(row.Button);
    }

    [RelayCommand(CanExecute = nameof(CanResetControllerButtons))]
    private void ResetControllerButtons()
    {
        ControllerMap.Reset();
        StatusText = "Controller buttons are back to their defaults.";
    }

    private bool CanResetControllerButtons() => !ControllerMap.IsAllDefault;
}

/// <summary>A row in the controller button list: the button, and what it does now.</summary>
public sealed partial class ControllerButtonRow(PadButton button) : ObservableObject
{
    public PadButton Button { get; } = button;

    public string ButtonName => ControllerMap.ButtonName(Button);

    /// <summary>The button as it looks on the pad (see PadGlyphView), shown beside its job in Settings.</summary>
    public PadGlyph Glyph => PadHintItem.GlyphOf(Button) ?? PadGlyph.A;

    /// <summary>"Default · Search" while on the default, otherwise the chosen job.</summary>
    [ObservableProperty]
    private string _functionText = string.Empty;

    [ObservableProperty]
    private bool _isCustom;

    internal void Refresh(ControllerMap map)
    {
        IsCustom = !map.IsDefault(Button);
        FunctionText = IsCustom ? ControllerMap.Describe(map.Resolve(Button)) : $"Default · {ControllerMap.Describe(ControllerMap.DefaultFor(Button))}";
    }
}
