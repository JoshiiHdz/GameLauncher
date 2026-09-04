using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.SessionTracking;
using GameLauncher.Tests.Services.SessionTracking;
using Microsoft.Extensions.Time.Testing;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises GameSessionWatcher's timing-dependent logic entirely through fakes (FakeTimeProvider,
/// FakeProcessProvider, FakeExecutableNameDiscovery) - no real processes, no real waiting.
///
/// Two Advance() patterns are used, verified against FakeTimeProvider's actual semantics before this
/// suite was written:
///  - One big Advance() covering an entire timeout/deadline correctly jumps a loop straight to "past
///    deadline" in a single step (used for DiscoveryTimeout and deadline-based grace-period checks).
///  - Several small Advance() calls, each followed by a short real yield, are needed to drive a loop
///    through discrete polls where a test needs to change state between them (a process appearing,
///    cancellation) - a single big Advance() only fires whatever is due *at that instant*, it does not
///    cascade through timers a loop only registers *after* resuming.
/// </summary>
public class GameSessionWatcherTests
{
    private const string InstallDir = @"C:\Games\TestGame";

    private static GameEntry MakeGame() => new()
    {
        Id = "test-1",
        Name = "Test Game",
        ExecutablePath = Path.Combine(InstallDir, "game.exe"),
        InstallDir = InstallDir,
        Source = GameSource.Manual,
    };

    private sealed record Fixture(GameSessionWatcher Watcher, FakeTimeProvider TimeProvider, FakeExecutableNameDiscovery NameDiscovery, FakeProcessProvider ProcessProvider);

    private static Fixture CreateFixture(params string[] candidateNames)
    {
        var timeProvider = new FakeTimeProvider();
        var nameDiscovery = new FakeExecutableNameDiscovery(candidateNames);
        var processProvider = new FakeProcessProvider();
        var watcher = new GameSessionWatcher(timeProvider, nameDiscovery, processProvider, GameSessionWatcherOptions.Default);
        return new Fixture(watcher, timeProvider, nameDiscovery, processProvider);
    }

    /// <summary>Drives fake time forward one poll interval at a time, each followed by a short real
    /// yield so the awaiting loop's continuation actually runs (and can observe state a test changes
    /// between steps) before the next Advance().</summary>
    private static async Task StepAsync(FakeTimeProvider timeProvider, TimeSpan step, int steps, Task task)
    {
        for (var i = 0; i < steps && !task.IsCompleted; i++)
        {
            timeProvider.Advance(step);
            await Task.Delay(20);
        }
    }

    // ---- Discovery timeout -------------------------------------------------------------------

    [Fact]
    public async Task DiscoveryTimeout_NoProcessEverAppears_ReturnsTrue()
    {
        var f = CreateFixture("game");

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.DiscoveryTimeout);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Cancellation in every phase ----------------------------------------------------------

    [Fact]
    public async Task Cancellation_DuringDiscoveryPolling_ReturnsFalse()
    {
        var f = CreateFixture("game");
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50); // reaches the discovery loop's Task.Delay with nothing found yet

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task Cancellation_WhileProcessStillGenuinelyRunning_ReturnsFalse()
    {
        // Cancelling while `await process.WaitForExitAsync(ct)` is pending throws
        // OperationCanceledException - a SystemException subtype - which the surrounding `catch
        // (Exception ex) when (ex is InvalidOperationException or SystemException)` swallows as
        // though the process exited normally. That alone would misreport this as "exited" - but the
        // process is deliberately never made to exit in this test, so WaitForHandoffAsync's own
        // FindRunning call immediately re-discovers the still-alive process, so it does NOT conclude
        // "no replacement found". Control falls through to the outer `while (!ct.IsCancellationRequested)`
        // loop, which correctly stops and returns false. See the handoff-wait test below for the
        // narrower case where this protection doesn't apply.
        var f = CreateFixture("game");
        f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50); // discovery finds the process synchronously and moves into WaitForExitAsync on it

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task Cancellation_DuringHandoffWait_DoesNotReturnFalse()
    {
        // KNOWN GAP, pinned down rather than fixed here (this refactor is behavior-preserving): once
        // the watched process has genuinely exited and GameSessionWatcher is waiting to see whether
        // anything hands off from it, cancelling ct while blocked in WaitForHandoffAsync's own
        // Task.Delay makes it return "no replacement found" - indistinguishable, to the caller, from a
        // genuine handoff timeout. Both conclude true, even though this launch was actually
        // superseded/shutting down, not finished. MainWindow.xaml.cs works around this today by
        // clearing the game's "Running" badge at the point it cancels, rather than trusting this
        // return value.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process); // enters the handoff wait
        await Task.Delay(50);

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // documents the current (surprising) behavior, not the desired one
    }

    // ---- Short-stub handoff ---------------------------------------------------------------------

    [Fact]
    public async Task ShortStubHandoff_ReplacementFoundWithinGracePeriod_ContinuesWatchingReplacement()
    {
        var f = CreateFixture("launcher", "realgame");
        var stub = f.ProcessProvider.AddRunning(1, "launcher", Path.Combine(InstallDir, "launcher.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(stub); // short-lived (uptime ~0) - gets the full HandoffGracePeriod (12s)
        await Task.Delay(50);

        var real = f.ProcessProvider.AddRunning(2, "realgame", Path.Combine(InstallDir, "realgame.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 3, task);

        Assert.False(task.IsCompleted); // still watching the handed-off-to process, not done yet
        Assert.True(real.ExitWaitPrepared); // confirms it was actually picked up as the watched process

        f.ProcessProvider.Exit(real);
        await Task.Delay(50); // let the exit's continuation reach WaitForHandoffAsync's Task.Delay before advancing past it
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod); // no further replacement

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Long-session exit ---------------------------------------------------------------------

    [Fact]
    public async Task LongSessionExit_NoReplacementNeeded_ReturnsTruePromptly()
    {
        var f = CreateFixture("game");
        var longRunningStart = f.TimeProvider.GetUtcNow() - GameSessionWatcherOptions.Default.LongSessionThreshold - TimeSpan.FromSeconds(1);
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), longRunningStart);

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process);
        await Task.Delay(50); // let the exit's continuation reach WaitForHandoffAsync's Task.Delay before advancing past it

        // Advancing by only the SHORT LongSessionHandoffCheck window (2s), not the full 12s
        // HandoffGracePeriod, is enough to conclude the watch - proves the long-session fast path was
        // taken rather than the full stub-handoff wait.
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionHandoffCheck);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Multiple processes ---------------------------------------------------------------------

    [Fact]
    public async Task MultipleProcesses_WaitsForAllBeforeConcludingExit()
    {
        var f = CreateFixture("game", "helper");
        var main = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        var helper = f.ProcessProvider.AddRunning(2, "helper", Path.Combine(InstallDir, "helper.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(main);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // helper is still running - not done yet

        f.ProcessProvider.Exit(helper);
        await Task.Delay(50); // let the exit's continuation reach WaitForHandoffAsync's Task.Delay before advancing past it
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
        Assert.True(main.Disposed);
        Assert.True(helper.Disposed);
    }

    // ---- Path-access denial fallback ------------------------------------------------------------

    [Fact]
    public async Task PathAccessDenied_NameMatchAloneIsTrusted()
    {
        // Simulates an anti-cheat-protected process that refuses even the minimal path read -
        // GetPath() returning null must not exclude it, or the watcher never finds a genuinely
        // running game (the real Fortnite/EasyAntiCheat bug this fallback exists for).
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", path: null, f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process);
        await Task.Delay(50); // let the exit's continuation reach WaitForHandoffAsync's Task.Delay before advancing past it
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // reached the "found it, watched it, it exited" path - not the discovery timeout
    }

    // ---- Path mismatch rejection ----------------------------------------------------------------

    [Fact]
    public async Task PathMismatch_NameMatchUnderDifferentDirectory_IsRejected()
    {
        var f = CreateFixture("game");
        // Name matches, but it's a different, unrelated program that happens to share the exe name.
        f.ProcessProvider.AddRunning(1, "game", @"C:\Other\game.exe", f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.PollInterval, steps: 3, task);

        Assert.False(task.IsCompleted); // never "found" - correctly rejected by the install-dir check
    }

    // ---- Dynamically appearing executables ------------------------------------------------------

    [Fact]
    public async Task DynamicallyAppearingExecutable_PickedUpMidHandoffWait()
    {
        // Only "launcher" is known at first (what the initial folder scan found); "realgame" is
        // discovered only once WaitForHandoffAsync re-scans mid-wait, simulating an exe that gets
        // written/extracted to disk partway through the handoff.
        var f = CreateFixture("launcher");
        var stub = f.ProcessProvider.AddRunning(1, "launcher", Path.Combine(InstallDir, "launcher.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(stub);
        await Task.Delay(50);

        // "realgame" isn't a candidate name yet - added to the provider, but invisible until
        // GetCandidateNames is updated below.
        var real = f.ProcessProvider.AddRunning(2, "realgame", Path.Combine(InstallDir, "realgame.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 2, task);
        Assert.False(real.ExitWaitPrepared); // not picked up yet - correctly invisible under the old name set

        f.NameDiscovery.SetNames("launcher", "realgame"); // the "rescan" finding the new exe
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 3, task);

        Assert.True(real.ExitWaitPrepared); // now picked up once the candidate list includes it
        Assert.False(task.IsCompleted); // still watching it, not concluded yet
    }

    // ---- Chained handoffs -----------------------------------------------------------------------

    [Fact]
    public async Task ChainedHandoffs_MultipleConsecutiveReplacementsAreAllWatched()
    {
        var f = CreateFixture("launcher", "anticheat", "realgame");
        var launcher = f.ProcessProvider.AddRunning(1, "launcher", Path.Combine(InstallDir, "launcher.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        // First handoff: launcher -> anti-cheat init stage.
        f.ProcessProvider.Exit(launcher);
        await Task.Delay(50);
        var antiCheat = f.ProcessProvider.AddRunning(2, "anticheat", Path.Combine(InstallDir, "anticheat.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 3, task);
        Assert.True(antiCheat.ExitWaitPrepared);
        Assert.False(task.IsCompleted);

        // Second handoff: anti-cheat init -> the real game. Proves the outer while loop truly runs
        // more than once, not just for a single handoff.
        f.ProcessProvider.Exit(antiCheat);
        await Task.Delay(50);
        var real = f.ProcessProvider.AddRunning(3, "realgame", Path.Combine(InstallDir, "realgame.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 3, task);
        Assert.True(real.ExitWaitPrepared);
        Assert.False(task.IsCompleted);

        // Finally, the real game exits with nothing left to hand off to.
        f.ProcessProvider.Exit(real);
        await Task.Delay(50); // let the exit's continuation reach WaitForHandoffAsync's Task.Delay before advancing past it
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }
}
