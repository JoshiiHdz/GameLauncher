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
    private const int MaxExeScanDepth = 3;

    /// <summary>
    /// Returns once the game appears to have exited. Returns false if the game's processes were
    /// never found - in that case the caller should leave the window alone rather than popping it
    /// over a game that is probably still running.
    /// </summary>
    public async Task<bool> WaitForExitAsync(GameEntry game, Process? launched, CancellationToken ct = default)
    {
        var candidateNames = GetCandidateProcessNames(game);
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

        Logger.Info($"Watching {running.Count} process(es) for '{game.Name}'.");

        // Wait for everything to exit, then look once more: a launcher process commonly hands off
        // to the real game, so new processes can appear right as the first ones die.
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

            running = FindRunning(game, candidateNames, launched: null);
            if (running.Count == 0)
            {
                Logger.Info($"'{game.Name}' exited.");
                return true;
            }
        }

        return false;
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
