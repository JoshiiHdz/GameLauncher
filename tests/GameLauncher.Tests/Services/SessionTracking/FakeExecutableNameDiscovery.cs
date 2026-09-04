using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

/// <summary>Returns a mutable, test-controlled set of candidate names. SetNames lets a scenario script
/// "an exe appears partway through the handoff wait" - WaitForHandoffAsync re-queries this on every
/// poll tick specifically so a name added mid-wait still gets picked up.</summary>
internal sealed class FakeExecutableNameDiscovery : IExecutableNameDiscovery
{
    private HashSet<string> _names;

    public FakeExecutableNameDiscovery(params string[] names) =>
        _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public void SetNames(params string[] names) =>
        _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public HashSet<string> GetCandidateNames(string installDir) => _names;
}
