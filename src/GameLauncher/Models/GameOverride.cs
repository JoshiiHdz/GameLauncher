namespace GameLauncher.Models;

/// <summary>Per-game user customization, keyed by GameEntry.Id in AppSettings.Overrides. Id is
/// stable across exe-repick fixes (every scanner derives it from an install dir, registry id, or
/// app id - never from ExecutablePath), unlike ExecutablePath itself, which can change whenever a
/// scanner's "which .exe is the real game" heuristic is corrected. Keying by Id used to be keyed by
/// ExecutablePath, which silently orphaned a game's Favorite/Hidden/DateAdded the moment its picked
/// exe changed - the EA trial-exe fix was a real, confirmed instance of this.</summary>
public sealed class GameOverride
{
    public string? CustomName { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public DateTime? DateAdded { get; set; }
}
