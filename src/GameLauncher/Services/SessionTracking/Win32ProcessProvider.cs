using System.Diagnostics;

namespace GameLauncher.Services.SessionTracking;

internal sealed class Win32ProcessProvider : IProcessProvider
{
    public IReadOnlyList<IGameProcess> FindProcessesByName(IEnumerable<string> names)
    {
        var found = new List<IGameProcess>();

        foreach (var name in names)
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var process in matches)
                found.Add(new Win32GameProcess(process));
        }

        return found;
    }

    public IGameProcess Wrap(Process process) => new Win32GameProcess(process);
}
