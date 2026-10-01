using System.Diagnostics;

namespace GameLauncher.Services.SessionTracking;

/// <summary>For whole-library passive discovery: enumerate once per check, not once per game name.
/// Only matching names proceed to the more expensive executable-path query.</summary>
internal sealed class SnapshotProcessProvider : IProcessProvider
{
    private readonly Func<IReadOnlyList<IGameProcess>> _snapshot;

    public SnapshotProcessProvider() : this(() => Process.GetProcesses()
        .Select(p => (IGameProcess)new Win32GameProcess(p)).ToArray()) { }

    internal SnapshotProcessProvider(Func<IReadOnlyList<IGameProcess>> snapshot) => _snapshot = snapshot;

    public IReadOnlyList<IGameProcess> FindProcessesByName(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];
        var all = _snapshot();
        var matched = new List<IGameProcess>();
        try
        {
            foreach (var process in all)
            {
                if (wanted.Contains(process.ProcessName)) matched.Add(process);
            }
        }
        catch
        {
            foreach (var process in all) process.Dispose();
            throw;
        }
        var retained = matched.ToHashSet();
        foreach (var process in all)
            if (!retained.Contains(process)) process.Dispose();
        return matched;
    }

    public IGameProcess Wrap(Process process) => new Win32GameProcess(process);
}
