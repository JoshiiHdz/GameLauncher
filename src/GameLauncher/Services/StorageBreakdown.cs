using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>One game's slice of a drive.</summary>
public sealed record StorageRow(GameEntry Game, string Name, string SizeDisplay, double Fraction, string ShareText);

/// <summary>"What's eating my drive": the games on one drive ranked by measured install size. Sizes are measured in the background and
/// only approximate, so a game that has not been measured yet is counted separately instead of being shown as zero.</summary>
public sealed record StorageSnapshot(
    IReadOnlyList<StorageRow> Rows,
    int UnmeasuredCount,
    long GamesBytes,
    long? OtherBytes)
{
    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => !HasRows;

    public bool HasUnmeasured => UnmeasuredCount > 0;

    public string UnmeasuredText => UnmeasuredCount == 1 ? "1 game is still being measured..." : $"{UnmeasuredCount} games are still being measured...";

    public bool HasOther => OtherBytes is not null;

    public string GamesTotalText => $"Games: {DriveSpaceInfo.FormatGb(GamesBytes)}";

    public string OtherText => OtherBytes is { } other ? $"Everything else on the drive: {DriveSpaceInfo.FormatGb(other)}" : "";
}

public static class StorageBreakdown
{
    /// <param name="gamesOnDrive">Every game whose install folder is on the drive (hidden ones included - they still take space).</param>
    /// <param name="usedBytes">Space used on the whole drive, or null when the drive could not be read.</param>
    /// <param name="totalBytes">Drive capacity, or 0 when unknown (no share-of-drive figure is shown then).</param>
    public static StorageSnapshot Build(IEnumerable<GameEntry> gamesOnDrive, long? usedBytes, long totalBytes)
    {
        var all = gamesOnDrive.ToList();
        var measured = all.Where(g => g.InstallSizeBytes is not null)
            .OrderByDescending(g => g.InstallSizeBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unmeasured = all.Count - measured.Count;
        var largest = measured.Count > 0 ? measured[0].InstallSizeBytes!.Value : 0;
        var gamesBytes = measured.Sum(g => g.InstallSizeBytes!.Value);

        var rows = measured.Select(g =>
        {
            var bytes = g.InstallSizeBytes!.Value;
            var fraction = totalBytes > 0 ? (double)bytes / totalBytes : 0;
            var share = totalBytes <= 0 ? "" : fraction < 0.01 ? "under 1% of drive" : $"{Math.Round(fraction * 100):0}% of drive";
            return new StorageRow(g, g.Name, g.InstallSizeDisplay, largest > 0 ? (double)bytes / largest : 0, share);
        }).ToList();

        // "Everything else" is only honest once every game has been measured; before that it would swallow the unmeasured ones.
        long? other = unmeasured == 0 && usedBytes is { } used ? Math.Max(0, used - gamesBytes) : null;
        return new StorageSnapshot(rows, unmeasured, gamesBytes, other);
    }
}
