using System.Runtime.InteropServices;
using System.Text;

namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// Production IGameWindowProvider: enumerates top-level windows via the same kind of system-wide,
/// no-per-process-access-rights Win32 calls FindProcessesByName relies on for process discovery -
/// EnumWindows walks the window manager's own list, not anything scoped to (or deniable by) an individual
/// process, so this needs none of Win32GameProcess's access-denial fallbacks.
/// </summary>
internal sealed class Win32GameWindowProvider : IGameWindowProvider
{
    // A game's real render surface is comfortably larger than any dialog/toast/overlay utility window a
    // launcher or anti-cheat stack tends to spawn alongside it - not a documented OS constant, just an
    // empirical floor picked the same way GameSessionWatcherOptions' own timings were: generous enough
    // that no genuine game window should ever sit anywhere near it, small enough that no real game
    // window - including a modest windowed-mode one - should ever be mistaken for a utility window.
    private const int MinCandidateWidth = 200;
    private const int MinCandidateHeight = 150;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>rcNormalPosition (not the live window rect) is used for the size check - GetWindowRect
    /// reports an off-screen, not-reliably-sized rectangle for a minimized window on some Windows
    /// versions, while WINDOWPLACEMENT.rcNormalPosition keeps reporting the window's restored size
    /// regardless of its current minimized/maximized state. This is exactly what lets a minimized
    /// gameplay window still pass the size heuristic on first detection - see IGameWindowProvider's
    /// remarks on why visibility/minimized state must never gate "is this still a candidate."</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    public IReadOnlyList<GameWindow> FindGameplayWindows(IReadOnlySet<int> processIds)
    {
        var found = new List<GameWindow>();

        EnumWindows((hWnd, _) =>
        {
            if (IsGameplayCandidate(hWnd, processIds, out var window))
                found.Add(window!);

            return true; // keep enumerating - EnumWindows stops early only when the callback returns false
        }, nint.Zero);

        return found;
    }

    /// <summary>GetWindowThreadProcessId returning 0 means the handle no longer identifies any window at
    /// all (an invalid/destroyed HWND); a non-zero result belonging to a DIFFERENT pid than expected means
    /// Windows has recycled this exact HWND value for an unrelated window since it was captured - both
    /// cases must be treated as "gone" for this caller's identity, not just the first one.</summary>
    public bool IsWindowAlive(nint handle, int expectedProcessId)
    {
        var threadId = GetWindowThreadProcessId(handle, out var pid);
        return threadId != 0 && pid == expectedProcessId;
    }

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    private static bool IsGameplayCandidate(nint hWnd, IReadOnlySet<int> processIds, out GameWindow? window)
    {
        window = null;

        GetWindowThreadProcessId(hWnd, out var pid);
        if (!processIds.Contains((int)pid))
            return false;

        // A window owned by another window is a dialog/tooltip/popup, not a top-level game surface.
        if (GetWindow(hWnd, GW_OWNER) != nint.Zero)
            return false;

        // WS_EX_TOOLWINDOW marks a utility window (e.g. a floating toolbar) deliberately excluded from
        // the taskbar/alt-tab - the same signal Explorer itself uses to decide what counts as a "real"
        // application window.
        if ((GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
            return false;

        var titleLength = GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return false;

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref placement))
            return false;

        var width = placement.rcNormalPosition.Right - placement.rcNormalPosition.Left;
        var height = placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top;
        if (width < MinCandidateWidth || height < MinCandidateHeight)
            return false;

        var titleBuffer = new StringBuilder(titleLength + 1);
        GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);

        window = new GameWindow(hWnd, (int)pid, titleBuffer.ToString());
        return true;
    }
}
