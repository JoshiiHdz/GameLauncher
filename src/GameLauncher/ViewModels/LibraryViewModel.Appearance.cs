using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Which of the three looks the launcher wears (Settings > Appearance > Theme). The view model only records the choice; the
/// window applies it (it owns the resources), so the library and everything running in it carry on untouched.</summary>
public partial class LibraryViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAxisTheme), nameof(IsConsoleTileTheme), nameof(IsConsoleRibbonTheme), nameof(IsConsoleTheme), nameof(ControllerModeAvailable), nameof(ShowConsoleCaption))]
    private ThemeId _appearanceTheme = ThemeId.Axis;

    /// <summary>Set by the window in the PlayStation style: brings a game up on the home screen with its details showing and Play highlighted.</summary>
    internal Action<GameEntry>? ShowGameOnRibbonHome { get; set; }

    public bool IsAxisTheme => AppearanceTheme == ThemeId.Axis;
    public bool IsConsoleTileTheme => AppearanceTheme == ThemeId.ConsoleTile;
    public bool IsConsoleRibbonTheme => AppearanceTheme == ThemeId.ConsoleRibbon;
    public bool IsConsoleTheme => AppearanceTheme != ThemeId.Axis;

    partial void OnAppearanceThemeChanged(ThemeId value)
    {
        // A theme the user chose is theirs for good: nothing is handed back later. (The theme controller mode borrows is not saved, so Axis is still what a restart opens.)
        if (!_themeChangeIsForController)
            _themeBeforeController = null;

        if (value != ThemeId.ConsoleRibbon)
            ExitControllerMode($"the theme changed to {value}"); // switching to another theme ends controller mode and removes every way back into it

        EnterControllerModeFromAxisCommand.NotifyCanExecuteChanged();
        if (_themeChangeIsForController)
            return;

        _settings.AppearanceTheme = value.ToString();
        if (!_settingsService.Save(_settings))
            Logger.Warn("The theme choice could not be saved; it applies for this session only.");
    }

    /// <summary>Picks a theme by its id ("Axis", "ConsoleTile" or "ConsoleRibbon"); an unknown id changes nothing.</summary>
    [RelayCommand]
    private void ChangeTheme(string? id)
    {
        if (Enum.TryParse<ThemeId>(id, ignoreCase: true, out var theme) && Enum.IsDefined(theme) && ThemeManager.IsAvailable(theme))
        {
            // Choosing the theme that controller mode is borrowing makes it the user's own: it is kept, and nothing is handed back when the mode ends.
            if (theme == AppearanceTheme && _themeBeforeController is not null)
            {
                _themeBeforeController = null;
                _settings.AppearanceTheme = theme.ToString();
                _settingsService.Save(_settings);
            }

            AppearanceTheme = theme;
        }
    }
}
