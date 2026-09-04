using System.Diagnostics;
using System.IO;
using GameLauncher.Models;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Services;

/// <summary>
/// Works out when a launched game has actually exited.
///
/// The process we start is often not the game: Steam titles go through steam:// so we never get a
/// handle at all, and plenty of games run a bootstrapper that exits the instant it spawns the real
/// executable. So instead of trusting the process we started, this looks for processes running from
/// inside the game's install folder, then waits for all of them to exit.
///
/// Discovery polls briefly after launch (games take a while to come up); once the processes are
/// found it switches to waiting on handles, so there's no ongoing polling while you play.
///
/// All OS/filesystem/timing access goes through the injected seams (IProcessProvider,
/// IExecutableNameDiscovery, TimeProvider) rather than calling System.Diagnostics.Process, the
/// filesystem, or DateTime.UtcNow directly - see GameSessionWatcherOptions for the tunable timings and
/// the Win32* classes for the production (real-OS) implementations. This is what lets
/// GameLauncher.Tests exercise every timing-dependent scenario (discovery timeout, handoff, long-session
/// detection, ...) without actually waiting real minutes or depending on real game processes.
/// </summary>
public sealed class GameSessionWatcher
{
    private readonly TimeProvider _timeProvider;
    private readonly IExecutableNameDiscovery _nameDiscovery;
    private readonly IProcessProvider _processProvider;
    private readonly GameSessionWatcherOptions _options;

    public GameSessionWatcher()
        : this(TimeProvider.System, new FileSystemExecutableNameDiscovery(), new Win32ProcessProvider(), GameSessionWatcherOptions.Default)
    {
    }

    /// <summary>Test-only seam - GameLauncher.Tests substitutes a FakeTimeProvider, a scripted
    /// IExecutableNameDiscovery/IProcessProvider, and millisecond-scale options here. Production
    /// always goes through the parameterless constructor above.</summary>
    internal GameSessionWatcher(
        TimeProvider timeProvider,
        IExecutableNameDiscovery nameDiscovery,
        IProcessProvider processProvider,
        GameSessionWatcherOptions options)
    {
        _timeProvider = timeProvider;
        _nameDiscovery = nameDiscovery;
        _processProvider = processProvider;
        _options = options;
    }

    /// <summary>
    /// Returns once the game appears to have exited, or once it's given up waiting for the game to
    /// even start. Returns false only when ct is cancelled (a newer launch superseded this one, or
    /// the app is closing) - the caller should treat false as "leave the window alone," since
    /// something else now owns the state. Every other outcome, including a discovery timeout, returns
    /// true - both mean "this launch is over, safe to restore the window."
    /// </summary>
    public Task<bool> WaitForExitAsync(GameEntry game, Process? launched, CancellationToken ct = default) =>
        WaitForExitAsync(game, launched is null ? null : _processProvider.Wrap(launched), ct);

    /// <summary>Same contract as the Process-taking overload above, but against the IGameProcess seam
    /// directly - this is the one GameLauncher.Tests calls, with a fake `launched` (or none) instead of
    /// a real OS process.</summary>
    internal async Task<bool> WaitForExitAsync(GameEntry game, IGameProcess? launched, CancellationToken ct)
    {
        var candidateNames = _nameDiscovery.GetCandidateNames(game.InstallDir);
        Logger.Info($"'{game.Name}': watching for {candidateNames.Count} candidate exe name(s) "
            + $"under '{game.InstallDir}': {string.Join(", ", candidateNames)}");

        if (candidateNames.Count == 0)
        {
            Logger.Warn($"No executables found under '{game.InstallDir}' - can't watch '{game.Name}' for exit.");
            return false;
        }

        var discoveryDeadline = _timeProvider.GetUtcNow() + _options.DiscoveryTimeout;
        List<IGameProcess> running = [];

        while (_timeProvider.GetUtcNow() < discoveryDeadline && !ct.IsCancellationRequested)
        {
            running = FindRunning(game, candidateNames, launched);
            if (running.Count > 0)
                break;

            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (running.Count == 0)
        {
            if (ct.IsCancellationRequested)
            {
                Logger.Info($"Stopped watching for '{game.Name}' to start (superseded or shutting down).");
                return false;
            }

            Logger.Warn($"Never saw a running process for '{game.Name}' within "
                + $"{_options.DiscoveryTimeout.TotalMinutes:0} minutes - assuming the launch failed and restoring the window.");
            return true;
        }

        Logger.Info($"Watching {running.Count} process(es) for '{game.Name}': "
            + string.Join(", ", running.Select(p => $"{p.ProcessName} (pid {p.Id}) <- {p.GetPath() ?? "path unknown"}")));

        // Captured up front, right while these processes are still alive - once a process exits it
        // can't be asked when it started, so this has to be read now and carried forward to whenever
        // that process actually exits below.
        var startTimes = running.ToDictionary(p => p.Id, p => p.GetStartTimeUtc());

        while (!ct.IsCancellationRequested)
        {
            foreach (var process in running)
            {
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                {
                    // Process already gone or inaccessible - treat as exited.
                }
                finally
                {
                    process.Dispose();
                }
            }

            // If any of the processes that just exited had clearly been running for a while, this
            // was a real "I'm done playing" exit, not a bootstrapper handing off - skip the long
            // wait. Unknown uptimes (null) don't count either way, so a batch this can't measure at
            // all safely falls back to the full wait instead of guessing. Reads startTimes.Values
            // directly rather than touching the `running` process objects again - they were just
            // disposed above.
            var wasLongSession = startTimes.Values.Any(started =>
                started is { } s && _timeProvider.GetUtcNow() - s >= _options.LongSessionThreshold);
            var gracePeriod = wasLongSession ? _options.LongSessionHandoffCheck : _options.HandoffGracePeriod;

            // A launcher process commonly hands off to the real game and exits well before it's up,
            // so don't trust a single immediate recheck - keep looking for a replacement for a while.
            var replacement = await WaitForHandoffAsync(game, candidateNames, gracePeriod, ct);
            if (replacement.Count == 0)
            {
                Logger.Info($"'{game.Name}' exited{(wasLongSession ? " (was a real play session)" : "")} - "
                    + $"no replacement process found under '{game.InstallDir}' within the "
                    + $"{gracePeriod.TotalSeconds:0}s handoff window.");
                return true;
            }

            Logger.Info($"'{game.Name}' handed off to {replacement.Count} new process(es), still watching: "
                + string.Join(", ", replacement.Select(p => $"{p.ProcessName} (pid {p.Id}) <- {p.GetPath() ?? "path unknown"}")));
            running = replacement;
            startTimes = running.ToDictionary(p => p.Id, p => p.GetStartTimeUtc());
        }

        return false;
    }

    private async Task<List<IGameProcess>> WaitForHandoffAsync(
        GameEntry game, HashSet<string> candidateNames, TimeSpan gracePeriod, CancellationToken ct)
    {
        var deadline = _timeProvider.GetUtcNow() + gracePeriod;

        while (true)
        {
            // Re-scan for candidate names too, not just running processes, on every tick: some games
            // only write/extract their real binary partway through the handoff, so a name absent at
            // the start of this wait can still appear at any point before the deadline. A first
            // attempt refreshed this only once at entry and missed a handoff file created a few
            // seconds later - confirmed live with a stub that spawns its "real" process after a delay.
            if (_nameDiscovery.GetCandidateNames(game.InstallDir) is { Count: > 0 } refreshed)
                candidateNames = refreshed;

            var found = FindRunning(game, candidateNames, launched: null);
            if (found.Count > 0 || _timeProvider.GetUtcNow() >= deadline || ct.IsCancellationRequested)
                return found;

            try
            {
                await Task.Delay(_options.HandoffPollInterval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }
    }

    private List<IGameProcess> FindRunning(GameEntry game, HashSet<string> candidateNames, IGameProcess? launched)
    {
        var found = new List<IGameProcess>();

        // Unlike the name-matched loop below, `launched` isn't scoped to this game's own exe names -
        // it's whatever process Process.Start happened to return (for Steam, explicitly documented as
        // "the URI handler rather than the game," i.e. often unrelated) - so this one keeps the
        // strict path check rather than trusting a blocked path read.
        if (launched is not null && IsUnder(launched.GetPath(), game.InstallDir))
            found.Add(launched);

        foreach (var process in _processProvider.FindProcessesByName(candidateNames))
        {
            if (found.Any(p => p.Id == process.Id) || !IsRunningThisGame(process, game.InstallDir))
            {
                process.Dispose();
                continue;
            }

            process.PrepareForExitWait();
            found.Add(process);
        }

        return found;
    }

    private static bool IsUnder(string? path, string directory)
        => path is not null
           && path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Real log from a Fortnite session: a path read was refused for every candidate process
    /// (EasyAntiCheat-protected processes commonly block it), so IsUnder(null, ...) failed every
    /// single one and the watcher never found the game at all - "Never saw a running process", even
    /// though it was running the whole time. When the path genuinely can't be read, this trusts the
    /// name match alone instead of silently dropping the process: candidateNames only ever contains
    /// exe names actually found inside this specific game's own install folder, so an unrelated
    /// process happening to share one of those exact names is effectively impossible. When the path
    /// *can* be read, the strict install-dir check still applies unchanged.
    /// </summary>
    private static bool IsRunningThisGame(IGameProcess process, string installDir)
    {
        var path = process.GetPath();
        return path is null || IsUnder(path, installDir);
    }
}
