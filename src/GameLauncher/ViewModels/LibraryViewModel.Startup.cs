using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel
{
    private readonly IStartupRegistration? _startupRegistration;

    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>Open the launcher maximized. On by default.</summary>
    [ObservableProperty]
    private bool _startMaximized = true;

    /// <summary>Also look for games that no launcher installed (repacks, pre-installed or copied folders) on every drive that is switched on.
    /// On by default; Settings > Library can turn it off.</summary>
    [ObservableProperty]
    private bool _findGamesWithoutLauncher = true;

    /// <summary>True while Reset puts many settings back at once - it rescans once itself, so each change must not start its own scan.</summary>
    private bool _bulkSettingChange;

    partial void OnFindGamesWithoutLauncherChanged(bool value)
    {
        _settings.FindGamesWithoutLauncher = value;
        _settingsService.Save(_settings);
        if (_bulkSettingChange)
            return;

        // Off removes what the search found, on brings it in: either way the library is scanned again, and on does a fresh look at the disks.
        _ = (RescanAfterChange ?? (value ? ForcedRescan : DefaultRescan))();
    }

    partial void OnStartMaximizedChanged(bool value)
    {
        _settings.StartMaximized = value;
        _settingsService.Save(_settings);
    }

    /// <summary>On the very first run the launcher starts with Windows, as most launchers do; it can be switched off in Settings, and
    /// that choice is then kept. Only a first run does this, so nobody who already turned it off is switched back on, and only for an
    /// installed copy (a loose or portable copy could be moved or deleted). If it cannot be set up (no built exe, no shell), nothing is
    /// saved and the next run simply tries again.</summary>
    private bool ApplyFirstRunStartupDefault(IStartupRegistration? registration)
    {
        if (!_settings.IsFreshInstall || registration is null || registration.IsEnabled || !registration.SuitableForDefaultStartup)
            return false;

        try
        {
            registration.SetEnabled(true);
            _settings.StartWithWindows = true;
            _settingsService.Save(_settings);
            Logger.Info("First run: Start with Windows is on by default.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("First run: couldn't turn on Start with Windows by default.", ex);
            return false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            if (_startupRegistration is null)
                throw new InvalidOperationException("Startup registration is unavailable in this host.");
            _startupRegistration.SetEnabled(value);
            _settings.StartWithWindows = value;
            _settingsService.Save(_settings);
            StatusText = value ? "Axis Game Launcher will start when you sign in to Windows."
                : "Start with Windows is turned off.";
        }
        catch (Exception ex)
        {
            // Restore the bound toggle without recursively trying the operation again.
            _startWithWindows = !value;
            OnPropertyChanged(nameof(StartWithWindows));
            StatusText = "Couldn't change Windows startup. " + ex.Message;
            Logger.Warn("Couldn't change the Axis Game Launcher startup shortcut.", ex);
        }
    }
}
