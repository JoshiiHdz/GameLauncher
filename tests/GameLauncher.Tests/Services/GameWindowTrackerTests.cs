using GameLauncher.Services;
using GameLauncher.Services.SessionTracking;
using GameLauncher.Tests.Services.SessionTracking;
using Microsoft.Extensions.Time.Testing;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises GameWindowTracker's state machine entirely through FakeTimeProvider and
/// FakeGameWindowProvider - no real Win32 window enumeration. Deliberately built and tested before any
/// Win32 call was written (see Win32GameWindowProvider), the same "seam first, OS integration second"
/// approach GameSessionWatcherTests already established for process tracking.
///
/// Every state change - initial candidate observation, arming, noticing a destruction, finding a
/// replacement - sits behind this tracker's own poll loop (there's no reactive TaskCompletionSource-style
/// signal the way process exit-waiting has). So advancing real wall-clock time (a bare
/// `await Task.Delay(50)`) only ever lets the state machine run whatever is already due; making it notice
/// something a test just injected (a Destroy(), a foreground change, a newly-added window) always
/// requires advancing the FAKE clock - see NoticeChangeAsync/LetStabilizeAsync.
///
/// The very first candidate window a watch ever observes fixes a permanent "baselineProcessId" - its
/// OWNING PROCESS, not the window itself. A later window from that SAME process can never arm either, no
/// matter how long it holds the foreground; only a window from a genuinely different process is ever
/// eligible. Most tests below are about properties unrelated to that distinction (PID handling, HWND
/// reuse, replacement selection, cancellation), so WarmPastBaselineAsync burns through a disposable
/// candidate on its own dedicated WarmupPid first, leaving the process id actually under test free to
/// arm via the normal path - keeping those tests fast and focused. The baseline mechanism itself, and the
/// two-stage-bootstrap case it exists for, are covered directly by their own tests further down.
/// </summary>
public class GameWindowTrackerTests
{
    private const int Pid = 100;

    /// <summary>Used only by WarmPastBaselineAsync, in tests where the baseline mechanism itself isn't
    /// what's being tested - keeping it a dedicated, never-reused id makes sure it can never coincide
    /// with a pid a test cares about elsewhere.</summary>
    private const int WarmupPid = 1;

    private sealed record Fixture(GameWindowTracker Tracker, FakeTimeProvider TimeProvider, FakeGameWindowProvider WindowProvider);

    private static Fixture CreateFixture()
    {
        var timeProvider = new FakeTimeProvider();
        var windowProvider = new FakeGameWindowProvider();
        var tracker = new GameWindowTracker(timeProvider, windowProvider, GameWindowTrackerOptions.Default);
        return new Fixture(tracker, timeProvider, windowProvider);
    }

    private static IReadOnlySet<int> FixedIds(params int[] ids) => new HashSet<int>(ids);

    /// <summary>Same two-Advance()-styles caveat as GameSessionWatcherTests.StepAsync: small steps with a
    /// real yield between them are needed to drive the loop through discrete polls where a test changes
    /// state in between; a single big Advance() only fires what's due at that instant.</summary>
    private static async Task StepAsync(FakeTimeProvider timeProvider, TimeSpan step, int steps, Task task)
    {
        for (var i = 0; i < steps && !task.IsCompleted; i++)
        {
            timeProvider.Advance(step);
            await Task.Delay(20);
        }
    }

    private static Task NoticeChangeAsync(FakeTimeProvider timeProvider, Task task) =>
        StepAsync(timeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

    /// <summary>Jumps straight past StabilizationPeriod in one Advance() - nothing needs to change
    /// mid-wait, so a single big jump (the "entire deadline in one step" pattern) is enough, unlike
    /// NoticeChangeAsync's discrete stepping. Must only be called once a candidate is ALREADY being
    /// observed (with an earlier FirstSeenUtc) - if discovery itself would only happen at the jumped-to
    /// instant, stabilization can never be satisfied by the same jump; call NoticeChangeAsync first to
    /// let discovery happen, then this to let it stabilize.</summary>
    private static async Task LetStabilizeAsync(FakeTimeProvider timeProvider)
    {
        timeProvider.Advance(GameWindowTrackerOptions.Default.StabilizationPeriod + GameWindowTrackerOptions.Default.WindowPollInterval);
        await Task.Delay(50);
    }

    /// <summary>Observes and drops a disposable candidate on its own dedicated WarmupPid first, so that
    /// whichever process id a test actually cares about is free to arm normally instead of incidentally
    /// becoming the baseline itself. The caller's `getTrackedProcessIds` must already include WarmupPid
    /// from the start for this to have anything to discover.</summary>
    private static async Task WarmPastBaselineAsync(Fixture f, Task task)
    {
        var dummy = f.WindowProvider.AddWindow(WarmupPid, "Warm-up baseline");
        f.WindowProvider.SetForeground(dummy);
        await Task.Delay(50); // lets the tracker's very first (pre-dummy) discovery check finish scheduling its poll delay

        // Advance the FAKE clock so the tracker actually discovers `dummy` and claims WarmupPid as the
        // baseline process BEFORE it's destroyed - a bare real-time delay above does nothing to the
        // tracker's own fake-time-based poll loop, so skipping this step would destroy `dummy` before it
        // was ever observed, leaving baselineProcessId unset and letting the real process under test
        // become the (unintended) baseline instead.
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        f.WindowProvider.Destroy(dummy);
        f.WindowProvider.SetForeground(null);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 2, task);
    }

    // ---- No candidate ever observed ------------------------------------------------------------------

    [Fact]
    public async Task NoCandidateEverObserved_NeverConcludes_OnlyCancellationEndsIt()
    {
        var f = CreateFixture();
        using var cts = new CancellationTokenSource();

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid), cts.Token);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 20, task);

        Assert.False(task.IsCompleted);

        cts.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    // ---- The baseline mechanism itself: process ownership, not window order --------------------------

    [Fact]
    public async Task SamePidSecondCandidate_NeverArms_RegardlessOfForegroundDuration()
    {
        // The very first candidate's OWNING PROCESS becomes the permanent baseline - a LATER window from
        // that exact same process must never arm either, no matter how long it individually holds the
        // foreground. Direct, minimal proof behind the two-stage bootstrap test below.
        var f = CreateFixture();
        var first = f.WindowProvider.AddWindow(Pid, "Splash");
        f.WindowProvider.SetForeground(first);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid), CancellationToken.None);
        await Task.Delay(50);

        f.WindowProvider.Destroy(first);
        var second = f.WindowProvider.AddWindow(Pid, "Main menu (same process)");
        f.WindowProvider.SetForeground(second);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);
        Assert.False(task.IsCompleted);

        // Holds the foreground far longer than StabilizationPeriod would ever require - still must not
        // arm, since its process never changed from the baseline.
        f.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        f.WindowProvider.Destroy(second);
        f.WindowProvider.SetForeground(null);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);

        // If `second` had wrongly armed, this would already have concluded true via the replacement
        // debounce by now.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.StabilizationPeriod
            + GameWindowTrackerOptions.Default.ReplacementDebounce + GameWindowTrackerOptions.Default.WindowPollInterval);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task TwoStageBootstrap_SplashThenLauncherSameProcess_ThenRealGameDifferentProcess_OnlyArmsOnTheRealGame()
    {
        // The exact real-world shape this redesign targets: a launcher/bootstrapper commonly keeps ONE
        // process across multiple screens (a splash screen, then its own main menu/loading UI) before
        // finally starting the actual game as a genuinely separate process - window ORDER alone (which
        // stage came "first" vs "later") cannot tell any of the launcher's own screens apart from one
        // another, only a change in OWNING PROCESS can. (A bootstrap chain of three or more fully
        // separate processes - a standalone splash.exe, launcher.exe, AND game.exe - remains an ambiguous
        // case this doesn't solve; GameSessionWatcher's process-exit tracking is the correct fallback
        // there, not a bigger timer.)
        const int gamePid = 777;
        var f = CreateFixture();
        var splash = f.WindowProvider.AddWindow(Pid, "Splash");
        f.WindowProvider.SetForeground(splash);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, gamePid), CancellationToken.None);
        await Task.Delay(50);

        f.WindowProvider.Destroy(splash);
        var launcher = f.WindowProvider.AddWindow(Pid, "Launcher main menu");
        f.WindowProvider.SetForeground(launcher);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        // Holds the foreground for 3 seconds - comfortably past StabilizationPeriod (1.5s), exactly what
        // would have wrongly armed it under a purely window-order-based rule.
        f.TimeProvider.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // same process as the baseline - never became a candidate at all

        f.WindowProvider.Destroy(launcher);
        f.WindowProvider.SetForeground(null);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);
        Assert.False(task.IsCompleted);

        // Seven full seconds with nothing at all - proves this never manufactures a false conclusion
        // while waiting for the real game.
        await StepAsync(f.TimeProvider, TimeSpan.FromSeconds(1), steps: 7, task);
        Assert.False(task.IsCompleted);

        var game = f.WindowProvider.AddWindow(gamePid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed now (a different process), but still running - not concluded

        f.WindowProvider.Destroy(game);
        await NoticeChangeAsync(f.TimeProvider, task);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Splash/launcher windows must never trigger a premature conclusion --------------------------

    [Fact]
    public async Task SplashWindow_ClosesBeforeGameplay_NeverConcludes_ResumesDiscovery()
    {
        // Splash is the baseline (first-ever) process here, so it never even becomes an observed
        // candidate in the first place, regardless of how many ticks pass while it's alive.
        var f = CreateFixture();
        var splash = f.WindowProvider.AddWindow(Pid, "Loading...");
        f.WindowProvider.SetForeground(splash);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid), CancellationToken.None);
        await Task.Delay(50);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        f.WindowProvider.Destroy(splash);
        f.WindowProvider.SetForeground(null);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);
        Assert.False(task.IsCompleted);

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.StabilizationPeriod
            + GameWindowTrackerOptions.Default.ReplacementDebounce + GameWindowTrackerOptions.Default.WindowPollInterval);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // must NOT conclude - it was never armed
    }

    [Fact]
    public async Task MultipleCandidates_OnlyTheForegroundOneArms()
    {
        // A launcher window can remain open (a valid "candidate" by the weak heuristic) behind the real
        // game the whole time - only whichever window is actually foreground is ever considered.
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var launcher = f.WindowProvider.AddWindow(Pid, "Launcher");
        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(launcher);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        // Foreground switches to the real game before the launcher ever stabilizes.
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 2, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on the game, not concluded

        // The launcher (never armed, still technically a "candidate") lingers untouched the whole time -
        // destroying the ARMED game window must still conclude correctly despite it still existing.
        f.WindowProvider.Destroy(game);
        await NoticeChangeAsync(f.TimeProvider, task);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // the still-open, never-foreground launcher was correctly never a replacement
    }

    // ---- A lingering window must not be mistaken for a valid replacement -----------------------------

    [Fact]
    public async Task LauncherWindowRegainsForegroundAfterGameplayCloses_IsNotMistakenForReplacement()
    {
        // A launcher window that was already OBSERVED once (even if it never armed) must not be accepted
        // as a genuine replacement later just because it happens to regain the foreground right as the
        // real, armed gameplay window closes.
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var launcher = f.WindowProvider.AddWindow(Pid, "Launcher");
        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(launcher);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 2, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted);

        f.WindowProvider.Destroy(game);
        f.WindowProvider.SetForeground(launcher); // the still-open launcher regains focus right away
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted); // NOT accepted as a replacement - already seen, rejected

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // concluded correctly despite the launcher being foreground at that moment
    }

    [Fact]
    public async Task BackgroundLauncherNeverSelectedAsForeground_IsNotMistakenForReplacementWhenGameplayCloses()
    {
        // Discovery only ever inspects the CURRENT foreground window when picking a candidate to observe
        // - a launcher sitting open in the background for the WHOLE watch (never once foreground, never
        // individually selected/observed) must still be excluded from ever being accepted as a "new"
        // replacement. Proves the continuous per-tick history scan (which runs regardless of arming
        // state) is what catches this - the selection path alone would miss it entirely.
        const int launcherPid = 200;
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, launcherPid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var launcher = f.WindowProvider.AddWindow(launcherPid, "Background Launcher"); // never made foreground
        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // records BOTH into history
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on game; launcher still sitting in the background, never selected

        f.WindowProvider.Destroy(game);
        f.WindowProvider.SetForeground(null);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted);

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // the never-selected, still-open launcher was correctly never a valid replacement
    }

    [Fact]
    public async Task ReplacementRecreatedWhileAnotherAppHasFocus_IsStillRecognized()
    {
        // A legitimate replacement (a fullscreen/resolution switch recreating the game's window, or a
        // handoff to a new stage) must be recognized even if the player has Alt-Tabbed away to some other
        // application at that exact moment - foreground is only ever used for arming/selection, never
        // required for a replacement to be accepted once a chain is already confirmed.
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted);

        f.WindowProvider.Destroy(game);
        f.WindowProvider.SetForeground(null); // some unrelated, untracked application now has focus
        await NoticeChangeAsync(f.TimeProvider, task); // enters the replacement debounce for `game`

        // The game recreates its window (e.g. a fullscreen toggle) - still without taking focus back.
        var recreated = f.WindowProvider.AddWindow(Pid, "Real Game (Fullscreen)");
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        // Advance well past where `game`'s ORIGINAL debounce would have expired on its own - if
        // `recreated` had been wrongly rejected for lacking the foreground, the task would already have
        // concluded true by now purely from that original timeout, regardless of `recreated` ever
        // existing. Still running here is what actually proves `recreated` was accepted.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // recognized as a replacement despite lacking the foreground

        // Proves it's genuinely tracked now, not a fluke: destroying IT starts a FRESH debounce of its own.
        f.WindowProvider.Destroy(recreated);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted); // within recreated's own fresh debounce, not concluded yet

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ReplacementReusesAHandleAlreadySeenForADifferentTrackedProcess_IsStillRecognized()
    {
        // History must key on (handle, pid) TOGETHER, not the numeric handle alone - otherwise a handle
        // value that happened to belong to an earlier, unrelated tracked-process window could wrongly
        // exclude a legitimate later replacement that coincidentally reuses that same numeric value for a
        // genuinely different (but still currently-tracked) process.
        const int otherPid = 200;
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, otherPid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var earlier = f.WindowProvider.AddWindow(otherPid, "Earlier, unrelated window");
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // recorded via the per-tick scan
        f.WindowProvider.Destroy(earlier);

        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on game

        f.WindowProvider.Destroy(game);
        f.WindowProvider.SetForeground(null);
        await NoticeChangeAsync(f.TimeProvider, task);

        // Reuses `earlier`'s exact numeric handle, but for Pid (the game's own process) - a genuinely new
        // (handle, pid) identity despite the recycled handle number.
        var replacement = f.WindowProvider.AddWindowWithHandle(earlier.Handle, Pid, "Real Game (Fullscreen)");
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 2, task);

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // recognized as a replacement, not rejected by handle-only history

        f.WindowProvider.Destroy(replacement);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Tracked process id set changing while the old window remains --------------------------------

    [Fact]
    public async Task ArmedWindowsProcessLeavesTrackedSet_ResumesDiscoveryRatherThanTrustingStaleWindow()
    {
        const int handedOffPid = 300;
        var trackedIds = new HashSet<int> { Pid, WarmupPid };
        var f = CreateFixture();

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => trackedIds, CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var window = f.WindowProvider.AddWindow(Pid);
        f.WindowProvider.SetForeground(window);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed

        // The process-side watch hands off to a new pid; `window` (still alive!) is no longer relevant.
        trackedIds = [handedOffPid];
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);
        Assert.False(task.IsCompleted); // de-armed, back to discovery - NOT concluded just because it left the set

        // The new process's own window appears and stabilizes - proves discovery actually resumed rather
        // than getting stuck.
        var newWindow = f.WindowProvider.AddWindow(handedOffPid);
        f.WindowProvider.SetForeground(newWindow);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);

        f.WindowProvider.Destroy(newWindow);
        await NoticeChangeAsync(f.TimeProvider, task);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- HWND reuse -------------------------------------------------------------------------------

    [Fact]
    public async Task ArmedWindowsHandleReusedByDifferentProcess_IsNotMistakenForStillAlive()
    {
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var window = f.WindowProvider.AddWindow(Pid);
        f.WindowProvider.SetForeground(window);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed

        const int unrelatedPid = 999;
        f.WindowProvider.SimulateHandleReusedByDifferentProcess(window, unrelatedPid);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted); // treated as destroyed - now in the replacement debounce

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // the reused handle was correctly never accepted as "still the same window"
    }

    // ---- Liveness is checked, never re-derived from the candidate search (post-arming) ---------------

    [Fact]
    public async Task ArmedWindow_NoLongerMatchingCandidateSearch_IsNotMistakenForDestroyed()
    {
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var window = f.WindowProvider.AddWindow(Pid);
        f.WindowProvider.SetForeground(window);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed

        f.WindowProvider.TemporarilyExcludeFromCandidateSearch(window);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce + GameWindowTrackerOptions.Default.WindowPollInterval);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // still alive per IsWindowAlive - must not be treated as destroyed

        f.WindowProvider.IncludeInCandidateSearch(window);
        f.WindowProvider.Destroy(window);
        await NoticeChangeAsync(f.TimeProvider, task);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // still concludes correctly once genuinely destroyed
    }

    // ---- Cancellation in every phase -----------------------------------------------------------------

    [Fact]
    public async Task Cancellation_WhileObservingBeforeArming_ReturnsFalsePromptly()
    {
        var f = CreateFixture();
        using var cts = new CancellationTokenSource();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), cts.Token);
        await WarmPastBaselineAsync(f, task);

        var window = f.WindowProvider.AddWindow(Pid);
        f.WindowProvider.SetForeground(window);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // observing, not yet stabilized

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    [Fact]
    public async Task Cancellation_DuringReplacementDebounceAfterArming_ReturnsFalsePromptly()
    {
        var f = CreateFixture();
        using var cts = new CancellationTokenSource();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), cts.Token);
        await WarmPastBaselineAsync(f, task);

        var window = f.WindowProvider.AddWindow(Pid);
        f.WindowProvider.SetForeground(window);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed

        f.WindowProvider.Destroy(window);
        await NoticeChangeAsync(f.TimeProvider, task); // enters the replacement debounce

        cts.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result);
    }

    // ---- Same-poll destroy+recreate: the ordering fix -------------------------------------------------

    [Fact]
    public async Task ReplacementCreatedWithinTheSamePollInterval_IsNotExcludedByThatTicksOwnHistoryRecording()
    {
        // The game can destroy and recreate its window between two polls entirely - by the time the
        // tracker next checks, FindGameplayWindows already shows the NEW window and IsWindowAlive already
        // reports the OLD one gone, both within the same tick. The new window must still be recognized as
        // a genuine replacement, not excluded because that same tick's own candidate scan would otherwise
        // have recorded it into history before the replacement check ever ran.
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed

        // Both happen before the tracker gets a single chance to poll in between.
        f.WindowProvider.Destroy(game);
        var recreated = f.WindowProvider.AddWindow(Pid, "Real Game (Fullscreen)");
        f.WindowProvider.SetForeground(recreated);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // the very next poll sees BOTH events at once

        // Advance well past where the ORIGINAL debounce would have expired on its own - if `recreated`
        // had been wrongly excluded, the task would already have concluded true by now.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // recognized immediately, not rejected by this tick's own history scan

        f.WindowProvider.Destroy(recreated);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    [Fact]
    public async Task ReplacementCreatedWithinTheSamePollInterval_StillDistinguishesFromAnOverlappingKnownBackgroundWindow()
    {
        // A previously-seen background window (e.g. a launcher) can be present in the SAME tick a
        // same-poll destroy+recreate happens - the immediate same-tick check must still pick only the
        // genuinely new window, not the already-known background one, even though both appear together in
        // that tick's candidate scan.
        const int launcherPid = 200;
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, launcherPid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var launcher = f.WindowProvider.AddWindow(launcherPid, "Background Launcher"); // never foreground
        var game = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // launcher AND game recorded
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on game; launcher still sitting in the background

        f.WindowProvider.Destroy(game);
        var recreated = f.WindowProvider.AddWindow(Pid, "Real Game (Fullscreen)");
        f.WindowProvider.SetForeground(recreated);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task); // launcher + recreated BOTH present this tick

        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // recognized `recreated`, not the already-known `launcher`

        // Proves it's genuinely tracking `recreated` (not `launcher`, which is still alive and untouched):
        // destroying `recreated` with nothing left must conclude, even though `launcher` still exists.
        f.WindowProvider.Destroy(recreated);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Overlapping old/new gameplay windows: prior observation alone must not disqualify -----------

    [Fact]
    public async Task ReplacementAppearsWhileArmedWindowStillAlive_IsNotDisqualifiedByThatOverlapTick()
    {
        // A render-window recreate can create the NEW surface before destroying the OLD one, rather than
        // the other way around - so the tracker can observe both existing together, for one or more polls,
        // BEFORE the original is destroyed. That earlier co-existence must not by itself disqualify the new
        // window from later being recognized as the legitimate replacement once the original is gone -
        // "previously observed" alone isn't stale-window proof, only having been observed BEFORE anything
        // ever armed is (see the class remarks).
        var f = CreateFixture();
        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, WarmupPid), CancellationToken.None);
        await WarmPastBaselineAsync(f, task);

        var gameA = f.WindowProvider.AddWindow(Pid, "Real Game");
        f.WindowProvider.SetForeground(gameA);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on gameA

        // gameB appears while gameA is STILL alive - one full poll records this co-existence.
        var gameB = f.WindowProvider.AddWindow(Pid, "Real Game (new surface)");
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        Assert.False(task.IsCompleted); // still armed on gameA; gameB merely coexists so far

        // gameA closes; gameB remains alive - it must be recognized as the replacement.
        f.WindowProvider.Destroy(gameA);
        await NoticeChangeAsync(f.TimeProvider, task);

        // Advance well past where the ORIGINAL debounce would have expired on its own - if `gameB` had
        // been wrongly excluded as "already seen", the task would already have concluded true by now.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // recognized gameB as the replacement, not disqualified by the overlap tick

        // Proves it's genuinely tracking gameB now, not a fluke: destroying it with nothing left must conclude.
        f.WindowProvider.Destroy(gameB);
        await NoticeChangeAsync(f.TimeProvider, task);
        Assert.False(task.IsCompleted);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ---- Baseline exclusion must apply to replacement selection too -----------------------------------

    [Fact]
    public async Task BaselineProcessOpeningAFreshWindowAfterGameplayCloses_IsNotAcceptedAsReplacement()
    {
        // The baseline process is "permanently ineligible" - that exclusion has to apply identically to
        // replacement selection, not just initial arming. A brand-new window from that same still-tracked
        // process (the launcher reopening itself, or a fresh dialog) after the real, armed gameplay window
        // closes must not be accepted just because its specific (handle, pid) identity has never been
        // seen before - it's still the wrong process.
        const int gamePid = 777;
        var f = CreateFixture();
        var launcher = f.WindowProvider.AddWindow(Pid, "Launcher"); // becomes the baseline process
        f.WindowProvider.SetForeground(launcher);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, gamePid), CancellationToken.None);
        await Task.Delay(50);

        f.WindowProvider.Destroy(launcher);
        var game = f.WindowProvider.AddWindow(gamePid, "Real Game");
        f.WindowProvider.SetForeground(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);
        await LetStabilizeAsync(f.TimeProvider);
        Assert.False(task.IsCompleted); // armed on game (a different process than the baseline)

        f.WindowProvider.Destroy(game);
        f.WindowProvider.SetForeground(null);
        await NoticeChangeAsync(f.TimeProvider, task); // enters the replacement debounce

        // The baseline process (still tracked!) opens a BRAND NEW window - never-before-seen identity,
        // but still the same permanently-ineligible process.
        var freshBaselineWindow = f.WindowProvider.AddWindow(Pid, "Launcher (reopened)");
        f.WindowProvider.SetForeground(freshBaselineWindow);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 2, task);

        // Advance well past where the debounce would have expired on its own - if the fresh baseline
        // window had been wrongly accepted, the task would be permanently stuck "armed" on it (never
        // completing) instead of correctly timing out.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result); // correctly concluded - the fresh baseline-process window was never valid
    }

    // ---- Product limitation: explicitly tracked, not silently assumed ---------------------------------

    [Fact]
    public async Task ProductLimitation_DirectLaunchGameWithNoSeparateLauncher_NeverArmsViaWindowSignal()
    {
        // Documented, accepted tradeoff: since the FIRST candidate a watch ever observes always becomes
        // the (permanently ineligible) baseline process - regardless of whether it's actually a launcher
        // or the real game itself - a game with no separate launcher stage gets NO faster restoration
        // from this signal, for its entire session; GameSessionWatcher's process-exit tracking is the
        // only signal for these launches. This is intentional (no fixed-duration timer can safely
        // distinguish "held foreground a while" from "definitely real gameplay" on its own - see the
        // class remarks), not a bug - this test exists so the tradeoff stays visible and tracked rather
        // than silently assumed.
        var f = CreateFixture();
        var game = f.WindowProvider.AddWindow(Pid, "Direct-launch game (no separate launcher)");
        f.WindowProvider.SetForeground(game);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid), CancellationToken.None);
        await Task.Delay(50);

        // Even holding the foreground far longer than any real launcher screen plausibly would.
        f.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Assert.False(task.IsCompleted); // never armed - the window signal never engages for this launch

        f.WindowProvider.Destroy(game);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);

        // Still never concludes via the window signal, even now that the game has genuinely exited -
        // GameSessionWatcher's process-exit tracking remains the only thing that will ever restore the
        // launcher for this session.
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce
            + GameWindowTrackerOptions.Default.StabilizationPeriod + GameWindowTrackerOptions.Default.WindowPollInterval);
        await Task.Delay(50);
        Assert.False(task.IsCompleted);
    }

    // ---- Known, unresolved limitation: tracked here, not fixed -----------------------------------------

    [Fact(Skip = "Known limitation, tracked but not fixed: a bootstrap chain of three or more genuinely " +
        "separate processes (splash.exe -> a SEPARATE launcher.exe -> game.exe) is not caught by " +
        "process-ownership alone - the second, still-not-real process differs from the baseline just as " +
        "much as the real game does, so it can still arm prematurely. Closing this needs corroboration " +
        "this tracker doesn't have access to yet (e.g. GameSessionWatcher's own process-chain " +
        "confirmation) - see GameWindowTracker's class remarks. This test asserts the DESIRED behavior as " +
        "the acceptance criterion for whatever eventually closes the gap.")]
    public async Task ThreeDistinctProcessBootstrap_SplashThenSeparateLauncherProcess_ThenGame_DoesNotArmOnTheLauncher()
    {
        const int launcherPid = 200;
        const int gamePid = 777;
        var f = CreateFixture();
        var splash = f.WindowProvider.AddWindow(Pid, "Splash"); // becomes the baseline process
        f.WindowProvider.SetForeground(splash);

        var task = f.Tracker.WaitForGameplayWindowExitAsync(() => FixedIds(Pid, launcherPid, gamePid), CancellationToken.None);
        await Task.Delay(50);

        f.WindowProvider.Destroy(splash);
        var launcher = f.WindowProvider.AddWindow(launcherPid, "Separate launcher process"); // a DIFFERENT process than the baseline
        f.WindowProvider.SetForeground(launcher);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 1, task);

        f.TimeProvider.Advance(TimeSpan.FromSeconds(3)); // past StabilizationPeriod
        await Task.Delay(50);

        // Destroying `launcher` here and observing whether that alone concludes the watch is what
        // actually proves whether it armed - a bare "task not completed" check right after the 3s advance
        // is trivially true either way (arming itself never completes the task, only a later destruction
        // with no replacement does), so it cannot tell "correctly never armed" apart from "armed, but
        // just hasn't been destroyed yet."
        f.WindowProvider.Destroy(launcher);
        f.WindowProvider.SetForeground(null);
        await StepAsync(f.TimeProvider, GameWindowTrackerOptions.Default.WindowPollInterval, steps: 3, task);
        f.TimeProvider.Advance(GameWindowTrackerOptions.Default.ReplacementDebounce);
        await Task.Delay(50);

        // Desired: still not armed, so destroying `launcher` (never having been the real game) must NOT
        // conclude the watch - it should simply resume discovery, exactly like a splash/launcher closing
        // during ordinary observation. The current, known-unresolved design instead arms `launcher` (it
        // isn't the baseline process) and concludes true here.
        Assert.False(task.IsCompleted);
    }
}
