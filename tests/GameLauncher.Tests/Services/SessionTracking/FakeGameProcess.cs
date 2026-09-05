using System.ComponentModel;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

/// <summary>In-memory IGameProcess for scripting GameSessionWatcher scenarios. A process "exits" only
/// when the test calls SignalExited() (normally via FakeProcessProvider.Exit, which also removes it
/// from future FindProcessesByName results) - it never times out or completes on its own.</summary>
internal sealed class FakeGameProcess : IGameProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _path;
    private DateTimeOffset? _startTimeUtc;
    private bool _identityGone;
    private bool _deniesWait;
    private bool _deniesStartTimeQuery;

    public FakeGameProcess(int id, string processName, string? path, DateTimeOffset? startTimeUtc = null)
    {
        Id = id;
        ProcessName = processName;
        _path = path;
        _startTimeUtc = startTimeUtc;
    }

    public int Id { get; }
    public string ProcessName { get; }
    public bool Disposed { get; private set; }
    public bool ExitWaitPrepared { get; private set; }
    public int WaitAttempts { get; private set; }

    public string? GetPath() => _path;

    /// <summary>Returns the captured start time as long as the OS would still recognize this exact
    /// process identity - null once SignalExited() has run (mirroring OpenProcess failing outright for a
    /// truly-gone process), or once DenyStartTimeQuery() has been called (mirroring a process protected
    /// heavily enough to deny even this minimal query while still genuinely running - the case
    /// GameSessionWatcher.CheckPresence exists to not mistake for "exited").</summary>
    public DateTimeOffset? GetStartTimeUtc() => _identityGone || _deniesStartTimeQuery ? null : _startTimeUtc;

    public void PrepareForExitWait() => ExitWaitPrepared = true;

    /// <summary>By default awaits SignalExited() like a normal waitable process. After
    /// DenyWaitForExit(), simulates an access-protected process instead: every call throws a
    /// Win32Exception (a SystemException) without the process actually having exited, the real-world
    /// behavior behind the Marvel Rivals/Fortnite same-PID busy loop this fixture exists to test.</summary>
    public Task WaitForExitAsync(CancellationToken ct)
    {
        if (_deniesWait)
        {
            WaitAttempts++;
            return Task.FromException(new Win32Exception(5, "Access is denied"));
        }

        return _exited.Task.WaitAsync(ct);
    }

    /// <summary>Makes WaitForExitAsync throw instead of waiting, while the process otherwise stays
    /// "running" (still identity-alive, still discoverable) until SignalExited() is called.</summary>
    internal void DenyWaitForExit() => _deniesWait = true;

    /// <summary>Makes GetStartTimeUtc() return null from now on, without the process actually exiting -
    /// simulates a process too heavily protected to answer even the minimal PROCESS_QUERY_LIMITED_INFORMATION
    /// query CheckPresence's identity check relies on.</summary>
    internal void DenyStartTimeQuery() => _deniesStartTimeQuery = true;

    /// <summary>Simulates Windows recycling this exact PID for a genuinely different process: the
    /// identity CheckPresence sees from now on no longer matches whatever start time was captured
    /// earlier, without the process ever having been removed from FakeProcessProvider (it's still
    /// "discoverable" - just as someone else now).</summary>
    internal void SimulatePidReusedByDifferentProcess(DateTimeOffset newStartTimeUtc) => _startTimeUtc = newStartTimeUtc;

    /// <summary>Not for direct use by scenarios - go through FakeProcessProvider.Exit so the process
    /// also stops appearing in FindProcessesByName, mirroring the OS dropping an exited process from
    /// the process table.</summary>
    internal void SignalExited()
    {
        _identityGone = true;
        _exited.TrySetResult();
    }

    public void Dispose() => Disposed = true;
}
