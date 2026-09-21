namespace GameLauncher.Models;

public enum ScanPhase
{
    /// <summary>Walking the launchers (Steam, Epic, GOG...) and watched folders for installed games.</summary>
    LookingForGames,

    /// <summary>Working out which game each entry is and fetching its cover - where nearly all of a scan's time goes.</summary>
    IdentifyingGames,
}

/// <summary>One step of a library scan, for the progress bar. `Done` is how many items of `Total` are finished when this is reported;
/// `Item` names what is being worked on now. The percentage is real, not a spinner: the launcher pass (fast file and registry reads)
/// is the first 15%, and identifying games and fetching covers fills the rest in proportion to games done.</summary>
public readonly record struct ScanProgress(ScanPhase Phase, int Done, int Total, string? Item = null)
{
    private const double LauncherShare = 15;

    public double Percent
    {
        get
        {
            var fraction = Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
            return Phase == ScanPhase.LookingForGames ? LauncherShare * fraction : LauncherShare + (100 - LauncherShare) * fraction;
        }
    }

    public string Text => Phase == ScanPhase.LookingForGames
        ? (string.IsNullOrWhiteSpace(Item) ? "Looking for games..." : $"Looking for games... {Item}")
        : Total <= 0
            ? "Identifying games..."
            : $"Identifying games... {Math.Clamp(Done, 0, Total)} of {Total}" + (string.IsNullOrWhiteSpace(Item) ? "" : $" - {Item}");
}
