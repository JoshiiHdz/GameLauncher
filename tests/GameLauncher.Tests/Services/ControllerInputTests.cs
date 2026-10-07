using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>The controller reader: presses, repeats, triggers, the stick as a D-pad, connection changes, and - the part that matters most - reading
/// nothing while the launcher is not allowed to (another window in front, a game running).</summary>
public sealed class ControllerInputTests
{
    private const ushort Up = 0x1, Left = 0x4, Right = 0x8, Start = 0x10, LB = 0x100, RB = 0x200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private sealed class Rig
    {
        public PadReading? Slot0;
        public long Now;
        public bool MayRead = true;
        public readonly List<int> Asked = new();
        public readonly List<PadButton> Pressed = new();
        public readonly List<bool> Connection = new();
        public readonly List<PadBattery?> Batteries = new();
        public PadBattery? Battery;
        public readonly ControllerInput Input;

        public Rig()
        {
            Input = new ControllerInput(i =>
            {
                Asked.Add(i);
                return i == 0 ? Slot0 : null;
            }, () => Now, () => MayRead, i => i == 0 ? Battery : null);
            Input.ButtonPressed += Pressed.Add;
            Input.ConnectionChanged += Connection.Add;
            Input.BatteryChanged += Batteries.Add;
        }

        public void Hold(ushort buttons, short lx = 0, short ly = 0, byte lt = 0, byte rt = 0) => Slot0 = new PadReading(buttons, lx, ly, lt, rt);

        /// <summary>A pad already there with nothing pressed (a button already down when a pad first appears is not a press, so tests plug it in first).</summary>
        public void Plugged()
        {
            Hold(0);
            Input.Poll();
            Pressed.Clear(); Connection.Clear(); Batteries.Clear(); Asked.Clear();
        }
    }

    [Theory]
    [InlineData(A, PadButton.Accept)]
    [InlineData(B, PadButton.Back)]
    [InlineData(Y, PadButton.Y)]
    [InlineData(X, PadButton.X)]
    [InlineData(Start, PadButton.Start)]
    [InlineData(LB, PadButton.LB)]
    [InlineData(RB, PadButton.RB)]
    [InlineData(Up, PadButton.Up)]
    public void EachButton_IsOnePress_AndHoldingItDoesNotFireAgain(ushort buttons, PadButton expected)
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(buttons);

        rig.Input.Poll();
        rig.Now += 10;
        rig.Input.Poll();

        Assert.Equal([expected], rig.Pressed);
    }

    [Fact]
    public void TheLeftStick_ActsAsTheDpad_OnlyPastHalfWay()
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(0, lx: 9000);
        rig.Input.Poll();
        Assert.Empty(rig.Pressed);

        rig.Hold(0, lx: 20000);
        rig.Input.Poll();
        Assert.Equal([PadButton.Right], rig.Pressed);
    }

    [Fact]
    public void Triggers_AreThePageButtons_PastHalfWay()
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(0, lt: 60);
        rig.Input.Poll();
        Assert.Empty(rig.Pressed);

        rig.Hold(0, lt: 200, rt: 255);
        rig.Input.Poll();
        Assert.Equal([PadButton.LT, PadButton.RT], rig.Pressed.OrderBy(b => b).ToList());
    }

    [Fact]
    public void Directions_RepeatWhileHeld_AfterADelay_ButActionsDoNot()
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold((ushort)(Right | A));

        rig.Input.Poll();                                                  // both pressed once
        rig.Now = ControllerInput.RepeatDelayMs - 1; rig.Input.Poll();     // still inside the delay
        Assert.Equal(2, rig.Pressed.Count);

        rig.Now = ControllerInput.RepeatDelayMs; rig.Input.Poll();         // first repeat: the direction only
        Assert.Equal(3, rig.Pressed.Count);
        Assert.Equal(PadButton.Right, rig.Pressed[^1]);

        rig.Now += ControllerInput.RepeatIntervalMs - 10; rig.Input.Poll(); // too soon for the next
        Assert.Equal(3, rig.Pressed.Count);

        rig.Now += 10; rig.Input.Poll();
        Assert.Equal(4, rig.Pressed.Count);
    }

    [Fact]
    public void LettingGo_MakesTheNextPressNew()
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(A);
        rig.Input.Poll();
        rig.Hold(0);
        rig.Input.Poll();
        rig.Hold(A);
        rig.Input.Poll();

        Assert.Equal([PadButton.Accept, PadButton.Accept], rig.Pressed);
    }

    [Fact]
    public void WhenReadingIsNotAllowed_NoButtonIsFired_AndThePadIsOnlyCheckedOncePerSecond()
    {
        var rig = new Rig { MayRead = false };
        rig.Hold(A);

        rig.Input.Poll();
        var asked = rig.Asked.Count;
        rig.Now = 500; rig.Input.Poll();
        rig.Now = 900; rig.Input.Poll();

        Assert.Empty(rig.Pressed); // XInput is global: a button pressed in a game or another window is never acted on
        Assert.Equal(asked, rig.Asked.Count); // only the once-a-second "is a pad plugged in" check, not a reading per tick
    }

    [Fact]
    public void ThePadIsReportedConnected_EvenWhileButtonsAreNotBeingRead()
    {
        var rig = new Rig { MayRead = false };
        rig.Input.Poll();
        Assert.False(rig.Input.IsConnected);

        rig.Hold(A);
        rig.Now = ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        Assert.True(rig.Input.IsConnected);
        Assert.Equal([true], rig.Connection);
        Assert.Empty(rig.Pressed);

        rig.Slot0 = null;
        rig.Now += ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        Assert.False(rig.Input.IsConnected);
        Assert.Equal([true, false], rig.Connection);
    }

    [Fact]
    public void TheBattery_IsReported_WhenAPadIsFound_AndWhenItChanges_ButNotRepeatedly()
    {
        var rig = new Rig();
        rig.Battery = new PadBattery(false, PadBatteryLevel.Medium);
        rig.Hold(0);

        rig.Input.Poll();
        Assert.Equal(new PadBattery(false, PadBatteryLevel.Medium), rig.Input.Battery);
        Assert.Single(rig.Batteries);

        rig.Now += 100; rig.Input.Poll(); rig.Now += 100; rig.Input.Poll();
        Assert.Single(rig.Batteries); // unchanged: not raised again

        rig.Battery = new PadBattery(false, PadBatteryLevel.Low);
        rig.Now += ControllerInput.BatteryRecheckMs; rig.Input.Poll();
        Assert.Equal(2, rig.Batteries.Count);
        Assert.True(rig.Input.Battery!.Value.NeedsCharging);

        rig.Battery = new PadBattery(true, PadBatteryLevel.Full); // a cable is plugged in
        rig.Now += ControllerInput.BatteryRecheckMs; rig.Input.Poll();
        Assert.Equal("No battery reported", rig.Input.Battery!.Value.Describe());
        Assert.False(rig.Input.Battery!.Value.NeedsCharging);

        rig.Slot0 = null; // the pad goes away
        rig.Now += ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        Assert.Null(rig.Input.Battery);
    }

    [Fact]
    public void ABatteryReading_IsDescribedInWords()
    {
        Assert.Equal("Battery full", new PadBattery(false, PadBatteryLevel.Full).Describe());
        Assert.Equal("Battery empty", new PadBattery(false, PadBatteryLevel.Empty).Describe());
        Assert.True(new PadBattery(false, PadBatteryLevel.Empty).NeedsCharging);
        Assert.False(new PadBattery(false, PadBatteryLevel.Medium).NeedsCharging);
        Assert.False(new PadBattery(true, PadBatteryLevel.Empty).NeedsCharging); // a wired pad has no battery to run down
    }

    [Fact]
    public void AButtonStillDownWhenReadingResumes_IsNotAPress()
    {
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(A);
        rig.Input.Poll();
        Assert.Single(rig.Pressed);

        rig.MayRead = false; rig.Input.Poll();   // a game takes the screen; the player is still holding A
        rig.MayRead = true; rig.Input.Poll();    // back in the launcher

        Assert.Single(rig.Pressed); // not a second press
    }

    [Fact]
    public void ConnectionChanges_AreReported_OnceEach()
    {
        var rig = new Rig();
        rig.Input.Poll();
        Assert.Empty(rig.Connection);
        Assert.False(rig.Input.IsConnected);

        rig.Hold(0);
        rig.Now = ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        rig.Input.Poll();
        Assert.Equal([true], rig.Connection);
        Assert.True(rig.Input.IsConnected);

        rig.Slot0 = null;
        rig.Input.Poll();
        Assert.Equal([true, false], rig.Connection);
    }

    [Fact]
    public void EmptySlots_AreAskedAboutOnlyOncePerSecond()
    {
        var rig = new Rig();

        rig.Input.Poll();                       // all four slots asked once
        var first = rig.Asked.Count;
        rig.Now = 500; rig.Input.Poll();
        Assert.Equal(first, rig.Asked.Count);   // nothing new asked

        rig.Now = ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        Assert.Equal(first * 2, rig.Asked.Count);
    }

    [Fact]
    public void APressThatPollsAgain_FromInsideItsHandler_IsNotCountedTwice()
    {
        // A press can open a dialog that pumps messages, so the timer polls again before the first poll has returned.
        var rig = new Rig();
        rig.Plugged();
        rig.Hold(A);
        var nested = false;
        rig.Input.ButtonPressed += _ =>
        {
            if (nested) return;
            nested = true;
            rig.Input.Poll();
        };

        rig.Input.Poll();

        Assert.Single(rig.Pressed);
    }

    [Fact]
    public void ControllerScale_IsTwentyPercent_OnEveryScreenBigEnoughToStayAboveTheSmallLayout()
    {
        Assert.Equal(1.2, ControllerScale.Factor);
        Assert.Equal(1.2, ControllerScale.For(1920, 1080));
        Assert.Equal(1.2, ControllerScale.For(2560, 1440)); // a 4K screen at 150 percent Windows scaling: no bigger than a 1080p one
        Assert.Equal(1.2, ControllerScale.For(1280, 720));
        Assert.Equal(1.2, ControllerScale.For(1080, 672)); // exactly the smallest that still leaves the normal layout
        Assert.Equal(1.0, ControllerScale.For(1024, 768)); // scaled, it would drop under 900 wide
        Assert.Equal(1.0, ControllerScale.For(1366, 640));
    }

    [Fact]
    public void TheButtonThatWakesAPad_IsNotACommand_ButTheNextPressIs()
    {
        // A controller that switched itself off wakes with a button press; that press must not also press A in the launcher.
        var rig = new Rig();
        rig.Input.Poll();                       // no pad yet
        rig.Hold(A);                            // woken with A
        rig.Now = ControllerInput.EmptySlotRecheckMs; rig.Input.Poll();
        rig.Now += 50; rig.Input.Poll();
        Assert.True(rig.Input.IsConnected);
        Assert.Empty(rig.Pressed);              // not counted, however long it is held

        rig.Hold(0); rig.Now += 50; rig.Input.Poll();
        rig.Hold(A); rig.Now += 50; rig.Input.Poll();
        Assert.Equal([PadButton.Accept], rig.Pressed); // a fresh press counts
    }

    [Fact]
    public void APadThatGoesAwayAndComesBack_IsReportedBothWays()
    {
        var rig = new Rig();
        rig.Hold(0);
        rig.Input.Poll();
        Assert.Equal([true], rig.Connection);

        rig.Slot0 = null; rig.Now += 100; rig.Input.Poll();   // it switched itself off
        Assert.Equal([true, false], rig.Connection);

        rig.Hold(0); rig.Now += ControllerInput.EmptySlotRecheckMs; rig.Input.Poll(); // woken
        Assert.Equal([true, false, true], rig.Connection);
    }
}
