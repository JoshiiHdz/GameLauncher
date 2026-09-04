using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

/// <summary>In-memory IGameProcess for scripting GameSessionWatcher scenarios. A process "exits" only
/// when the test calls SignalExited() (normally via FakeProcessProvider.Exit, which also removes it
/// from future FindProcessesByName results) - it never times out or completes on its own.</summary>
internal sealed class FakeGameProcess : IGameProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _path;
    private readonly DateTimeOffset? _startTimeUtc;

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

    public string? GetPath() => _path;
    public DateTimeOffset? GetStartTimeUtc() => _startTimeUtc;
    public void PrepareForExitWait() => ExitWaitPrepared = true;

    public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

    /// <summary>Not for direct use by scenarios - go through FakeProcessProvider.Exit so the process
    /// also stops appearing in FindProcessesByName, mirroring the OS dropping an exited process from
    /// the process table.</summary>
    internal void SignalExited() => _exited.TrySetResult();

    public void Dispose() => Disposed = true;
}
