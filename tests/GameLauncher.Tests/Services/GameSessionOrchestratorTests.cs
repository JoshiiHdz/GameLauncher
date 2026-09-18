using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.SessionTracking;
using GameLauncher.Tests.Services.SessionTracking;
using Microsoft.Extensions.Time.Testing;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Proves GameSessionOrchestrator's one job: the logging-only window observer it starts alongside
/// GameSessionWatcher's real, authoritative watch can NEVER affect that watch's own result, timing, or
/// cancellation - no matter whether the observer completes early, throws, never completes, or the whole
/// session gets superseded. Every test here drives a REAL GameSessionWatcher (via the same
/// FakeTimeProvider/FakeProcessProvider/FakeExecutableNameDiscovery fixture GameSessionWatcherTests
/// already uses) so the asserted outcome is GameSessionWatcher's genuine contract, not a stand-in for it -
/// with a scripted `observe` function substituted for GameWindowObserver.ObserveAsync so each scenario
/// (early completion, throwing, never completing) can be driven deterministically without depending on
/// real Win32 window enumeration.
/// </summary>
public class GameSessionOrchestratorTests
{
    private const string InstallDir = @"C:\Games\TestGame";

    private static GameEntry MakeGame(string id = "test-1") => new()
    {
        Id = id,
        Name = "Test Game",
        ExecutablePath = Path.Combine(InstallDir, "game.exe"),
        InstallDir = InstallDir,
        Source = GameSource.Manual,
    };

    private sealed record Fixture(GameSessionWatcher Watcher, FakeTimeProvider TimeProvider, FakeProcessProvider ProcessProvider);

    private static Fixture CreateFixture(params string[] candidateNames)
    {
        var timeProvider = new FakeTimeProvider();
        var nameDiscovery = new FakeExecutableNameDiscovery(candidateNames);
        var processProvider = new FakeProcessProvider();
        var watcher = new GameSessionWatcher(timeProvider, nameDiscovery, processProvider, GameSessionWatcherOptions.Default);
        return new Fixture(watcher, timeProvider, processProvider);
    }

    // ---- Diagnostics off: the observer must never even be started -----------------------------------

    [Fact]
    public async Task DiagnosticsDisabled_ObserverNeverInvoked_ResultMatchesPlainWatcher()
    {
        var f = CreateFixture("game");
        var observeCalls = 0;
        Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> ids, CancellationToken ct)
        {
            observeCalls++;
            return Task.CompletedTask;
        }

        var orchestrator = new GameSessionOrchestrator(f.Watcher, diagnosticsEnabled: () => false, Observe);
        var task = orchestrator.WaitForExitAsync(sessionId: 1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result); // matches GameSessionWatcher's own "never saw a process -> true" contract
        Assert.Equal(0, observeCalls);
    }

    // ---- The four scenarios Codex asked to be covered explicitly -------------------------------------

    [Fact]
    public async Task ObserverCompletesEarly_DoesNotAffectSessionOutcome()
    {
        var f = CreateFixture("game");
        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true,
            (_, _, _, _) => Task.CompletedTask); // completes immediately, well before the real watch does

        var process = f.ProcessProvider.AddRunning(100, "game", Path.Combine(InstallDir, "game.exe"));
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // the real watch's own outcome, entirely unaffected by the observer finishing early
    }

    /// <summary>Proves the caller-visible half of "observer failure must only disable diagnostics": a
    /// throwing observer changes nothing about the real watch's own result or timing. Does NOT, on its
    /// own, prove the exception was actually logged rather than silently discarded - RunObserverSafely's
    /// try/catch around `_observe` is what's responsible for that (a fire-and-forget task's unhandled
    /// exception does not crash the process or fail this test either way on modern .NET, so a mutation
    /// removing that catch is not distinguishable from this assertion alone). Verified directly instead:
    /// deleting RunObserverSafely's catch was tried by hand while writing this test and, as expected,
    /// left every assertion here still green - logging Logger has no test seam to assert against without
    /// reading its real, shared per-process log file, which risks cross-test flakiness disproportionate
    /// to what it would prove here.</summary>
    [Fact]
    public async Task ObserverThrows_IsSwallowed_SessionOutcomeUnaffected()
    {
        var f = CreateFixture("game");
        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true,
            (_, _, _, _) => Task.FromException(new InvalidOperationException("simulated observer failure")));

        var process = f.ProcessProvider.AddRunning(100, "game", Path.Combine(InstallDir, "game.exe"));
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        // Must not throw here - a faulted observer task is caught and logged (RunObserverSafely), never
        // rethrown to this caller.
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ObserverNeverCompletes_DoesNotBlockSessionOutcome_AndIsCancelledOnCleanup()
    {
        var f = CreateFixture("game");
        CancellationToken? observerToken = null;
        Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> ids, CancellationToken ct)
        {
            observerToken = ct;
            return Task.Delay(Timeout.Infinite, ct); // never completes on its own
        }

        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true, Observe);
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);

        // Must not hang waiting on the observer - it is never awaited on this return path.
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result);
        Assert.NotNull(observerToken);
        Assert.True(observerToken!.Value.IsCancellationRequested); // cleaned up once the real watch concluded
    }

    [Fact]
    public async Task Superseded_CancellingCallerToken_ReturnsFalse_AndCancelsObserver()
    {
        var f = CreateFixture("game");
        using var cts = new CancellationTokenSource();
        CancellationToken? observerToken = null;
        Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> ids, CancellationToken ct)
        {
            observerToken = ct;
            return Task.Delay(Timeout.Infinite, ct);
        }

        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true, Observe);
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50); // reaches discovery polling with nothing found yet

        cts.Cancel(); // a newer launch superseding this one, or the app closing
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result); // matches GameSessionWatcher's own cancellation contract
        Assert.NotNull(observerToken);
        Assert.True(observerToken!.Value.IsCancellationRequested); // the observer was cancelled too, promptly
    }

    [Fact]
    public async Task ObserverSlowToCleanUpAfterCancellation_DoesNotDelayCallerReturn()
    {
        // A cancellation-honoring observer that still takes a noticeable moment to actually finish (real
        // I/O, a real Win32 call in flight) must not add that time to the caller's own return - cleanup
        // is signalled, never awaited. Uses a REAL delay (not the fake clock) specifically to measure real
        // wall-clock latency on the return path, which is exactly what "must not delay restoration" means.
        var f = CreateFixture("game");
        async Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> ids, CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(2000); // deliberately slow, real-time cleanup AFTER noticing cancellation
            }
        }

        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true, Observe);
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Assert.True(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Caller's own return took {stopwatch.Elapsed.TotalMilliseconds:0}ms - should return promptly " +
            "without waiting on the observer's own (much slower) cleanup.");
    }

    // ---- Distinct sessions must never cross-contaminate the observer's own identity -------------------

    [Fact]
    public async Task DistinctSessions_ObserverReceivesDistinctSessionAndGameIdentity()
    {
        var f1 = CreateFixture("game");
        var f2 = CreateFixture("game");
        var seen = new List<(int SessionId, string GameId)>();
        Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> ids, CancellationToken ct)
        {
            lock (seen) seen.Add((sid, g.Id));
            return Task.CompletedTask;
        }

        var orchestrator1 = new GameSessionOrchestrator(f1.Watcher, () => true, Observe);
        var orchestrator2 = new GameSessionOrchestrator(f2.Watcher, () => true, Observe);

        var task1 = orchestrator1.WaitForExitAsync(1, MakeGame("game-a"), launched: (IGameProcess?)null, CancellationToken.None);
        var task2 = orchestrator2.WaitForExitAsync(2, MakeGame("game-b"), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f1.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);
        f2.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);
        await Task.WhenAll(task1.WaitAsync(TimeSpan.FromSeconds(5)), task2.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains((1, "game-a"), seen);
        Assert.Contains((2, "game-b"), seen);
    }

    // ---- The process-batch snapshot the observer reads must reflect GameSessionWatcher's own batches --

    [Fact]
    public async Task ProcessBatchSnapshot_ReflectsWatchersCurrentBatch_NeverALiveOrMutableCollection()
    {
        var f = CreateFixture("game");
        IReadOnlySet<int>? firstSnapshot = null;
        var snapshotsSeen = new List<IReadOnlySet<int>>();

        async Task Observe(int sid, GameEntry g, Func<IReadOnlySet<int>> getTrackedProcessIds, CancellationToken ct)
        {
            // Polls the same way GameWindowTracker itself would, purely to observe what gets published.
            while (!ct.IsCancellationRequested)
            {
                var current = getTrackedProcessIds();
                if (current.Count > 0)
                {
                    firstSnapshot ??= current;
                    lock (snapshotsSeen) snapshotsSeen.Add(current);
                }

                try { await Task.Delay(10, ct); } catch (OperationCanceledException) { return; }
            }
        }

        var orchestrator = new GameSessionOrchestrator(f.Watcher, () => true, Observe);
        var process = f.ProcessProvider.AddRunning(100, "game", Path.Combine(InstallDir, "game.exe"));
        var task = orchestrator.WaitForExitAsync(1, MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);

        // Give the observer's own polling loop a real chance to read a published snapshot.
        await Task.Delay(100);

        Assert.NotNull(firstSnapshot);
        Assert.Contains(100, firstSnapshot!);

        f.ProcessProvider.Exit(process);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);

        // Every snapshot read is its own, never-mutated-in-place instance - later publishes replace the
        // reference rather than changing a collection this test (or GameWindowTracker) already holds.
        Assert.All(snapshotsSeen, s => Assert.Equal(new HashSet<int> { 100 }, s));
    }
}
