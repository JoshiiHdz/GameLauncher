using System.Diagnostics;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

internal sealed class FakeProcessProvider : IProcessProvider
{
    private readonly List<FakeGameProcess> _running = [];

    public FakeGameProcess AddRunning(int id, string processName, string? path, DateTimeOffset? startTimeUtc = null)
    {
        var process = new FakeGameProcess(id, processName, path, startTimeUtc);
        _running.Add(process);
        return process;
    }

    /// <summary>Simulates the process terminating: completes its WaitForExitAsync and removes it from
    /// future FindProcessesByName results, mirroring the OS dropping an exited process from the
    /// process table.</summary>
    public void Exit(FakeGameProcess process)
    {
        _running.Remove(process);
        process.SignalExited();
    }

    public IReadOnlyList<IGameProcess> FindProcessesByName(IEnumerable<string> names)
    {
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return _running.Where(p => nameSet.Contains(p.ProcessName)).Cast<IGameProcess>().ToList();
    }

    public IGameProcess Wrap(Process process) =>
        throw new NotSupportedException(
            "Tests call GameSessionWatcher's internal IGameProcess overload directly with a FakeGameProcess " +
            "instead of routing a real Process through Wrap.");
}
