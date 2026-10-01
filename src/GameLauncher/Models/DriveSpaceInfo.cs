using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

/// <summary>A snapshot of one drive's space, for the sidebar's "Drives" list. Snapshotted at refresh
/// time rather than kept live - space used changes slowly enough that re-reading on demand (Settings
/// opening, or Refresh) is all that's needed, no background polling.</summary>
public sealed partial class DriveSpaceInfo : ObservableObject
{
    public required string Letter { get; init; }
    public required string Label { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }

    /// <summary>True while this drive is the active library filter - observable (not a plain bool) so
    /// the row's highlight updates in place when LibraryViewModel.SelectedDriveLetter changes, rather
    /// than needing the whole Drives collection rebuilt.</summary>
    [ObservableProperty]
    private bool _isSelected;

    private long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>0-1, for a ScaleTransform on the used-space bar.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string SummaryText => $"{FormatGb(UsedBytes)} used of {FormatGb(TotalBytes)} "
        + $"({FormatGb(FreeBytes)} free)";

    private static string FormatGb(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";
}
