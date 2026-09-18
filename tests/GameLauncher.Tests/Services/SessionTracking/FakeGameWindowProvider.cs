using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

/// <summary>In-memory IGameWindowProvider for scripting GameWindowTracker scenarios. A window only stops
/// appearing (FindGameplayWindows) and answering alive (IsWindowAlive) once the test calls Destroy() -
/// mirroring a real HWND, which the window manager reports as valid right up until it's actually torn
/// down, with no in-between "about to close" state.
///
/// Deliberately has no concept of minimized/visible/hidden at all - matching IGameWindowProvider's own
/// contract that visibility must never gate "is this still a candidate" - so there is nothing for a test
/// to even toggle that could accidentally affect FindGameplayWindows/IsWindowAlive; a window this fake
/// tracks is exactly as alive whether or not a real player would currently see it on screen.</summary>
internal sealed class FakeGameWindowProvider : IGameWindowProvider
{
    private readonly Dictionary<nint, GameWindow> _alive = [];
    private readonly HashSet<nint> _hiddenFromSearch = [];
    private nint _nextHandle = 1;
    private nint _foregroundHandle;

    public GameWindow AddWindow(int processId, string title = "Game")
    {
        var window = new GameWindow(_nextHandle++, processId, title);
        _alive[window.Handle] = window;
        return window;
    }

    /// <summary>Adds a window at a specific, caller-chosen handle rather than the next auto-incrementing
    /// one - used to simulate Windows recycling an exact HWND VALUE for a brand-new window belonging to a
    /// different (but possibly still tracked) process, the scenario history keyed on (handle, pid)
    /// together - not the handle alone - has to tell apart from the original identity.</summary>
    public GameWindow AddWindowWithHandle(nint handle, int processId, string title = "Game")
    {
        var window = new GameWindow(handle, processId, title);
        _alive[window.Handle] = window;
        return window;
    }

    /// <summary>Simulates the window being destroyed (closed, crashed, or its owning process exiting) -
    /// it stops appearing in FindGameplayWindows and IsWindowAlive starts returning false for its handle,
    /// permanently, the same way a real HWND never becomes valid again once torn down.</summary>
    public void Destroy(GameWindow window) => _alive.Remove(window.Handle);

    /// <summary>Simulates Windows recycling this exact HWND value for a genuinely different, unrelated
    /// window belonging to `newProcessId` - the window is never removed (it's still "discoverable"),
    /// only its owning identity changes, the same way SimulatePidReusedByDifferentProcess models PID
    /// reuse on the process side.</summary>
    public void SimulateHandleReusedByDifferentProcess(GameWindow window, int newProcessId) =>
        _alive[window.Handle] = window with { ProcessId = newProcessId };

    /// <summary>Simulates a still-alive window temporarily failing the real candidate heuristic (e.g. a
    /// fullscreen switch briefly clearing its title) - IsWindowAlive keeps reporting it alive, only
    /// FindGameplayWindows stops returning it, exactly the gap GameWindowTracker must not mistake for
    /// destruction.</summary>
    public void TemporarilyExcludeFromCandidateSearch(GameWindow window) => _hiddenFromSearch.Add(window.Handle);

    public void IncludeInCandidateSearch(GameWindow window) => _hiddenFromSearch.Remove(window.Handle);

    /// <summary>Sets which window (if any) currently holds the OS foreground - GameWindowTracker only
    /// ever arms, or accepts as a replacement, whichever window this says is foreground at the time.
    /// Pass null to simulate nothing this fake tracks holding focus (some other, untracked window, or
    /// the desktop itself).</summary>
    public void SetForeground(GameWindow? window) => _foregroundHandle = window?.Handle ?? 0;

    public IReadOnlyList<GameWindow> FindGameplayWindows(IReadOnlySet<int> processIds) =>
        _alive.Values.Where(w => processIds.Contains(w.ProcessId) && !_hiddenFromSearch.Contains(w.Handle)).ToList();

    public bool IsWindowAlive(nint handle, int expectedProcessId) =>
        _alive.TryGetValue(handle, out var window) && window.ProcessId == expectedProcessId;

    public nint GetForegroundWindow() => _foregroundHandle;
}
