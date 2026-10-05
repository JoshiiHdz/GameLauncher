using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameLauncher.ViewModels;

/// <summary>The sidebar's collapsible sections. Tools can always be folded away; Launchers and Drives only when there is more than one
/// of them to fold (with a single launcher or drive the section is just that one row, nothing to collapse). Remembered between runs.</summary>
public partial class LibraryViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolsFolded))]
    private bool _isToolsExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchersFolded))]
    private bool _isLaunchersExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrivesFolded))]
    private bool _isDrivesExpanded = true;

    /// <summary>More than one launcher has games, so the Launchers section can be folded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchersFolded))]
    private bool _hasMultipleLaunchers;

    /// <summary>More than one drive holds games, so the Drives section can be folded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrivesFolded))]
    private bool _hasMultipleDrives;

    public bool ToolsFolded => !IsToolsExpanded;
    public bool LaunchersFolded => HasMultipleLaunchers && !IsLaunchersExpanded;
    public bool DrivesFolded => HasMultipleDrives && !IsDrivesExpanded;

    partial void OnIsToolsExpandedChanged(bool value) => SaveSidebarState();

    partial void OnIsLaunchersExpandedChanged(bool value) => SaveSidebarState();

    partial void OnIsDrivesExpandedChanged(bool value) => SaveSidebarState();

    private void SaveSidebarState()
    {
        _settings.SidebarToolsExpanded = IsToolsExpanded;
        _settings.SidebarLaunchersExpanded = IsLaunchersExpanded;
        _settings.SidebarDrivesExpanded = IsDrivesExpanded;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleTools() => IsToolsExpanded = !IsToolsExpanded;

    [RelayCommand]
    private void ToggleLaunchers()
    {
        if (HasMultipleLaunchers)
            IsLaunchersExpanded = !IsLaunchersExpanded;
    }

    [RelayCommand]
    private void ToggleDrives()
    {
        if (HasMultipleDrives)
            IsDrivesExpanded = !IsDrivesExpanded;
    }
}
