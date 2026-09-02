using System.Diagnostics;
using System.IO;
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
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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
    /// Returns once the game appears to have exited. Returns false if the game's processes were
    /// never found - in that case the caller should leave the window alone rather than popping it
    /// over a game that is probably still running.
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

        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        List<Process> running = [];

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
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
            Logger.Info($"Never saw a running process for '{game.Name}' - not watching for exit.");
            return false;
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
                if (found.Any(p => p.Id == process.Id) || !IsUnder(SafeGetPath(process), game.InstallDir))
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
    /// name before doing the more expensive path check on just those.</summary>
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
                    names.Add(Path.GetFileNameWithoutExtension(exe));

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

    private static string? SafeGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // Anti-cheat, elevated, or already-exited processes refuse this - just skip them.
            return null;
        }
    }

    private static bool IsUnder(string? path, string directory)
        => path is not null
           && path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);
}
