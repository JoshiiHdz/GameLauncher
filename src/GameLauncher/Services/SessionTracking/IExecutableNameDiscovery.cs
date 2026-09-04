namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// Finds the candidate executable names to watch for, given a game's install directory.
/// FileSystemExecutableNameDiscovery is the production implementation (the recursive exe walk); tests
/// substitute a fixed or scripted list - e.g. one that changes between calls, to exercise
/// WaitForHandoffAsync's re-scan (an exe appearing partway through the handoff wait).
/// </summary>
public interface IExecutableNameDiscovery
{
    HashSet<string> GetCandidateNames(string installDir);
}
