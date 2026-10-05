using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

public enum DriveSpaceLevel
{
    Ok,
    Low,
    Critical,
}

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

    /// <summary>True while the user has switched this drive off: it is not searched and none of its games are shown. The sidebar keeps the
    /// row (dimmed) so it can be switched back on from the same place.</summary>
    [ObservableProperty]
    private bool _isIgnored;

    private long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>Below this a drive can't take another big game, whatever its size.</summary>
    internal const long CriticalFreeBytes = 10L << 30;

    /// <summary>Below this the drive is running low. A percentage rule alone would nag on a 4 TB drive with 300 GB free,
    /// so the percentage only counts while less than <see cref="LowFreeFractionCapBytes"/> is actually free.</summary>
    internal const long LowFreeBytes = 25L << 30;

    internal const double LowFreeFraction = 0.10;
    internal const long LowFreeFractionCapBytes = 100L << 30;

    public DriveSpaceLevel Level =>
        FreeBytes < CriticalFreeBytes ? DriveSpaceLevel.Critical
        : FreeBytes < LowFreeBytes
            || (FreeBytes < LowFreeFractionCapBytes && TotalBytes > 0 && (double)FreeBytes / TotalBytes < LowFreeFraction)
            ? DriveSpaceLevel.Low
            : DriveSpaceLevel.Ok;

    public bool IsLow => Level == DriveSpaceLevel.Low;

    public bool IsCritical => Level == DriveSpaceLevel.Critical;

    /// <summary>The sidebar warning line; empty while there is plenty of room.</summary>
    public string WarningText => Level switch
    {
        DriveSpaceLevel.Critical => $"Almost full - only {FormatGb(FreeBytes)} free",
        DriveSpaceLevel.Low => $"Running low - {FormatGb(FreeBytes)} free",
        _ => "",
    };

    public bool HasWarning => Level != DriveSpaceLevel.Ok;

    /// <summary>0-1, for a ScaleTransform on the used-space bar.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string SummaryText => $"{FormatGb(UsedBytes)} used of {FormatGb(TotalBytes)} "
        + $"({FormatGb(FreeBytes)} free)";

    internal static string FormatGb(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.#} GB";
}
