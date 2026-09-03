using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Works out when a launched game has actually exited.
///
/// The process we start is often not the game: Steam titles go through steam:// so we never get a
/// handle at all, and plenty of games run a bootstrapper that exits the instant it spawns the real
/// executable. So instead of trusting the process we started, this looks for processes running from
/// inside the game's install folder, then waits for all of them to exit.
///
/// Discovery polls briefly after launch (games take a while to come up); once the processes are
/// found it switches to waiting on handles, so there's no ongoing polling while you play.
/// </summary>
public sealed class GameSessionWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // Generous on purpose: real logs showed anti-cheat-heavy titles taking well over the old
    // 2-minute cap to spawn their real process, and this only exists as an outer sanity bound, not
    // the normal path - every confirmed-working launch in those same logs discovered its process
    // within 15 seconds. Past this, a launch that's still found nothing has most likely failed
    // outright (crashed, blocked on a prompt, cancelled) rather than still being "slow," so it's
    // treated the same as a real exit - restore the window rather than leave it stuck hidden for a
    // launch that's never coming back, the way the old un-timed-out version briefly was before this.
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(10);

    // How long to keep looking for a handoff process after the watched one(s) exit before believing
    // the game is actually closed. Found from real logs: gamelaunchhelper.exe (Xbox) and an EA
    // trial-launcher stub both exit within half a second of starting, well before the real game
    // process exists yet, which made the launcher pop back out of the tray almost instantly.
    // Widened from 20s to 45s after a report of the launcher reappearing exactly when a game's
    // splash screen closed - large modern titles (Call of Duty was the one reported) can take longer
    // than 20s between the loader exiting and the real game window/process being fully up.
    private static readonly TimeSpan HandoffGracePeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HandoffPollInterval = TimeSpan.FromSeconds(1);

    // Widened from 3 to 6 after the same report: if a game's real executable lives deeper in its
    // install folder than this scan goes (common for titles that split content into many nested
    // package/DLC folders), its name never makes it into candidateNames at all - so the handoff
    // check above can never find it running, no matter how long it waits, and restores the window
    // over a game that's actually still open.
    private const int MaxExeScanDepth = 6;

    /// <summary>
    /// Returns once the game appears to have exited, or once it's given up waiting for the game to
    /// even start. Returns false only when ct is cancelled (a newer launch superseded this one, or
    /// the app is closing) - the caller should treat false as "leave the window alone," since
    /// something else now owns the state. Every other outcome, including a discovery timeout, returns
    /// true - both mean "this launch is over, safe to restore the window."
    /// </summary>
    public async Task<bool> WaitForExitAsync(GameEntry game, Process? launched, CancellationToken ct = default)
    {
        var candidateNames = GetCandidateProcessNames(game);
        Logger.Info($"'{game.Name}': watching for {candidateNames.Count} candidate exe name(s) "
            + $"under '{game.InstallDir}': {string.Join(", ", candidateNames)}");

        if (candidateNames.Count == 0)
        {
            Logger.Warn($"No executables found under '{game.InstallDir}' - can't watch '{game.Name}' for exit.");
            return false;
        }

        var discoveryDeadline = DateTime.UtcNow + DiscoveryTimeout;
        List<Process> running = [];

        while (DateTime.UtcNow < discoveryDeadline && !ct.IsCancellationRequested)
        {
            running = FindRunning(game, candidateNames, launched);
            if (running.Count > 0)
                break;

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (running.Count == 0)
        {
            if (ct.IsCancellationRequested)
            {
                Logger.Info($"Stopped watching for '{game.Name}' to start (superseded or shutting down).");
                return false;
            }

            Logger.Warn($"Never saw a running process for '{game.Name}' within "
                + $"{DiscoveryTimeout.TotalMinutes:0} minutes - assuming the launch failed and restoring the window.");
            return true;
        }

        Logger.Info($"Watching {running.Count} process(es) for '{game.Name}': "
            + string.Join(", ", running.Select(p => $"{SafeGetProcessName(p)} (pid {p.Id}) <- {SafeGetPath(p) ?? "path unknown"}")));

        while (!ct.IsCancellationRequested)
        {
            foreach (var process in running)
            {
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                {
                    // Process already gone or inaccessible - treat as exited.
                }
                finally
                {
                    process.Dispose();
                }
            }

            // A launcher process commonly hands off to the real game and exits well before it's up,
            // so don't trust a single immediate recheck - keep looking for a replacement for a while.
            var replacement = await WaitForHandoffAsync(game, candidateNames, ct);
            if (replacement.Count == 0)
            {
                Logger.Info($"'{game.Name}' exited - no replacement process found under "
                    + $"'{game.InstallDir}' within the {HandoffGracePeriod.TotalSeconds:0}s handoff window.");
                return true;
            }

            Logger.Info($"'{game.Name}' handed off to {replacement.Count} new process(es), still watching: "
                + string.Join(", ", replacement.Select(p => $"{SafeGetProcessName(p)} (pid {p.Id}) <- {SafeGetPath(p) ?? "path unknown"}")));
            running = replacement;
        }

        return false;
    }

    private static async Task<List<Process>> WaitForHandoffAsync(
        GameEntry game, HashSet<string> candidateNames, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HandoffGracePeriod;

        while (true)
        {
            // Re-scan for candidate names too, not just running processes, on every tick: some games
            // only write/extract their real binary partway through the handoff, so a name absent at
            // the start of this wait can still appear at any point before the deadline. A first
            // attempt refreshed this only once at entry and missed a handoff file created a few
            // seconds later - confirmed live with a stub that spawns its "real" process after a delay.
            if (GetCandidateProcessNames(game) is { Count: > 0 } refreshed)
                candidateNames = refreshed;

            var found = FindRunning(game, candidateNames, launched: null);
            if (found.Count > 0 || DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
                return found;

            try
            {
                await Task.Delay(HandoffPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }
    }

    private static List<Process> FindRunning(GameEntry game, HashSet<string> candidateNames, Process? launched)
    {
        var found = new List<Process>();

        // Unlike the name-matched loop below, `launched` isn't scoped to this game's own exe names -
        // it's whatever process Process.Start happened to return (for Steam, explicitly documented as
        // "the URI handler rather than the game," i.e. often unrelated) - so this one keeps the
        // strict path check rather than trusting a blocked MainModule read.
        if (launched is not null && IsUnder(SafeGetPath(launched), game.InstallDir))
            found.Add(launched);

        foreach (var name in candidateNames)
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var process in matches)
            {
                if (found.Any(p => p.Id == process.Id) || !IsRunningThisGame(process, game.InstallDir))
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    process.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                {
                }

                found.Add(process);
            }
        }

        return found;
    }

    /// <summary>Executable names inside the install dir, used to cheaply shortlist processes by
    /// name before doing the more expensive path check on just those. Generic installer/crash-
    /// reporter noise (GameExeFinder.NoiseExcludePatterns) is filtered out here too: without this, a
    /// name match against something like UnityCrashHandler64.exe or a redist installer could trip the
    /// anti-cheat path-unreadable fallback below and make an unrelated crash/installer process look
    /// like "the game is still running." Deliberately keeps "trial"/"anticheat"-named exes as valid
    /// candidates unlike the exe-picker - those can be genuine handoff targets to watch for.</summary>
    private static HashSet<string> GetCandidateProcessNames(GameEntry game)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(game.InstallDir, 0);
        return names;

        void Collect(string dir, int depth)
        {
            if (depth > MaxExeScanDepth)
                return;

            try
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    var name = Path.GetFileNameWithoutExtension(exe);
                    if (!GameExeFinder.NoiseExcludePatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        names.Add(name);
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Collect(sub, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            return "unknown";
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Process.MainModule.FileName requests PROCESS_QUERY_INFORMATION + PROCESS_VM_READ under the
    /// hood (it has to walk the module list, not just read one string), which anti-cheat-protected
    /// and elevated processes routinely deny even to an admin-equivalent caller - that's exactly the
    /// gap IsRunningThisGame's name-only trust fallback exists for. QueryFullProcessImageName only
    /// needs PROCESS_QUERY_LIMITED_INFORMATION, the access level Windows specifically carves out for
    /// "let any caller see this process's own image path without touching anything else" - it
    /// succeeds against far more protected processes than MainModule does, which means the strict
    /// path-verified branch now covers more cases and the "trust the name alone" fallback (a wider,
    /// if still narrow, false-positive surface) is needed less often.
    /// </summary>
    private static string? SafeGetPath(Process process)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
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

    private static bool IsUnder(string? path, string directory)
        => path is not null
           && path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Real log from a Fortnite session: MainModule access was refused for every candidate process
    /// (EasyAntiCheat-protected processes commonly block it), so IsUnder(null, ...) failed every
    /// single one and the watcher never found the game at all - "Never saw a running process", even
    /// though it was running the whole time. When the path genuinely can't be read, this trusts the
    /// name match alone instead of silently dropping the process: candidateNames only ever contains
    /// exe names actually found inside this specific game's own install folder, so an unrelated
    /// process happening to share one of those exact names is effectively impossible. When the path
    /// *can* be read, the strict install-dir check still applies unchanged.
    /// </summary>
    private static bool IsRunningThisGame(Process process, string installDir)
    {
        var path = SafeGetPath(process);
        return path is null || IsUnder(path, installDir);
    }
}
