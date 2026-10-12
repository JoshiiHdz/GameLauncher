using System.Runtime.CompilerServices;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.TestSupport;

/// <summary>Runs once when the test assembly loads. No test may ever edit the registry of the PC it runs on, or stop a real Windows service, so every library view model
/// starts with this pretend registry (and no service guard) unless a test supplies its own.</summary>
internal static class NoRealRegistry
{
    [ModuleInitializer]
    internal static void Install()
    {
        LibraryViewModel.UserRegistryFactory = () => new PretendRegistry();
        ServiceGuard.StartForTest = _ => null;
        LibraryViewModel.ServiceStatesFactory = () => new NoServices(); // and none asks the real service manager
        LibraryViewModel.StartupSourceFactory = () => new EmptyStartup(); // no test reads (or changes) the startup list of the PC it runs on
        LibraryViewModel.WindowsOldSizerFactory = () => null; // and none walks a real Windows.old
    }
}

internal sealed class PretendRegistry : IUserRegistry
{
    public Dictionary<(string Key, string Name), string> Values { get; } = new();

    public string? Read(string key, string name) => Values.GetValueOrDefault((key, name));

    public void Write(string key, string name, string encoded) => Values[(key, name)] = encoded;

    public void Delete(string key, string name) => Values.Remove((key, name));
}

/// <summary>A service list where the services are known by the test: nothing is asked of Windows.</summary>
internal sealed class NoServices : IServiceStates
{
    public Dictionary<string, bool> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? IsRunning(string service) => Running.TryGetValue(service, out var running) ? running : null;
}

internal sealed class EmptyStartup : IStartupSource
{
    public IReadOnlyList<(string Name, string Command)> RunEntries() => [];
    public IReadOnlyList<string> FolderEntries() => [];
    public bool? ReadApproved(string area, string name) => null;
    public void WriteApproved(string area, string name, bool enabled) { }
}
