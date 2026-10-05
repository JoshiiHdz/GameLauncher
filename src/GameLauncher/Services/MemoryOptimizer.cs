using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GameLauncher.Models;

namespace GameLauncher.Services;

public readonly record struct MemoryStatus(long TotalBytes, long AvailableBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

    /// <summary>0-1, for a progress bar.</summary>
    public double UsedFraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / TotalBytes, 0, 1);

    public string Describe() => TotalBytes <= 0
        ? "Memory details are unavailable right now."
        : $"{ByteFormat.Size(UsedBytes)} in use of {ByteFormat.Size(TotalBytes)} ({UsedFraction:P0}), {ByteFormat.Size(AvailableBytes)} available";
}

public sealed record MemoryTrimResult(int ProcessesTrimmed, int ProcessesSkipped, long AvailableBefore, long AvailableAfter)
{
    public long FreedBytes => Math.Max(0, AvailableAfter - AvailableBefore);

    public string Describe() => ProcessesTrimmed == 0
        ? "Nothing could be trimmed."
        : $"Made about {ByteFormat.Size(FreedBytes)} available by trimming {ProcessesTrimmed} apps' memory. Windows hands it back to an app the moment it needs it.";
}

/// <summary>Frees RAM the documented way: asking Windows to trim other apps' working sets (the pages they are holding but not using right
/// now), the same call Task Manager-style cleaners use. It is not a "boost" - Windows gives pages back as soon as an app touches them -
/// but it does put the memory in the available pool right before something big, like a game, starts. Never touches anything Windows
/// would not allow a normal user to, never closes an app, and leaves the foreground app, this launcher and any protected folder's
/// programs (the game being played) alone.</summary>
public interface IMemoryOptimizer
{
    MemoryStatus GetStatus();

    /// <param name="protectedFolders">Programs running from inside these folders are skipped (the running game's install folder).</param>
    MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders);
}

public sealed class MemoryOptimizer : IMemoryOptimizer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public MemoryStatus GetStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? new MemoryStatus((long)status.TotalPhys, (long)status.AvailPhys) : default;
    }

    /// <summary>True when <paramref name="path"/> is one of the folders or inside one - compared as whole path segments, so
    /// "C:\Games\Foo" protects "C:\Games\Foo\bin\x.exe" but not "C:\Games\FooBar\x.exe".</summary>
    internal static bool IsInside(string? path, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public MemoryTrimResult Trim(IReadOnlyList<string> protectedFolders)
    {
        var before = GetStatus().AvailableBytes;
        int trimmed = 0, skipped = 0;

        int ownId = Environment.ProcessId, ownSession;
        using (var self = Process.GetCurrentProcess())
            ownSession = self.SessionId;

        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundId);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // Only this user's own programs: services and other sessions are not ours to touch.
                    if (process.Id == ownId || process.Id == foregroundId || process.Id <= 4 || process.SessionId != ownSession)
                        continue;

                    var handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, process.Id);
                    if (handle == IntPtr.Zero)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (IsInside(ImagePath(handle), protectedFolders))
                        {
                            skipped++;
                            continue;
                        }

                        if (EmptyWorkingSet(handle))
                            trimmed++;
                        else
                            skipped++;
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    skipped++; // exited while being looked at, or off limits
                }
            }
        }

        var after = GetStatus().AvailableBytes;
        Logger.Info($"Memory optimize: trimmed {trimmed} processes (skipped {skipped}); available {ByteFormat.Size(before)} -> {ByteFormat.Size(after)}.");
        return new MemoryTrimResult(trimmed, skipped, before, after);
    }

    private static string? ImagePath(IntPtr handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }
}
