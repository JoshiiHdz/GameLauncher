namespace GameLauncher.Models;

/// <summary>Records a dedup merge where BOTH the surviving (winner) and superseded (loser) entries had
/// their own explicit, DIFFERENT user-selected cover - the winner's selection stays active (see
/// LibraryViewModel.MigrateMergedOverrides), but the loser's would otherwise become completely
/// unreferenced the moment its GameOverride is removed, making it eligible for eventual cleanup with no
/// way to ever discover it existed. Recorded here instead so the loser's pick has a durable, referenced
/// home - not a full reconciliation UI (that's separate future work), just a guarantee that "preserved"
/// is actually true rather than "logged, then silently lost."</summary>
public sealed class ArtworkConflict
{
    public string WinnerGameId { get; set; } = "";

    /// <summary>The superseded entry's stable id - not a display name, which isn't available by the
    /// time a merge is detected (GameOverride carries no Name field, and the loser's GameEntry itself is
    /// already gone). Still enough to cross-reference against logs from the same merge.</summary>
    public string LoserGameId { get; set; } = "";

    public ArtworkSelection LoserSelection { get; set; } = null!;
    public DateTime DetectedAt { get; set; }
}
