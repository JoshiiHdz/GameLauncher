using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLauncher.Services.SessionTracking;

/// <summary>
/// Production IGameProcess: wraps a real System.Diagnostics.Process. The P/Invoke calls here are
/// moved verbatim from the original, single-file GameSessionWatcher - see each method for the
/// reasoning behind using QueryFullProcessImageName/GetProcessTimes over Process's own MainModule.
/// </summary>
internal sealed class Win32GameProcess : IGameProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly Process _process;

    public Win32GameProcess(Process process) => _process = process;

    public int Id => _process.Id;

    public string ProcessName
    {
        get
        {
            try
            {
                return _process.ProcessName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// Process.MainModule.FileName requests PROCESS_QUERY_INFORMATION + PROCESS_VM_READ under the
    /// hood (it has to walk the module list, not just read one string), which anti-cheat-protected
    /// and elevated processes routinely deny even to an admin-equivalent caller. QueryFullProcessImageName
    /// only needs PROCESS_QUERY_LIMITED_INFORMATION, the access level Windows specifically carves out
    /// for "let any caller see this process's own image path without touching anything else" - it
    /// succeeds against far more protected processes than MainModule does.
    /// </summary>
    public string? GetPath()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, _process.Id);
            if (handle == IntPtr.Zero)
                return null;

            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Already-exited process, or some other access denial even at this minimal level.
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    /// <summary>Uses the same PROCESS_QUERY_LIMITED_INFORMATION handle as GetPath (GetProcessTimes
    /// needs no more than that), so this succeeds in every case the path lookup does.</summary>
    public DateTimeOffset? GetStartTimeUtc()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, _process.Id);
            if (handle == IntPtr.Zero)
                return null;

            return GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? DateTime.FromFileTimeUtc(creation)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    public void PrepareForExitWait()
    {
        try
        {
            _process.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
        }
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public void Dispose() => _process.Dispose();
}
