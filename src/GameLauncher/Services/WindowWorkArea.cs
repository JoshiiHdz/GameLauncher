using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameLauncher.Services;

/// <summary>Keeps a normal (not maximized) window inside the usable part of the screen the window is on - the monitor minus the taskbar. A window
/// restored from maximized, or reopened where it was left on another monitor or resolution, can otherwise end up with its bottom under the taskbar.</summary>
public static class WindowWorkArea
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const int MonitorDefaultToNearest = 2;

    /// <summary>The work area of the monitor the window is on, in the window's device-independent units; null when it cannot be found.</summary>
    public static Rect? Of(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info))
            return null;

        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }

    /// <summary>The whole monitor the window is on (taskbar included), in the window's device-independent units (what
    /// <see cref="ControllerScale"/> goes by); null when it cannot be found.</summary>
    public static Rect? MonitorOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info))
            return null;

        var dpi = VisualTreeHelper.GetDpi(window);
        var bounds = new Rect(info.Monitor.Left / dpi.DpiScaleX, info.Monitor.Top / dpi.DpiScaleY,
            (info.Monitor.Right - info.Monitor.Left) / dpi.DpiScaleX, (info.Monitor.Bottom - info.Monitor.Top) / dpi.DpiScaleY);
        return bounds;
    }

    /// <summary>Shrinks and moves a normal window so all of it is inside the work area. Does nothing for a maximized or minimized window.</summary>
    public static void Clamp(Window window)
    {
        if (window.WindowState != WindowState.Normal || Of(window) is not { } work)
            return;

        var width = Math.Min(window.Width, work.Width);
        var height = Math.Min(window.Height, work.Height);
        var left = Math.Clamp(window.Left, work.Left, work.Right - width);
        var top = Math.Clamp(window.Top, work.Top, work.Bottom - height);
        if (width != window.Width) window.Width = width;
        if (height != window.Height) window.Height = height;
        if (left != window.Left) window.Left = left;
        if (top != window.Top) window.Top = top;
    }
}
