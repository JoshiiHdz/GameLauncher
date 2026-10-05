namespace GameLauncher.Models;

/// <summary>A drive the user told the launcher to leave alone: nothing on it is searched, and nothing already found on it is shown.
/// Remembered by volume serial number as well as letter, so a drive that comes back under another letter (an external disk in a
/// different port) is still the one that was ignored, and a different drive that takes over the old letter is not.</summary>
public sealed class IgnoredDrive
{
    /// <summary>The letter it had when it was ignored, like "D:". Used on its own only when the serial number could not be read.</summary>
    public string Letter { get; set; } = "";

    public uint? VolumeSerial { get; set; }

    /// <summary>The volume label at the time, for the Settings list when the drive is unplugged.</summary>
    public string? Label { get; set; }
}
