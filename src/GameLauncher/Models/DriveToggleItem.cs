using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

/// <summary>One drive in Settings > Library > "Drives to search": a switch that reads and writes straight through to the view model's
/// ignored-drive list, so the list and the sidebar's 3-dot menu are two views of one fact.</summary>
public sealed class DriveToggleItem : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    public DriveToggleItem(string letter, string label, string sizeText, Func<bool> get, Action<bool> set)
    {
        Letter = letter;
        Label = label;
        SizeText = sizeText;
        _get = get;
        _set = set;
    }

    public string Letter { get; }
    public string Label { get; }
    public string SizeText { get; }

    /// <summary>"Local Disk (D:)".</summary>
    public string Title => $"{Label} ({Letter})";

    /// <summary>On = the drive is searched and its games are shown.</summary>
    public bool IsSearched
    {
        get => _get();
        set
        {
            if (_get() == value)
                return;
            _set(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the switch after the drive was switched from somewhere else (the sidebar menu).</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsSearched));
}
