namespace GameLauncher.Models;

/// <summary>Per-game user customization, keyed by ExecutablePath in AppSettings.Overrides.</summary>
public sealed class GameOverride
{
    public string? CustomName { get; set; }
    public bool Hidden { get; set; }
    public bool Favorite { get; set; }
    public DateTime? DateAdded { get; set; }
}
