using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GameLauncher.Services;

/// <summary>One system-wide keyboard shortcut, registered with Windows for a window. Works while the launcher is hidden in the tray or a
/// game has focus - that is the point. It registers a single combination, never watches other keys, and gives it back when disposed.
/// Registration can fail (another program already owns the combination); that is reported by the return value, never thrown.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private static int _nextId = 0x4158; // "AX", one id per registration within this process

    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    private GlobalHotkeyService(HwndSource source, int id)
    {
        _source = source;
        _id = id;
        source.AddHook(OnMessage);
    }

    /// <summary>The registration id Windows reports in WM_HOTKEY - exposed so a test can deliver that message itself.</summary>
    internal int Id => _id;

    /// <summary>Raised on the window's thread each time the combination is pressed.</summary>
    public event Action? Pressed;

    /// <summary>The Win32 modifier bits for a set of WPF modifiers; NoRepeat is always added so holding the keys down fires once.</summary>
    internal static uint ModifierBits(ModifierKeys modifiers) =>
        ModNoRepeat
        | (modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (modifiers.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    /// <summary>Registers the combination for <paramref name="window"/> (which must already have a handle). Null when Windows refuses it,
    /// most often because another program has it.</summary>
    public static GlobalHotkeyService? TryRegister(Window window, Key key, ModifierKeys modifiers)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle);
        if (source is null)
            return null;

        var id = Interlocked.Increment(ref _nextId);
        if (!RegisterHotKey(handle, id, ModifierBits(modifiers), (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            Logger.Warn($"Global hotkey {modifiers}+{key} could not be registered (error {Marshal.GetLastWin32Error()}) - another program probably has it.");
            return null;
        }

        var service = new GlobalHotkeyService(source, id) { _registered = true };
        Logger.Info($"Global hotkey {modifiers}+{key} registered.");
        return service;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        _registered = false;
        _source.RemoveHook(OnMessage);
        UnregisterHotKey(_source.Handle, _id);
    }
}
