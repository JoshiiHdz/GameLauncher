using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Services.SessionTracking;

/// <summary>Executable names inside a game's install dir, used to cheaply shortlist processes by name
/// before doing the more expensive path check on just those. Generic installer/crash-reporter noise
/// (GameExeFinder.NoiseExcludePatterns) is filtered out here too: without this, a name match against
/// something like UnityCrashHandler64.exe or a redist installer could trip GameSessionWatcher's
/// anti-cheat path-unreadable fallback and make an unrelated crash/installer process look like "the
/// game is still running." Deliberately keeps "trial"/"anticheat"-named exes as valid candidates unlike
/// the exe-picker - those can be genuine handoff targets to watch for.</summary>
internal sealed class FileSystemExecutableNameDiscovery : IExecutableNameDiscovery
{
    // Widened from 3 to 6 after a live report: if a game's real executable lives deeper in its install
    // folder than this scan goes (common for titles that split content into many nested package/DLC
    // folders), its name never makes it into the candidate set at all - so the handoff check can never
    // find it running, no matter how long it waits, and restores the window over a game that's
    // actually still open.
    private const int MaxScanDepth = 6;

    public HashSet<string> GetCandidateNames(string installDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(installDir, 0);
        return names;

        void Collect(string dir, int depth)
        {
            if (depth > MaxScanDepth)
                return;

            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var name = Path.GetFileNameWithoutExtension(exe);
                    if (!GameExeFinder.NoiseExcludePatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        names.Add(name);
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Collect(sub, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
