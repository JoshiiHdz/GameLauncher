namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// A running process, as GameSessionWatcher needs to see it - just enough surface to identify, wait
/// on, and log about it. Win32GameProcess is the production implementation, wrapping a real
/// System.Diagnostics.Process; tests substitute an in-memory fake so scenarios (discovery timeout,
/// handoff, long-session detection, ...) don't depend on spawning or finding real OS processes.
/// </summary>
public interface IGameProcess : IDisposable
{
    int Id { get; }

    /// <summary>Never throws - "unknown" on any access failure, mirroring the production adapter's
    /// fallback for an already-exited or otherwise inaccessible process.</summary>
    string ProcessName { get; }

    /// <summary>The process's own image path, or null if it can't be read (already exited, or access
    /// denied - anti-cheat-protected processes routinely deny even the minimal read level this needs).
    /// Never throws.</summary>
    string? GetPath();

    /// <summary>UTC process creation time, or null if it can't be read. Never throws.</summary>
    DateTimeOffset? GetStartTimeUtc();

    /// <summary>Best-effort priming for prompt exit detection (mirrors Process.EnableRaisingEvents on
    /// the production adapter). Never throws - a process that's already exited or otherwise refuses
    /// this is left as-is, the same way the original inline implementation tolerated it.</summary>
    void PrepareForExitWait();

    /// <summary>Completes when the process exits, or when ct is cancelled. The caller is responsible
    /// for treating "already exited/inaccessible" as a normal exit rather than an error (see
    /// GameSessionWatcher's catch around this call).</summary>
    Task WaitForExitAsync(CancellationToken ct);
}
