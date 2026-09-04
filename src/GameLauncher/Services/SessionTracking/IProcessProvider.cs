using System.Diagnostics;

namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// Finds and wraps OS processes. The one seam GameSessionWatcher uses for process discovery - both
/// the initial scan and every handoff recheck go through FindProcessesByName, so a test can substitute
/// a scripted set of processes instead of depending on real ones.
/// </summary>
public interface IProcessProvider
{
    /// <summary>Every currently-running process whose name exactly matches (case-insensitive) one of
    /// the given candidate names. Must never throw - a discovery failure for one name must not break
    /// the others (mirrors the production adapter's per-name try/catch around
    /// Process.GetProcessesByName).</summary>
    IReadOnlyList<IGameProcess> FindProcessesByName(IEnumerable<string> names);

    /// <summary>Wraps an already-known live process (what Process.Start returned) as an IGameProcess,
    /// for the one place GameSessionWatcher needs to inspect a process it didn't discover by name (the
    /// `launched` parameter of WaitForExitAsync).</summary>
    IGameProcess Wrap(Process process);
}
