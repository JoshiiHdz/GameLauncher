using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameLauncher.Services;

public enum GamepadButton
{
    Up,
    Down,
    Left,
    Right,
    /// <summary>A - choose.</summary>
    Accept,
    /// <summary>B - back out.</summary>
    Back,
    /// <summary>Y - favorite.</summary>
    Favorite,
    /// <summary>LB - the shelf above.</summary>
    PreviousShelf,
    /// <summary>RB - the shelf below.</summary>
    NextShelf,
}

/// <summary>One reading of a controller, in the shape XInput reports it.</summary>
public readonly record struct GamepadState(ushort Buttons, short ThumbLeftX, short ThumbLeftY);

/// <summary>Turns controller readings into button presses for the big-screen mode. Reads Xbox-style controllers through XInput (Windows' own
/// API; the same one every PC game uses), only while it has been started - nothing polls in the background while the launcher is idle or a
/// game is running. Directions repeat while held, the way a keyboard does; everything else fires once per press.</summary>
public sealed class GamepadService : IDisposable
{
    private const ushort DpadUp = 0x1, DpadDown = 0x2, DpadLeft = 0x4, DpadRight = 0x8;
    private const ushort LeftShoulder = 0x100, RightShoulder = 0x200, ButtonA = 0x1000, ButtonB = 0x2000, ButtonY = 0x8000;
    private const short StickThreshold = 16000; // about half of the stick's travel
    internal const int RepeatDelayMs = 400, RepeatIntervalMs = 120;
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
    private static GamepadState? ReadFromWindows(int index)
    {
        try
        {
            XInputState state;
            var result = _useLegacyDll ? XInputGetState910((uint)index, out state) : XInputGetState14((uint)index, out state);
            return result == 0 ? new GamepadState(state.Buttons, state.ThumbLX, state.ThumbLY) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (_useLegacyDll)
                return null;

            _useLegacyDll = true; // an older Windows: fall back to the first XInput that shipped
            return ReadFromWindows(index);
        }
    }

    private readonly Func<int, GamepadState?> _read;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<(int Controller, GamepadButton Button), long> _heldSince = new();
    private readonly Dictionary<(int Controller, GamepadButton Button), long> _lastFired = new();
    private DispatcherTimer? _timer;

    /// <param name="read">Reads controller N, or null when absent. Windows' XInput unless a test supplies another.</param>
    /// <param name="nowMs">A millisecond clock for the key-repeat timing.</param>
    public GamepadService(Func<int, GamepadState?>? read = null, Func<long>? nowMs = null)
    {
        _read = read ?? ReadFromWindows;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Raised for each press (and each repeat of a held direction), on the thread that calls <see cref="Poll"/>.</summary>
    public event Action<GamepadButton>? ButtonPressed;

    /// <summary>Starts polling about thirty times a second on the given dispatcher. Call <see cref="Dispose"/> to stop.</summary>
    public void Start(Dispatcher dispatcher)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, (_, _) => Poll(), dispatcher);
        _timer.Start();
    }

    /// <summary>The buttons down in one reading, with the stick treated as the D-pad.</summary>
    internal static IReadOnlySet<GamepadButton> Pressed(GamepadState state)
    {
        var down = new HashSet<GamepadButton>();
        if ((state.Buttons & DpadUp) != 0 || state.ThumbLeftY > StickThreshold) down.Add(GamepadButton.Up);
        if ((state.Buttons & DpadDown) != 0 || state.ThumbLeftY < -StickThreshold) down.Add(GamepadButton.Down);
        if ((state.Buttons & DpadLeft) != 0 || state.ThumbLeftX < -StickThreshold) down.Add(GamepadButton.Left);
        if ((state.Buttons & DpadRight) != 0 || state.ThumbLeftX > StickThreshold) down.Add(GamepadButton.Right);
        if ((state.Buttons & ButtonA) != 0) down.Add(GamepadButton.Accept);
        if ((state.Buttons & ButtonB) != 0) down.Add(GamepadButton.Back);
        if ((state.Buttons & ButtonY) != 0) down.Add(GamepadButton.Favorite);
        if ((state.Buttons & LeftShoulder) != 0) down.Add(GamepadButton.PreviousShelf);
        if ((state.Buttons & RightShoulder) != 0) down.Add(GamepadButton.NextShelf);
        return down;
    }

    private static bool Repeats(GamepadButton button) => button is GamepadButton.Up or GamepadButton.Down or GamepadButton.Left or GamepadButton.Right;

    /// <summary>Reads every controller once and raises whatever was newly pressed (or is repeating). The timer calls this; tests call it directly.</summary>
    public void Poll()
    {
        var now = _nowMs();
        var stillDown = new HashSet<(int, GamepadButton)>();
        for (var controller = 0; controller < MaxControllers; controller++)
        {
            if (_read(controller) is not { } state)
                continue;

            foreach (var button in Pressed(state))
            {
                var key = (controller, button);
                stillDown.Add(key);

                if (!_heldSince.ContainsKey(key))
                {
                    _heldSince[key] = now;
                    _lastFired[key] = now;
                    ButtonPressed?.Invoke(button);
                }
                else if (Repeats(button) && now - _heldSince[key] >= RepeatDelayMs && now - _lastFired[key] >= RepeatIntervalMs)
                {
                    _lastFired[key] = now;
                    ButtonPressed?.Invoke(button);
                }
            }
        }

        // Let go: forget it, so the next press counts as new.
        foreach (var key in _heldSince.Keys.Where(k => !stillDown.Contains(k)).ToList())
        {
            _heldSince.Remove(key);
            _lastFired.Remove(key);
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}
