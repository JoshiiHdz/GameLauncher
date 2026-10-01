using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel
{
    private readonly IStartupRegistration? _startupRegistration;

    [ObservableProperty]
    private bool _startWithWindows;

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
