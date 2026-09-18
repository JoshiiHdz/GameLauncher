namespace GameLauncher.Services.SessionTracking;

/// <summary>One current top-level "gameplay-candidate" window: has a title, isn't owned by another
/// window (a dialog/tooltip typically is), isn't a WS_EX_TOOLWINDOW utility window, and is large enough
/// to plausibly be a game's own render surface - see Win32GameWindowProvider for the exact heuristic.
/// "Candidate" is deliberately weak: a launcher/splash window routinely passes it too - see
/// GameWindowTracker for how a candidate earns enough trust to actually be armed as the real gameplay
/// signal. Title is carried only for logging - GameWindowTracker never re-derives identity from it.</summary>
public sealed record GameWindow(nint Handle, int ProcessId, string Title);

/// <summary>
/// Finds and tracks the OS-level top-level windows GameWindowTracker uses as its preferred game-exit
/// signal - the seam that keeps GameWindowTracker's state machine testable without touching Win32.
///
/// Window handles, unlike process handles, are never access-protected: any process can query any other
/// process's HWND validity through the window manager, regardless of anti-cheat or elevation - there is
/// no equivalent of Win32GameProcess/CheckPresence's access-denial handling needed here. This is
/// precisely why window lifecycle is a better exit signal than process lifecycle for restoring the
/// launcher: it can't be denied the way waiting on a protected process can.
/// </summary>
public interface IGameWindowProvider
{
    /// <summary>Every current gameplay-candidate top-level window owned by one of `processIds`. Only used
    /// to find a NEW candidate - initial discovery, and recognizing a replacement window during the
    /// post-destruction debounce (see GameWindowTracker) - never to re-confirm an already-tracked handle's
    /// continued existence (see IsWindowAlive for that): a window can legitimately stop matching the
    /// "candidate" heuristic - e.g. briefly losing its title during a fullscreen switch - without having
    /// been destroyed, so re-running this filter against a known handle could wrongly conclude it's gone.
    /// Must never throw.</summary>
    IReadOnlyList<GameWindow> FindGameplayWindows(IReadOnlySet<int> processIds);

    /// <summary>Whether the window at `handle` still exists AND is still owned by `expectedProcessId` -
    /// folds "does this HWND still exist" and "hasn't been recycled for an unrelated process in the
    /// meantime" into one answer, since either failure means the identity this caller cares about is
    /// gone. Windows can and does reuse HWND values once a window is destroyed, the same way it can reuse
    /// a PID once a process exits - checking existence alone (as an earlier version of this fix did)
    /// would let a stale handle be silently mistaken for the original window. Never gated by visibility,
    /// minimized state, size, or title - only actual destruction or reuse makes this false. Must never
    /// throw.</summary>
    bool IsWindowAlive(nint handle, int expectedProcessId);

    /// <summary>The handle of the current OS foreground window (0/IntPtr.Zero if none - e.g. the desktop
    /// itself is focused). Used only to help GameWindowTracker decide whether an observed candidate is
    /// worth trusting as the real gameplay window - see its arming criteria - never to gate basic
    /// liveness (IsWindowAlive already covers minimized/occluded windows correctly on its own). Must
    /// never throw.</summary>
    nint GetForegroundWindow();
}
