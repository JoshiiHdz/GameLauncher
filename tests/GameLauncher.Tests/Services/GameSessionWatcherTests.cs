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
    public async Task Cancellation_DuringHandoffWait_ReturnsFalse()
    {
        // Once the watched process has genuinely exited and GameSessionWatcher is waiting to see
        // whether anything hands off from it, cancelling ct while blocked in WaitForHandoffAsync's own
        // Task.Delay makes it return "no replacement found" - indistinguishable, on its own, from a
        // genuine handoff timeout. The explicit ct.IsCancellationRequested check right after
        // WaitForHandoffAsync returns (see WaitForExitAsync) is what tells these two apart and reports
        // false here instead of incorrectly concluding "this launch is over". This exercises the
        // unconfirmed (full HandoffGracePeriod) path - see the sibling test below for the confirmed
        // (short LongSessionHandoffCheck) path.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process); // enters the handoff wait
        await Task.Delay(50);

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task Cancellation_DuringConfirmedShortHandoffWait_ReturnsFalse()
    {
        // Same contract as Cancellation_DuringHandoffWait_ReturnsFalse above, but for a session already
        // confirmed real (via LongSessionThreshold, which also implies ChainConfirmationThreshold - see
        // GameSessionWatcherOptions' remarks) and so waiting on the short LongSessionHandoffCheck window
        // instead of the full HandoffGracePeriod - cancellation must still be told apart from a genuine
        // handoff timeout regardless of which window's Task.Delay it lands in.
        var f = CreateFixture("game");
        var longRunningStart = f.TimeProvider.GetUtcNow() - GameSessionWatcherOptions.Default.LongSessionThreshold - TimeSpan.FromSeconds(1);
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), longRunningStart);
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process); // confirmed real session - enters the SHORT handoff wait
        await Task.Delay(50);

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    // No dedicated test for "WaitForHandoffAsync returns a genuine match on the exact tick
    // cancellation lands" (the case the disposal loop right after WaitForHandoffAsync in
    // WaitForExitAsync guards against): confirmed by hand, empirically, that it can't be forced
    // deterministically from outside. Task.Delay(TimeSpan, TimeProvider, CancellationToken) always
    // observes cancellation immediately rather than waiting for the next timer tick, so cancelling
    // while WaitForHandoffAsync is suspended in its own Task.Delay short-circuits straight to "no
    // replacement" (covered by Cancellation_DuringHandoffWait_ReturnsFalse above) - it never gets to
    // re-run FindRunning and observe a process added in the meantime. The only way found.Count > 0
    // and ct.IsCancellationRequested could both be true on the same check is a same-instant race
    // between this method's own synchronous fast path and another thread calling Cancel() - a
    // zero-width window, not something a black-box test can reliably land on. The disposal loop is
    // kept as cheap, correct defense for that case regardless.

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

    // ---- Chain-level confirmation across handoffs ------------------------------------------------
    //
    // Real FC26 log this whole section is derived from: watching began at 17:45:12.426, handed off to
    // EAAntiCheat.GameServiceLauncher at 17:45:20.853 (~8.4s), the anti-cheat process itself disappeared
    // ~17:46:10.092 (~49.2s later - a ~57.7s uninterrupted chain overall), and the window wasn't
    // restored until 17:46:22.093 - exactly HandoffGracePeriod (12s) after the anti-cheat process
    // exited. Neither individual stage ever crossed LongSessionThreshold (60s) on its own, so the old
    // per-batch-only "wasLongSession" check never fired and every handoff in the chain paid the full
    // 12s, even on a session that had already run far longer than any bootstrapper realistically would.

    [Fact]
    public async Task ChainConfirmation_LongUninterruptedChainAcrossHandoffs_UsesShortCheckOnceConfirmed()
    {
        var f = CreateFixture("main", "anticheat");
        var main = f.ProcessProvider.AddRunning(1, "main", Path.Combine(InstallDir, "main.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        // "main" is watched for 8.4s - individually well under LongSessionThreshold - before exiting
        // and handing off, same as the real log's initial FC26 process.
        f.TimeProvider.Advance(TimeSpan.FromSeconds(8.4));
        f.ProcessProvider.Exit(main);
        await Task.Delay(50);

        var antiCheat = f.ProcessProvider.AddRunning(2, "anticheat", Path.Combine(InstallDir, "anticheat.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 3, task);
        Assert.True(antiCheat.ExitWaitPrepared);
        Assert.False(task.IsCompleted);

        // "anticheat" then runs 49.2s - also individually under LongSessionThreshold (60s) - but the
        // whole uninterrupted chain (8.4s + a few handoff-poll ticks + 49.2s) has now crossed
        // ChainConfirmationThreshold (45s), even though no single batch ever did.
        f.TimeProvider.Advance(TimeSpan.FromSeconds(49.2));
        f.ProcessProvider.Exit(antiCheat);
        await Task.Delay(50);

        // Only the SHORT LongSessionHandoffCheck (2s) is needed to conclude the watch here - proves
        // chain-level confirmation kicked in even though neither individual process crossed 60s. Before
        // the fix, this handoff would have needed the full 12s HandoffGracePeriod instead.
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionHandoffCheck);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ChainConfirmation_UnconfirmedChain_HandoffAfterSevenSeconds_StillDetectedWithinFullWindow()
    {
        // Guards against the fix over-correcting: an early, still-unconfirmed handoff (chain barely
        // started) must keep its full HandoffGracePeriod protection - this is the real EA trial-launcher
        // timing (~7s) HandoffGracePeriod's own remarks are calibrated from.
        var f = CreateFixture("launcher", "realgame");
        var stub = f.ProcessProvider.AddRunning(1, "launcher", Path.Combine(InstallDir, "launcher.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(stub); // short-lived, chain also brand new - stays unconfirmed
        await Task.Delay(50);

        // Replacement doesn't appear until 7s into the handoff wait - comfortably inside the still-full
        // 12s window, but well past the 2s short-confirmed one.
        await StepAsync(f.TimeProvider, TimeSpan.FromSeconds(1), steps: 6, task);
        var real = f.ProcessProvider.AddRunning(2, "realgame", Path.Combine(InstallDir, "realgame.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, TimeSpan.FromSeconds(1), steps: 2, task);

        Assert.True(real.ExitWaitPrepared);
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task ChainConfirmation_StaysConfirmedAcrossASubsequentShortLivedHandoff()
    {
        // Once a chain is confirmed real, a later, individually short-lived stage in the same chain
        // must not fall back to the full HandoffGracePeriod - confirmation persists for the rest of the
        // watch, not just the handoff that first earned it.
        var f = CreateFixture("launcher", "anticheat");
        var launcher = f.ProcessProvider.AddRunning(1, "launcher", Path.Combine(InstallDir, "launcher.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        // First stage alone already crosses LongSessionThreshold - confirms the session outright.
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionThreshold + TimeSpan.FromSeconds(1));
        f.ProcessProvider.Exit(launcher);
        await Task.Delay(50);

        var antiCheat = f.ProcessProvider.AddRunning(2, "anticheat", Path.Combine(InstallDir, "anticheat.exe"), f.TimeProvider.GetUtcNow());
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 2, task);
        Assert.True(antiCheat.ExitWaitPrepared);
        Assert.False(task.IsCompleted);

        // This second stage is itself very short-lived and has nothing to hand off to. If confirmation
        // weren't preserved across the handoff, this exit could fall back to the full 12s
        // HandoffGracePeriod instead of the short 2s LongSessionHandoffCheck.
        f.ProcessProvider.Exit(antiCheat);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionHandoffCheck);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ChainConfirmation_UnconfirmedShortFailedLaunch_StillWaitsFullHandoffWindow()
    {
        // A genuinely short, failed launch (process exits almost immediately, nothing ever replaces it,
        // chain never gets anywhere near ChainConfirmationThreshold) must still receive the full
        // HandoffGracePeriod before being concluded, not the short window - proves the fix didn't
        // accidentally shrink protection for the ordinary/ubiquitous case it wasn't meant to touch.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        f.ProcessProvider.Exit(process); // near-zero uptime, chain also brand new - fully unconfirmed
        await Task.Delay(50);

        // Past what the SHORT window would need (2s) but short of the FULL window (12s) - must NOT be
        // enough to conclude, proving this case is not being mistaken for a confirmed session.
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionHandoffCheck + TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }
}
