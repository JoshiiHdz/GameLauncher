using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameLauncher.Services;

/// <summary>A short memory of what the launcher has just done, so that when something odd shows up (the busy cursor) the log can say what was going on a moment before.
/// Anything that starts a process, probes hardware or does other work off the UI thread notes itself here. Cheap and thread-safe; keeps the last couple of hundred.</summary>
public static class Trail
{
    private const int Capacity = 200;
    private static readonly object Gate = new();
    private static readonly Queue<(long At, string What)> Entries = new();

    /// <summary>Notes something that just happened ("controller probe: slot 2 empty in 3 ms").</summary>
    public static void Note(string what)
    {
        lock (Gate)
        {
            Entries.Enqueue((Stopwatch.GetTimestamp(), what));
            while (Entries.Count > Capacity)
                Entries.Dequeue();
        }
    }

    /// <summary>What was noted within the last <paramref name="within"/>, oldest first, as "-1.2 s  what" lines.</summary>
    public static IReadOnlyList<string> Recent(TimeSpan within, int max = 14)
    {
        var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            var lines = Entries.Where(e => Stopwatch.GetElapsedTime(e.At, now) <= within)
                .Select(e => $"-{Stopwatch.GetElapsedTime(e.At, now).TotalSeconds:0.00}s {e.What}").ToList();
            return lines.Count <= max ? lines : lines.Skip(lines.Count - max).ToList();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }
}

/// <summary>Turns a stream of "is the busy cursor showing?" samples into episodes: when one began, when it ended and how long it lasted. Pure, so it can be tested.</summary>
public sealed class BusyCursorTracker
{
    private DateTime? _since;

    /// <summary>How many episodes have begun.</summary>
    public int Episodes { get; private set; }

    /// <summary>The length of the episode that has just ended, or null if none ended on the last sample.</summary>
    public TimeSpan? JustEnded { get; private set; }

    /// <summary>True when the last sample was the first of a new episode.</summary>
    public bool JustStarted { get; private set; }

    public bool IsBusy => _since is not null;

    public DateTime? Since => _since;

    public void Observe(bool busy, DateTime now)
    {
        JustStarted = false;
        JustEnded = null;
        if (busy && _since is null)
        {
            _since = now;
            Episodes++;
            JustStarted = true;
        }
        else if (!busy && _since is { } started)
        {
            JustEnded = now - started;
            _since = null;
        }
    }
}

/// <summary>Finds out why the busy cursor (the little loading circle) shows over the launcher. It watches, off the UI thread and a few times a second while the window is in
/// view: whether Windows is showing the wait or "working in background" pointer and over what; which processes the launcher has started; and whether the UI thread
/// answers promptly. When the busy pointer appears it writes one log line with what the launcher did just before, then a line when it goes - and a summary every minute,
/// so a pointer that flickers every second is one readable story, not a thousand lines. It only reads; it changes nothing.</summary>
public sealed class BusyCursorWatch : IDisposable
{
    private const int SampleMs = 250;
    private const int DetailedEpisodes = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private const uint SnapProcess = 0x2;
    private static readonly IntPtr IdcWait = (IntPtr)32514, IdcAppStarting = (IntPtr)32650;

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _windowInView;
    private readonly Func<string> _describeUiState;
    private readonly BusyCursorTracker _tracker = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<uint> _knownChildren = new();
    private readonly int _ownPid = Environment.ProcessId;

    private IntPtr _waitCursor, _startingCursor;
    private long _pingSentAt;          // Stopwatch ticks; 0 when none is waiting
    private long _worstLatencyTicks;   // worst answer in the current minute
    private int _episodesThisMinute;
    private int _slowUiThisMinute;
    private int _tick;
    private DateTime _lastSummary = DateTime.UtcNow;
    private DateTime _lastBusyLine = DateTime.MinValue;
    private string _wpfCursor = "";

    /// <param name="windowInView">True while the launcher's window is shown (not minimized, not in the tray): the watch sleeps otherwise.</param>
    /// <param name="describeUiState">Read on the UI thread when the watch pings it: what WPF thinks the pointer is (an override, or the element's cursor).</param>
    public BusyCursorWatch(Dispatcher dispatcher, Func<bool> windowInView, Func<string> describeUiState)
    {
        _dispatcher = dispatcher;
        _windowInView = windowInView;
        _describeUiState = describeUiState;
        _thread = new Thread(Run) { IsBackground = true, Name = "Busy cursor watch", Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        try
        {
            _waitCursor = LoadCursorW(IntPtr.Zero, IdcWait);
            _startingCursor = LoadCursorW(IntPtr.Zero, IdcAppStarting);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn("Busy cursor watch could not start.", ex);
            return;
        }

        Logger.Info("Busy cursor watch is on: it writes a line here when Windows shows the loading circle over the launcher, with what the launcher did just before.");
        _thread.Start();
    }

    private void Run()
    {
        var token = _stop.Token;
        while (!token.WaitHandle.WaitOne(SampleMs))
        {
            try
            {
                if (!_windowInView())
                {
                    _tracker.Observe(false, DateTime.UtcNow);
                    continue;
                }

                if (_tick++ % 4 == 0)
                    SampleChildren(); // a snapshot of every process is the dearest thing here: once a second is plenty to see a helper come and go

                Ping();
                SampleCursor();
                Summarise();
            }
            catch (Exception ex)
            {
                Logger.Warn("Busy cursor watch: a sample failed; it keeps going.", ex);
            }
        }
    }

    /// <summary>Asks the UI thread to answer; a slow answer means it was busy (a layout, a synchronous read, a dialog being built).</summary>
    private void Ping()
    {
        var now = Stopwatch.GetTimestamp();
        var waiting = Interlocked.Read(ref _pingSentAt);
        if (waiting != 0)
        {
            var late = Stopwatch.GetElapsedTime(waiting, now);
            if (late.TotalMilliseconds >= 500)
                Trail.Note($"UI thread has not answered for {late.TotalMilliseconds:0} ms");

            return;
        }

        Interlocked.Exchange(ref _pingSentAt, now);
        _dispatcher.BeginInvoke(() =>
        {
            var sent = Interlocked.Exchange(ref _pingSentAt, 0);
            var answered = Stopwatch.GetTimestamp();
            var ticks = answered - sent;
            if (ticks > Interlocked.Read(ref _worstLatencyTicks))
                Interlocked.Exchange(ref _worstLatencyTicks, ticks);

            var ms = Stopwatch.GetElapsedTime(sent, answered).TotalMilliseconds;
            if (ms >= 250)
            {
                Interlocked.Increment(ref _slowUiThisMinute);
                Trail.Note($"UI thread was busy for {ms:0} ms");
            }

            try { _wpfCursor = _describeUiState(); }
            catch (Exception) { _wpfCursor = "?"; }
        }, DispatcherPriority.Normal);
    }

    private void SampleCursor()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
            return;

        var busy = (info.Flags & 1) != 0 && info.Cursor != IntPtr.Zero && (info.Cursor == _waitCursor || info.Cursor == _startingCursor);
        var kind = info.Cursor == _startingCursor ? "working in background" : "wait";
        var now = DateTime.UtcNow;
        _tracker.Observe(busy, now);

        if (_tracker.JustStarted)
        {
            _episodesThisMinute++;
            var over = DescribeWindowUnderCursor();
            Trail.Note($"busy cursor ({kind}) {over}");
            if (_tracker.Episodes <= DetailedEpisodes || now - _lastBusyLine > TimeSpan.FromSeconds(30))
            {
                _lastBusyLine = now;
                var recent = Trail.Recent(TimeSpan.FromSeconds(3));
                Logger.Warn($"Busy cursor appeared ({kind}) {over}. Episode {_tracker.Episodes}. WPF pointer: {(_wpfCursor.Length == 0 ? "normal" : _wpfCursor)}. "
                    + $"Launcher's child processes now: {DescribeChildren()}. Just before: {(recent.Count == 0 ? "nothing noted" : string.Join(" | ", recent))}");
            }
        }
        else if (_tracker.JustEnded is { } length && (_tracker.Episodes <= DetailedEpisodes || now - _lastBusyLine < TimeSpan.FromSeconds(1)))
        {
            Logger.Info($"Busy cursor gone after {length.TotalMilliseconds:0} ms.");
        }
    }

    private string DescribeWindowUnderCursor()
    {
        try
        {
            if (!GetCursorPos(out var point))
                return "(over an unknown window)";

            var window = WindowFromPoint(point);
            GetWindowThreadProcessId(window, out var pid);
            if (pid == _ownPid)
                return "over the launcher's own window";

            using var process = Process.GetProcessById((int)pid);
            return $"over another program's window ({process.ProcessName})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "(over another window)";
        }
    }

    // ---- The launcher's own child processes, from a process snapshot (cheap, and starts nothing) ---------------------------------

    private readonly Dictionary<uint, string> _childNames = new();

    private void SampleChildren()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
            return;

        try
        {
            var seen = new HashSet<uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.ParentProcessId != _ownPid)
                    continue;

                seen.Add(entry.ProcessId);
                if (_knownChildren.Add(entry.ProcessId))
                {
                    _childNames[entry.ProcessId] = entry.ExeFile;
                    Trail.Note($"child process started: {entry.ExeFile} (pid {entry.ProcessId})");
                }
            }

            foreach (var gone in _knownChildren.Where(p => !seen.Contains(p)).ToList())
            {
                _knownChildren.Remove(gone);
                Trail.Note($"child process ended: {_childNames.GetValueOrDefault(gone, "?")} (pid {gone})");
                _childNames.Remove(gone);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private string DescribeChildren() =>
        _knownChildren.Count == 0 ? "none" : string.Join(", ", _knownChildren.Select(p => $"{_childNames.GetValueOrDefault(p, "?")} ({p})"));

    // ---- A line a minute, only when there is something to say -----------------------------------------------------------------

    private void Summarise()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSummary < TimeSpan.FromMinutes(1))
            return;

        _lastSummary = now;
        var worst = Stopwatch.GetElapsedTime(0, Interlocked.Exchange(ref _worstLatencyTicks, 0)).TotalMilliseconds;
        var episodes = Interlocked.Exchange(ref _episodesThisMinute, 0);
        var slow = Interlocked.Exchange(ref _slowUiThisMinute, 0);
        if (episodes == 0 && slow == 0)
            return;

        using var self = Process.GetCurrentProcess();
        Logger.Info($"Busy cursor watch, last minute: {episodes} busy-pointer episode(s), {slow} time(s) the UI thread took 250 ms or more (slowest {worst:0} ms). "
            + $"{self.Threads.Count} threads, {self.HandleCount} handles, {self.WorkingSet64 / (1024 * 1024)} MB. Total episodes this run: {_tracker.Episodes}.");
    }

    public void Dispose()
    {
        _stop.Cancel();
    }
}
