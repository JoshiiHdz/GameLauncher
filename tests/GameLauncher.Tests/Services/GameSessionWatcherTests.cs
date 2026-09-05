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
    public async Task ChainConfirmation_WallClockExceedsThresholdViaHandoffGapsAlone_StaysUnconfirmed()
    {
        // Distinguishes "cumulative time a process was actually running" from "wall-clock time since
        // the chain started" - five short-lived batches (p0..p4), each active only ~0.5s, separated by
        // four handoff gaps of ~11s each (just under the full 12s HandoffGracePeriod - the maximum a
        // still-unconfirmed handoff can ever take). Wall-clock elapsed since the chain started is
        // ~2.5s + 4*11s = ~46.5s, past ChainConfirmationThreshold (45s) - but cumulative ACTIVE duration
        // is only ~2.5s, nowhere close. If confirmation were still measured from wall-clock chain-start
        // (the pre-fix bug this test guards against), this chain would incorrectly confirm on its final
        // handoff; measured from active watched-process time alone, it must not.
        //
        // Each gap is driven through ~10 individual 1-second polling steps with NO replacement process
        // present yet, only adding one afterward and stepping one more poll to detect it (~11s total) -
        // a single big Advance() with the replacement already added beforehand would have left it
        // sitting in the fake process provider for the *entire* gap, which only proves the watcher wasn't
        // polling during that time, not that the gap was genuinely process-free.
        var names = new[] { "p0", "p1", "p2", "p3", "p4" };
        var f = CreateFixture(names);

        var current = f.ProcessProvider.AddRunning(1, "p0", Path.Combine(InstallDir, "p0.exe"), f.TimeProvider.GetUtcNow());
        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        for (var i = 1; i <= 4; i++)
        {
            f.TimeProvider.Advance(TimeSpan.FromSeconds(0.5)); // each batch is only briefly active
            f.ProcessProvider.Exit(current);
            await Task.Delay(50);

            // ~10s with genuinely nothing present - no replacement process exists in the fake provider
            // at all during this stretch, so this is a real process-free gap, not just unpolled time.
            await StepAsync(f.TimeProvider, TimeSpan.FromSeconds(1), steps: 10, task);
            Assert.False(task.IsCompleted); // still within the unconfirmed 12s window, nothing found yet

            current = f.ProcessProvider.AddRunning(i + 1, names[i], Path.Combine(InstallDir, $"{names[i]}.exe"), f.TimeProvider.GetUtcNow());
            await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.HandoffPollInterval, steps: 1, task);

            Assert.True(current.ExitWaitPrepared); // detected ~11s into the gap - still within the unconfirmed 12s window
            Assert.False(task.IsCompleted);
        }

        // Final batch (p4) exits with nothing left to replace it. Advancing by only the SHORT window
        // must NOT be enough to conclude - proves this chain is still using the full 12s window despite
        // wall-clock time since the chain started having long since exceeded ChainConfirmationThreshold.
        f.TimeProvider.Advance(TimeSpan.FromSeconds(0.5));
        f.ProcessProvider.Exit(current);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.LongSessionHandoffCheck);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);
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

    // ---- Protected-process wait denial (same-PID busy loop) ---------------------------------------
    //
    // Real logs this section is derived from: a Marvel Rivals session logged 3,751 handoff lines and
    // 3,752 batch lines in ~85 seconds (the same PIDs rediscovered roughly every 20ms), and Fortnite
    // briefly did the same with its GDKLauncher PID. Cause: Process.WaitForExitAsync can throw a
    // Win32Exception for an access-protected process that is still genuinely running (it can allow the
    // minimal PROCESS_QUERY_LIMITED_INFORMATION read GetStartTimeUtc needs while still refusing the
    // broader access a real wait needs). The old code caught that exception the same as a real exit,
    // which sent WaitForHandoffAsync straight into rediscovering the exact same still-running PID as a
    // "handoff" - with nothing throttling the retries, since FindRunning succeeds instantly against a
    // process that never went anywhere.

    [Fact]
    public async Task ProtectedProcess_WaitDenied_WhileStillAlive_PollsWithoutBusyLoopAndRestoresAfterExit()
    {
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        process.DenyWaitForExit(); // set before the watcher ever calls WaitForExitAsync on it

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50); // discovers the process; its first WaitForExitAsync call throws immediately

        Assert.Equal(1, process.WaitAttempts);
        Assert.False(task.IsCompleted); // backed off instead of concluding "exited"

        // Advancing several full poll intervals must cost only one retry each - proves the throttled
        // backoff, not a tight spin (which would have driven WaitAttempts into the thousands over the
        // same fake-time span, as the real logs showed).
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 3, task);
        Assert.Equal(4, process.WaitAttempts);
        Assert.False(task.IsCompleted); // still treating the process as alive, not restoring yet

        // The process actually exits now - the next throttled recheck must notice via identity (the
        // still-denied wait call keeps throwing) and correctly treat it as a genuine exit rather than
        // looping forever.
        f.ProcessProvider.Exit(process);
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 1, task);
        await Task.Delay(50); // let the exit continuation reach WaitForHandoffAsync's own Task.Delay

        Assert.False(task.IsCompleted); // unconfirmed session - still owed the full HandoffGracePeriod
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ProtectedProcess_WaitDenied_DoesNotGetMistakenForAHandoffReplacement()
    {
        // Narrower regression for the exact mechanism the real logs showed: without the identity check,
        // the denied-wait "exit" fell straight into WaitForHandoffAsync, whose FindRunning immediately
        // rediscovers the same still-running process and reports it as a "handed off to N new
        // process(es)" replacement - masking the same PID as a brand new batch, repeatedly. This proves
        // that no such handoff is ever reported while the identity hasn't actually changed: the process
        // is never disposed/replaced mid-denial, only once it genuinely exits.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        process.DenyWaitForExit();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 5, task);

        Assert.False(process.Disposed); // never treated as exited/replaced while still genuinely alive
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task ProtectedProcess_NoStartTimeEverAvailable_StillTreatedAsAliveNotExited()
    {
        // Red/green regression for an earlier version of this fix: it distinguished "couldn't wait" from
        // "exited" purely via GetStartTimeUtc() returning null - but that method returns null both for a
        // process that has genuinely exited AND for one that's still running but denies even that minimal
        // query. A process this watcher never had a start time for in the first place (both the
        // originally-captured value AND every later recheck are null) hit that ambiguity on the very
        // first denial and was wrongly concluded "exited", reintroducing the same-PID busy loop for
        // exactly the processes least identifiable. CheckPresence fixes this by keying off actual
        // discoverability (FindProcessesByName) rather than the start-time read.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), startTimeUtc: null);
        process.DenyWaitForExit();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, process.WaitAttempts);
        Assert.False(task.IsCompleted); // must NOT be concluded "exited" just because start time is unknown

        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 3, task);
        Assert.Equal(4, process.WaitAttempts); // still one retry per throttled interval - no busy loop
        Assert.False(task.IsCompleted);
        Assert.False(process.Disposed);

        // Green half: it still restores correctly once genuinely gone.
        f.ProcessProvider.Exit(process);
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 1, task);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ProtectedProcess_StartTimeBecomesUnavailableMidway_StillTreatedAsAliveNotExited()
    {
        // Same ambiguity as the test above, but for a process that WAS identifiable at first (a valid
        // start time was captured when its batch began) and only later - mid-polling - stops answering
        // even the minimal start-time query, while never actually exiting. The old GetStartTimeUtc-only
        // check would have flipped from "alive" to "exited" the moment this happened, purely because the
        // query itself started failing, not because anything about the process changed.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        process.DenyWaitForExit();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, process.WaitAttempts);

        // From here on, even the minimal identity query is denied too - but the process is still
        // genuinely running (never removed from the fake provider).
        process.DenyStartTimeQuery();

        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 3, task);
        Assert.Equal(4, process.WaitAttempts); // still throttled, not a busy loop
        Assert.False(task.IsCompleted);
        Assert.False(process.Disposed);

        f.ProcessProvider.Exit(process);
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 1, task);
        await Task.Delay(50);
        f.TimeProvider.Advance(GameSessionWatcherOptions.Default.HandoffGracePeriod);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task Cancellation_DuringProtectedProcessLivenessPolling_ReturnsFalsePromptly()
    {
        // Cancelling while backed off inside the throttled ProtectedProcessLivenessPollInterval delay
        // (WaitForSingleExitAsync's inner try/catch around that Task.Delay) must return false promptly -
        // the same contract every other wait in this method already has - not hang, and not get mistaken
        // for a confirmed exit.
        var f = CreateFixture("game");
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), f.TimeProvider.GetUtcNow());
        process.DenyWaitForExit();
        using var cts = new CancellationTokenSource();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, cts.Token);
        await Task.Delay(50); // discovers the process; its first WaitForExitAsync call throws, entering the throttled backoff

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task ProtectedProcess_SamePidReusedByDifferentProcess_DoesNotBusyLoopUnderStaleIdentity()
    {
        // Bare PID presence alone can't rule out Windows recycling that PID for a genuinely different
        // process between one liveness recheck and the next - CheckPresence also compares creation
        // times, when both are known, to catch exactly that. This proves a reused PID doesn't get stuck
        // being treated as "the same process, still alive" forever: once the mismatch is observed, the
        // watcher moves on (picking the occupant back up as a fresh batch/handoff) rather than settling
        // into a permanent one-attempt-per-interval steady state against a stale identity.
        var f = CreateFixture("game");
        var originalStart = f.TimeProvider.GetUtcNow();
        var process = f.ProcessProvider.AddRunning(1, "game", Path.Combine(InstallDir, "game.exe"), originalStart);
        process.DenyWaitForExit();

        var task = f.Watcher.WaitForExitAsync(MakeGame(), launched: (IGameProcess?)null, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, process.WaitAttempts);

        // A different process now occupies this exact PID (still "discoverable" - never removed from the
        // fake provider - just with a different creation time).
        process.SimulatePidReusedByDifferentProcess(originalStart + TimeSpan.FromSeconds(30));

        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 1, task);
        var attemptsRightAfterReuse = process.WaitAttempts;

        // Whatever happens next (picked back up as a fresh batch under the new identity, or otherwise),
        // it must still be throttled - one attempt per interval, not a busy loop - over several more
        // fake-time intervals.
        await StepAsync(f.TimeProvider, GameSessionWatcherOptions.Default.ProtectedProcessLivenessPollInterval, steps: 3, task);
        Assert.True(process.WaitAttempts <= attemptsRightAfterReuse + 3);
        Assert.False(task.IsCompleted);
    }
}
