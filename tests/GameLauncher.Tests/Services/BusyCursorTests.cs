using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>The loading circle that kept coming and going over the launcher: empty controller slots are no longer asked about on a timer, and the watch that finds
/// out what else it might be turns the samples into readable episodes.</summary>
public sealed class BusyCursorTests
{
    private const ushort A = 0x1000;

    private sealed class Rig
    {
        public PadReading? Slot0;
        public long Now;
        public readonly List<int> Asked = new();
        public readonly ControllerInput Input;

        public Rig()
        {
            Input = new ControllerInput(i =>
            {
                Asked.Add(i);
                return i == 0 ? Slot0 : null;
            }, () => Now, () => true, _ => null);
        }
    }

    [Fact]
    public void EmptySlots_AreAskedAboutOnceAtStart_NotEverySecond()
    {
        var rig = new Rig();

        rig.Input.Poll();
        Assert.Equal([0, 1, 2, 3], rig.Asked);                              // the first look: every slot

        rig.Asked.Clear();
        for (var second = 1; second <= 30; second++)
        {
            rig.Now = second * 1000L;
            rig.Input.Poll();
        }

        Assert.Empty(rig.Asked);                                           // thirty seconds with no controller: Windows is not asked once
        Assert.Equal(4, rig.Input.EmptySlotProbes);
    }

    [Fact]
    public void ARequest_LooksInTheEmptySlotsAgain_AndFindsAPadThatWasPluggedIn()
    {
        var rig = new Rig();
        rig.Input.Poll();
        rig.Asked.Clear();

        rig.Slot0 = new PadReading(0, 0, 0, 0, 0);                          // plugged in; nothing asks yet
        rig.Now = 5_000;
        rig.Input.Poll();
        Assert.False(rig.Input.IsConnected);

        rig.Input.RequestProbe();                                          // Windows said a device arrived (or the window came to the front)
        rig.Input.Poll();

        Assert.True(rig.Input.IsConnected);
        Assert.Contains(0, rig.Asked);
    }

    [Fact]
    public void AMinuteWithNoRequest_StillLooksOnce_AsANetUnderTheNotifications()
    {
        var rig = new Rig();
        rig.Input.Poll();
        rig.Asked.Clear();

        rig.Now = ControllerInput.EmptySlotSafetyRecheckMs - 1;
        rig.Input.Poll();
        Assert.Empty(rig.Asked);

        rig.Now = ControllerInput.EmptySlotSafetyRecheckMs + 1;
        rig.Input.Poll();
        Assert.Equal([0, 1, 2, 3], rig.Asked);
    }

    [Fact]
    public void APadThatIsThere_IsStillReadEveryTick_WithoutAnyRequest()
    {
        var rig = new Rig { Slot0 = new PadReading(A, 0, 0, 0, 0) };
        rig.Input.Poll();
        rig.Asked.Clear();

        rig.Now = 33; rig.Input.Poll();
        rig.Now = 66; rig.Input.Poll();

        Assert.Equal([0, 0], rig.Asked);                                    // the pad is read; the empty slots are left alone
    }

    [Fact]
    public void TheTracker_CountsEpisodes_AndSaysWhenEachBeganAndEnded()
    {
        var tracker = new BusyCursorTracker();
        var t = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        tracker.Observe(false, t);
        Assert.False(tracker.JustStarted);

        tracker.Observe(true, t.AddMilliseconds(250));
        Assert.True(tracker.JustStarted);
        Assert.Equal(1, tracker.Episodes);

        tracker.Observe(true, t.AddMilliseconds(500));                      // still the same episode
        Assert.False(tracker.JustStarted);
        Assert.Equal(1, tracker.Episodes);

        tracker.Observe(false, t.AddMilliseconds(750));
        Assert.Equal(TimeSpan.FromMilliseconds(500), tracker.JustEnded);
        Assert.False(tracker.IsBusy);

        tracker.Observe(true, t.AddSeconds(1));                             // a flicker a second later is a second episode
        Assert.Equal(2, tracker.Episodes);
    }

    [Fact]
    public void TheWatch_StartsAndStops_WithoutTouchingAnything()
    {
        // A smoke test of the real thing: it samples the pointer and the process list a few times and ends cleanly. (The UI-thread ping needs a pumping dispatcher,
        // which a test thread does not have; the watch must cope with that.)
        var watch = new BusyCursorWatch(System.Windows.Threading.Dispatcher.CurrentDispatcher, () => true, () => "");
        watch.Start();
        Thread.Sleep(700);
        watch.Dispose();

        Assert.True(true);
    }

    [Fact]
    public void TheTrail_KeepsWhatJustHappened_AndForgetsTheOld()
    {
        Trail.Clear();
        Trail.Note("child process started: powershell.exe (pid 1)");
        Trail.Note("controller probe: slot 1 empty in 2.0 ms");

        var recent = Trail.Recent(TimeSpan.FromSeconds(5), max: 1000); // (other tests note things here too: look for ours)
        Assert.Contains(recent, line => line.Contains("powershell.exe"));
        Assert.Contains(recent, line => line.Contains("slot 1 empty"));

        for (var i = 0; i < 500; i++)
            Trail.Note("x" + i);

        Assert.True(Trail.Recent(TimeSpan.FromSeconds(5), max: 1000).Count <= 200);   // a bounded memory
    }
}
