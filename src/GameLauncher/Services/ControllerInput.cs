using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameLauncher.Services;

/// <summary>A button on the controller, named as on an Xbox pad (the reference layout). What a button does is decided by <see cref="ControllerMap"/>.</summary>
public enum PadButton
{
    Up,
    Down,
    Left,
    Right,
    /// <summary>A - choose. Fixed: it cannot be remapped.</summary>
    Accept,
    /// <summary>B - back out. Fixed: it cannot be remapped.</summary>
    Back,
    /// <summary>X (remappable; searches by default).</summary>
    X,
    /// <summary>Y (remappable; opens Settings by default).</summary>
    Y,
    /// <summary>Menu / Start (remappable; the focused game's menu by default; with controller mode off it always turns it on).</summary>
    Start,
    /// <summary>LB, the left bumper (remappable; the tab before this one by default).</summary>
    LB,
    /// <summary>RB, the right bumper (remappable; the tab after this one by default).</summary>
    RB,
    /// <summary>LT, the left trigger (remappable; one page up by default).</summary>
    LT,
    /// <summary>RT, the right trigger (remappable; one page down by default).</summary>
    RT,
}

/// <summary>One reading of a controller, in the shape XInput reports it.</summary>
public readonly record struct PadReading(ushort Buttons, short ThumbLeftX, short ThumbLeftY, byte LeftTrigger = 0, byte RightTrigger = 0, short ThumbRightX = 0, short ThumbRightY = 0);

/// <summary>How full a wireless controller's battery is. XInput reports only these four steps (no percentage).</summary>
public enum PadBatteryLevel { Empty, Low, Medium, Full }

/// <summary>A controller's power: plugged in with a cable (no battery to report), or on batteries at a level. Signal strength is not offered by XInput
/// or by Windows.Gaming.Input, so it is not shown.</summary>
public readonly record struct PadBattery(bool Wired, PadBatteryLevel Level)
{
    /// <summary>"No battery reported", "Battery full", "Battery medium", "Battery low" or "Battery empty". XInput's "wired" type means only that the device has no battery to report:
    /// a cable, but also many wireless receivers (dongles) that do not pass the controller's battery on, so it is never called "wired" on screen.</summary>
    public string Describe() => Wired ? "No battery reported" : $"Battery {Level.ToString().ToLowerInvariant()}";

    public bool NeedsCharging => !Wired && Level is PadBatteryLevel.Empty or PadBatteryLevel.Low;
}

/// <summary>Turns controller readings into button presses for controller mode. Reads Xbox-style controllers (and anything that presents as one)
/// through XInput - Windows' own API, the one every PC game uses. XInput is read system-wide, so buttons are read only while the launcher is the
/// window in front (the caller decides): otherwise pressing A in a game would press A here too. Directions and page keys repeat while held, like a
/// keyboard; everything else fires once per press. Whether a pad is plugged in (and its battery) is still checked about once a second when buttons are
/// not being read, so the status pill is right in every theme. Empty slots are asked about at most once a second each: a question about an empty slot
/// costs a little more than one about a pad, and four of them a second is negligible.</summary>
public sealed class ControllerInput : IDisposable
{
    private const ushort DpadUp = 0x1, DpadDown = 0x2, DpadLeft = 0x4, DpadRight = 0x8, StartButton = 0x10;
    private const ushort LeftShoulder = 0x100, RightShoulder = 0x200, ButtonA = 0x1000, ButtonB = 0x2000, ButtonX = 0x4000, ButtonY = 0x8000;
    private const short StickThreshold = 16000; // about half of the stick's travel
    private const byte TriggerThreshold = 128; // about half of the trigger's travel
    internal const int RepeatDelayMs = 400, RepeatIntervalMs = 120, EmptySlotRecheckMs = 1000;
    private const int MaxControllers = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint index, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint index, out XInputState state);

    private static bool _useLegacyDll;

    /// <summary>Reads controller <paramref name="index"/> from Windows; null when none is plugged into that slot.</summary>
    private static PadReading? ReadFromWindows(int index)
    {
        try
        {
            XInputState state;
            var result = _useLegacyDll ? XInputGetState910((uint)index, out state) : XInputGetState14((uint)index, out state);
            return result == 0 ? new PadReading(state.Buttons, state.ThumbLX, state.ThumbLY, state.LeftTrigger, state.RightTrigger, state.ThumbRX, state.ThumbRY) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (_useLegacyDll)
                return null;

            _useLegacyDll = true; // an older Windows: fall back to the first XInput that shipped
            return ReadFromWindows(index);
        }
    }

    private readonly Func<int, PadReading?> _read;
    private readonly Func<long> _nowMs;
    private readonly Func<bool> _mayRead;
    private readonly Func<int, PadBattery?> _readBattery;
    private readonly Func<PadButton, bool> _repeats;
    private readonly Dictionary<(int Controller, PadButton Button), long> _heldSince = new();
    private readonly Dictionary<(int Controller, PadButton Button), long> _lastFired = new();
    private readonly long[] _nextProbe = new long[MaxControllers];
    private readonly bool[] _present = new bool[MaxControllers];
    private DispatcherTimer? _timer;
    private bool _connected;
    private bool _resuming; // the next reading follows a stretch with none: whatever is already down is not a new press

    /// <param name="read">Reads controller N, or null when absent. Windows' XInput unless a test supplies another.</param>
    /// <param name="nowMs">A millisecond clock for the key-repeat timing.</param>
    /// <param name="mayRead">Whether a reading may be taken right now (the launcher is in front and no game is running). Always, unless given.</param>
    /// <param name="repeats">Whether a held button repeats. The D-pad, stick and (by default) triggers do; the window turns the triggers' repeat off when they are given another job.</param>
    /// <param name="readBattery">Reads controller N's battery, or null when it has none to report. Windows' XInput unless a test supplies another.</param>
    public ControllerInput(Func<int, PadReading?>? read = null, Func<long>? nowMs = null, Func<bool>? mayRead = null, Func<int, PadBattery?>? readBattery = null,
        Func<PadButton, bool>? repeats = null)
    {
        _read = read ?? ReadFromWindows;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _mayRead = mayRead ?? (() => true);
        _readBattery = readBattery ?? (read is null ? ReadBatteryFromWindows : _ => null);
        _repeats = repeats ?? DefaultRepeats;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation14(uint index, byte deviceType, out XInputBatteryInformation info);

    /// <summary>The controller's battery from Windows: wired (1) has no battery; alkaline (2) and rechargeable (3) have one at one of four levels. A disconnected
    /// or unknown battery type, or a Windows without xinput1_4, gives null.</summary>
    private static PadBattery? ReadBatteryFromWindows(int index)
    {
        try
        {
            if (XInputGetBatteryInformation14((uint)index, 0, out var info) != 0)
                return null;

            return info.BatteryType switch
            {
                1 => new PadBattery(true, PadBatteryLevel.Full),
                2 or 3 when info.BatteryLevel <= 3 => new PadBattery(false, (PadBatteryLevel)info.BatteryLevel),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The first connected controller's power, or null when none is connected or it reports none. Checked every few seconds.</summary>
    public PadBattery? Battery { get; private set; }

    /// <summary>Raised when <see cref="Battery"/> changes (a new level, a cable plugged or pulled, a pad found or lost).</summary>
    public event Action<PadBattery?>? BatteryChanged;

    internal const int BatteryRecheckMs = 5000;
    private long _nextBatteryCheck;

    private void RefreshBattery(long now, bool force = false)
    {
        if (!force && now < _nextBatteryCheck)
            return;

        _nextBatteryCheck = now + BatteryRecheckMs;
        PadBattery? current = null;
        for (var controller = 0; controller < MaxControllers; controller++)
        {
            if (!_present[controller])
                continue;

            current = _readBattery(controller);
            break;
        }

        if (current == Battery)
            return;

        Battery = current;
        BatteryChanged?.Invoke(current);
    }

    /// <summary>Raised for each press (and each repeat of a held direction), on the thread that calls <see cref="Poll"/>.</summary>
    public event Action<PadButton>? ButtonPressed;

    /// <summary>The right stick is pushed: how far across and up (-1 to 1, up and right positive, after its dead zone) and how many seconds this push covers. Raised on every reading
    /// while it is held out of the dead zone, so a scroll that follows it is smooth and as fast as the push is hard.</summary>
    public event Action<double, double, double>? RightStick;

    private long _lastStickAt;

    /// <summary>A stick position as a push: 0 inside the dead zone (a quarter of its travel, so a resting stick that drifts does nothing), then 0 to 1 (or -1) over the rest.</summary>
    internal static double StickPush(short value)
    {
        var travel = value / 32767.0;
        var size = Math.Abs(travel);
        return size < 0.25 ? 0 : Math.Sign(travel) * Math.Min(1.0, (size - 0.25) / 0.75);
    }

    /// <summary>Raised when the first controller appears or the last one goes away.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>A controller is plugged in (as of the last reading; not updated while readings are not allowed).</summary>
    public bool IsConnected => _connected;

    /// <summary>Starts polling about thirty times a second on the given dispatcher. Call <see cref="Dispose"/> to stop.</summary>
    public void Start(Dispatcher dispatcher)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Normal, (_, _) => PollSafely(), dispatcher);
        _timer.Start();
    }

    /// <summary>Readings taken, and timer ticks seen (a tick with no reading means readings were not allowed), for the log's heartbeat line.</summary>
    public long PollCount { get; private set; }

    public long TickCount { get; private set; }

    private string? _lastError;

    /// <summary>What the timer calls: a failure in one reading (or in what a press does) is logged and the next tick carries on, rather than ending the pad
    /// for the rest of the session or taking the launcher down with it.</summary>
    private void PollSafely()
    {
        TickCount++;
        try
        {
            Poll();
        }
        catch (Exception ex)
        {
            var key = ex.GetType().Name + ex.Message;
            if (key != _lastError) // the same failure on every tick would flood the log
            {
                _lastError = key;
                Logger.Error("The controller reading or a button press failed; polling continues.", ex);
            }
        }
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        ForgetHeldButtons();
    }

    /// <summary>The buttons down in one reading, with the left stick treated as the D-pad.</summary>
    internal static IReadOnlySet<PadButton> Pressed(PadReading state)
    {
        var down = new HashSet<PadButton>();
        if ((state.Buttons & DpadUp) != 0 || state.ThumbLeftY > StickThreshold) down.Add(PadButton.Up);
        if ((state.Buttons & DpadDown) != 0 || state.ThumbLeftY < -StickThreshold) down.Add(PadButton.Down);
        if ((state.Buttons & DpadLeft) != 0 || state.ThumbLeftX < -StickThreshold) down.Add(PadButton.Left);
        if ((state.Buttons & DpadRight) != 0 || state.ThumbLeftX > StickThreshold) down.Add(PadButton.Right);
        if ((state.Buttons & ButtonA) != 0) down.Add(PadButton.Accept);
        if ((state.Buttons & ButtonB) != 0) down.Add(PadButton.Back);
        if ((state.Buttons & ButtonY) != 0) down.Add(PadButton.Y);
        if ((state.Buttons & ButtonX) != 0) down.Add(PadButton.X);
        if ((state.Buttons & StartButton) != 0) down.Add(PadButton.Start);
        if ((state.Buttons & LeftShoulder) != 0) down.Add(PadButton.LB);
        if ((state.Buttons & RightShoulder) != 0) down.Add(PadButton.RB);
        if (state.LeftTrigger >= TriggerThreshold) down.Add(PadButton.LT);
        if (state.RightTrigger >= TriggerThreshold) down.Add(PadButton.RT);
        return down;
    }

    private bool Repeats(PadButton button) => _repeats(button);

    /// <summary>The D-pad and stick repeat while held, and so do the triggers - but only when they page a long screen. A button given another job (Settings, Optimize...)
    /// must fire once, or holding it would open that job again and again.</summary>
    internal static bool DefaultRepeats(PadButton button) => button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right or PadButton.LT or PadButton.RT;

    /// <summary>Reads every controller once and raises whatever was newly pressed (or is repeating). The timer calls this; tests call it directly.
    /// When readings are not allowed it reads nothing, and a button still down when the launcher comes back in front does not count as a press.</summary>
    public void Poll()
    {
        if (!_mayRead())
        {
            ForgetHeldButtons();
            CheckPresence(); // still say whether a pad is plugged in (the status pill), though no button is read
            return;
        }

        PollCount++;
        var now = _nowMs();
        var stillDown = new HashSet<(int, PadButton)>();
        var fired = new List<PadButton>(); // raised only after the bookkeeping is done: a press can open a dialog that pumps messages and polls again
        double stickX = 0, stickY = 0;
        for (var controller = 0; controller < MaxControllers; controller++)
        {
            // An empty slot is only asked about now and then.
            if (!_present[controller] && now < _nextProbe[controller])
                continue;

            var wasPresent = _present[controller];
            var reading = _read(controller);
            _present[controller] = reading is not null;
            var justAppeared = reading is not null && !wasPresent; // a pad that has just woken up or been plugged in: the press that did it is not a command
            if (reading is not { } state)
            {
                _nextProbe[controller] = now + EmptySlotRecheckMs;
                continue;
            }

            // The right stick, as a smooth push from 0 to 1 once it is out of its dead zone; the strongest pad wins.
            var pushX = StickPush(state.ThumbRightX);
            var pushY = StickPush(state.ThumbRightY);
            if (Math.Abs(pushX) > Math.Abs(stickX)) stickX = pushX;
            if (Math.Abs(pushY) > Math.Abs(stickY)) stickY = pushY;

            foreach (var button in Pressed(state))
            {
                var key = (controller, button);
                stillDown.Add(key);

                if (!_heldSince.ContainsKey(key))
                {
                    if (_resuming || justAppeared)
                    {
                        // Already down when reading resumed or the pad appeared (the player pressed it in a game, or woke the pad with it): not a press, and no repeats, until it is let go.
                        _heldSince[key] = long.MaxValue;
                        _lastFired[key] = long.MaxValue;
                    }
                    else
                    {
                        _heldSince[key] = now;
                        _lastFired[key] = now;
                        fired.Add(button);
                    }
                }
                else if (Repeats(button) && now - _heldSince[key] >= RepeatDelayMs && now - _lastFired[key] >= RepeatIntervalMs)
                {
                    _lastFired[key] = now;
                    fired.Add(button);
                }
            }
        }

        _resuming = false;

        // Let go: forget it, so the next press counts as new.
        foreach (var key in _heldSince.Keys.Where(k => !stillDown.Contains(k)).ToList())
        {
            _heldSince.Remove(key);
            _lastFired.Remove(key);
        }

        UpdateConnection();

        foreach (var button in fired)
            ButtonPressed?.Invoke(button);

        if (stickX != 0 || stickY != 0)
        {
            var seconds = _lastStickAt == 0 ? 0.033 : Math.Min(0.1, (now - _lastStickAt) / 1000.0);
            _lastStickAt = now;
            RightStick?.Invoke(stickX, stickY, seconds);
        }
        else
        {
            _lastStickAt = 0;
        }
    }

    private long _nextPresenceCheck;

    /// <summary>Which pads are plugged in, once a second, without looking at a single button. Used while buttons are not being read (another window or a game
    /// in front, or a theme with no controller mode), so the launcher can still show whether a controller is connected.</summary>
    private void CheckPresence()
    {
        var now = _nowMs();
        if (now < _nextPresenceCheck)
            return;

        _nextPresenceCheck = now + EmptySlotRecheckMs;
        for (var controller = 0; controller < MaxControllers; controller++)
        {
            _present[controller] = _read(controller) is not null;
            _nextProbe[controller] = now + EmptySlotRecheckMs;
        }

        UpdateConnection();
    }

    private void UpdateConnection()
    {
        var connected = _present.Any(p => p);
        var changed = connected != _connected;
        _connected = connected;
        RefreshBattery(_nowMs(), force: changed);
        if (changed)
            ConnectionChanged?.Invoke(connected);
    }

    private void ForgetHeldButtons()
    {
        _resuming = true;
        _heldSince.Clear();
        _lastFired.Clear();
    }

    public void Dispose() => Stop();
}
