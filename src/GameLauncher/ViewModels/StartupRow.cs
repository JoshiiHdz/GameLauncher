using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>One startup program on the Optimize page: its name, what it runs, and whether Windows starts it at sign-in.</summary>
public sealed partial class StartupRow : ObservableObject
{
    private readonly StartupAppsService _service;
    private readonly Action _changed;
    private bool _quiet;

    internal StartupRow(StartupApp app, StartupAppsService service, Action changed)
    {
        App = app;
        _service = service;
        _changed = changed;
        _quiet = true;
        IsOn = app.Enabled;
        _quiet = false;
    }

    public StartupApp App { get; }

    public string Name => App.Name;

    /// <summary>The command, shortened to something readable.</summary>
    public string Command => App.Command.Length > 90 ? App.Command[..90] + "..." : App.Command;

    /// <summary>A security or Windows component is shown but not switchable from here.</summary>
    public bool CanSwitch => !App.Protected;

    public string Note => App.Protected ? "Security or Windows component - left as it is" : "";

    /// <summary>What the program says it is (its description and publisher, read from the file). A line that says so when the file says nothing, so no row is a bare name.</summary>
    [ObservableProperty]
    private string _about = "Reading what this program is...";

    internal async Task LoadAboutAsync()
    {
        string? text = null;
        try
        {
            var command = App.Command;
            text = await Task.Run(() => StartupAppInfo.Describe(command));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Startup apps: could not read what '{App.Name}' is.", ex);
        }

        var file = StartupAppInfo.ExecutableOf(App.Command) is { } path ? System.IO.Path.GetFileName(path) : null;
        About = text ?? (file is null ? "No description is available for this entry." : $"The file ({file}) does not say what it is. If you do not recognise it, searching for the file name will tell you.");
    }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (_quiet)
            return;

        if (!_service.SetEnabled(App.Id, value))
        {
            _quiet = true;
            IsOn = !value; // refused: the switch goes back
            _quiet = false;
            return;
        }

        _changed();
    }
}
