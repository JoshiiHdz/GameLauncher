using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameLauncher.Services;

/// <summary>
/// Releases the process working set when the launcher is minimized (which is what happens the
/// moment a game starts). Windows doesn't do this on its own here - measured identical working
/// sets before and after minimizing - so a launcher sitting idle behind a game was holding on to
/// hundreds of MB of physical RAM.
///
/// This trims resident pages, not committed memory: the pages move out of RAM and fault back in
/// when the window is restored. That's the right trade for an app that does nothing while
/// minimized, and it hands the physical RAM back to the game.
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);

    public static void Trim(string reason)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = process.WorkingSet64;

            // -1 for both sizes tells Windows to empty the working set.
            SetProcessWorkingSetSize(process.Handle, -1, -1);

            process.Refresh();
            Logger.Info($"Memory trim ({reason}): {before / 1024 / 1024} MB -> {process.WorkingSet64 / 1024 / 1024} MB working set.");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException
                                        or InvalidOperationException)
        {
            Logger.Warn("Couldn't trim the working set.", ex);
        }
    }
}
