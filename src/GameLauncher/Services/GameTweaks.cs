using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GameLauncher.Models;
using Microsoft.Win32;

namespace GameLauncher.Services;

/// <summary>How much a tweak can matter: nothing here is risky, and each is undone when the game ends. "Safe" changes a setting of the signed-in user only; "Needs permission"
/// stops a Windows background service for the length of the game and starts it again (Windows asks for permission once per game).</summary>
public enum TweakKind
{
    /// <summary>A setting of the signed-in user's own (HKCU): no permission needed.</summary>
    UserSetting,

    /// <summary>A Windows background service stopped while the game runs and started again after: needs administrator permission.</summary>
    Service,

    /// <summary>Something the launcher itself does just before the game starts (free memory, switch the power plan): no registry value and no permission. These two
    /// share the list so one place shows everything that can happen before a game starts, and each game can pick its own.</summary>
    Launcher,
}

/// <summary>One thing that can be switched for the length of a game. Every tweak is temporary and reversible; nothing is edited for good.</summary>
public sealed record GameTweak(string Id, string Name, string Description, TweakKind Kind, string Effect, bool Advanced = false)
{
    public bool NeedsPermission => Kind == TweakKind.Service;

    /// <summary>The heading the tweak sits under on the Optimize page and in Settings.</summary>
    public string Group => Kind switch
    {
        TweakKind.Launcher => "Memory and power",
        TweakKind.UserSetting => "Windows settings",
        _ => "Background services",
    };

    /// <summary>One line on what asking for it involves.</summary>
    public string Requirement => (NeedsPermission ? "Windows asks permission once per game" : "No permission needed") + (Advanced ? " - advanced, small effect" : "");
}

/// <summary>A registry value a user-setting tweak writes (always under HKCU) and what it writes there.</summary>
internal sealed record RegistryChange(string Key, string Name, object On, RegistryValueKind Kind = RegistryValueKind.DWord);

/// <summary>The catalogue of game tweaks. Chosen for being safe: each only changes something that Windows or the signed-in user can change at any time, is undone afterwards,
/// and none touches security features, drivers, the network stack, the registry's system hive or anything the person made. Left out on purpose: turning off security
/// mitigations or Core isolation, timer and priority-separation edits, network (Nagle/TCP) edits, SysMain/Superfetch (it helps a hard drive), the Xbox services (Game
/// Pass games need them), and disabling services for good.</summary>
public static class GameTweakCatalog
{
    public const string MemoryTrimId = "memory-trim";
    public const string PowerPlanId = "power-plan";

    public static readonly GameTweak FreeMemory = new(MemoryTrimId, "Free up memory before launching a game",
        "Asks Windows to trim the memory other apps are holding but not using, just before the game starts. It runs every time a game starts, however much memory is free, and it never closes anything.",
        TweakKind.Launcher, "Runs once as the game starts. Windows hands the pages back when an app needs them.");

    public static readonly GameTweak PowerPlan = new(PowerPlanId, "High performance power plan while playing",
        "Switches Windows to a high performance power plan while the game runs and back to your own plan when it ends (or if this app is closed or restarted). On a laptop on battery it is left alone unless you say otherwise in Focus play options.",
        TweakKind.Launcher, "Changes only the active power plan, and puts your plan back.");

    public static readonly GameTweak GameMode = new("game-mode", "Windows Game Mode",
        "Turns Windows Game Mode on for the session, so Windows favours the game over background work.", TweakKind.UserSetting,
        "Changes a setting of yours (Settings > Gaming > Game Mode) and puts it back.");

    public static readonly GameTweak CaptureOff = new("capture-off", "Pause Game Bar background recording",
        "Stops Xbox Game Bar from recording the game in the background, which costs a little frame rate.", TweakKind.UserSetting,
        "Changes your Game Bar capture settings and puts them back.");

    public static readonly GameTweak QuietToasts = new("quiet-toasts", "Silence notification pop-ups",
        "Keeps Windows notification banners from popping over the game (and from stealing its focus).", TweakKind.UserSetting,
        "Turns notification banners off for the session and back on after.");

    public static readonly GameTweak WindowedOptimizations = new("windowed-opt", "Optimizations for windowed games",
        "Windows 11's own setting: windowed and borderless DirectX 10/11 games are presented the efficient way (as full screen is), which can lower latency and smooth frame pacing.", TweakKind.UserSetting,
        "Turns on Settings > Graphics > Optimizations for windowed games for the session and puts it back. Other options in that setting are kept.");

    public static readonly GameTweak GpuHigh = new("gpu-high", "Use the fastest graphics card",
        "Tells Windows to run this game on the high-performance graphics card (a laptop's discrete GPU), where there are two.", TweakKind.UserSetting,
        "Sets the game's Windows graphics preference for the session and removes it after.");

    public static readonly GameTweak StopSearch = new("svc-search", "Pause Windows Search indexing",
        "Stops the indexer (Windows Search) while you play, so it cannot start scanning files in the middle of a game.", TweakKind.Service,
        "Stops the Windows Search service and starts it again when the game ends.");

    // Windows Update itself is deliberately NOT stopped: Microsoft supports pausing it from Settings, and force-stopping its services can interrupt an install. What competes
    // with an online game for the network is Delivery Optimization (Windows and Store downloads, including sharing with other PCs), which is safe to pause.
    public static readonly GameTweak StopUpdates = new("svc-updates", "Pause Delivery Optimization downloads",
        "Stops Delivery Optimization (Windows and Store downloads, and sharing them with other PCs) while you play, so they do not compete with the game for the network.", TweakKind.Service,
        "Stops the service for the session and starts it again after. Windows Update itself is left alone: pause it in Windows Settings if you want it quiet.");

    public static readonly GameTweak StopTelemetry = new("svc-telemetry", "Pause diagnostics and telemetry",
        "Stops the Connected User Experiences and Telemetry service (DiagTrack) while you play. Its effect on a game is small; most people can leave it.", TweakKind.Service,
        "Stops the service for the session and starts it again after.", Advanced: true);

    public static readonly GameTweak StopSpooler = new("svc-spooler", "Pause the print spooler",
        "Stops the print spooler while you play. An idle spooler costs almost nothing, so this is for tidiness more than speed.", TweakKind.Service,
        "Stops the service for the session and starts it again after (printing works again at once).", Advanced: true);

    // Windows Error Reporting is not offered: it is how a crash gets diagnosed, and the research found no gaming benefit worth that.
    public static IReadOnlyList<GameTweak> All { get; } = [FreeMemory, PowerPlan, GameMode, CaptureOff, QuietToasts, WindowedOptimizations, GpuHigh, StopSearch, StopUpdates, StopTelemetry, StopSpooler];

    public static GameTweak? ById(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>The registry values behind each user-setting tweak.</summary>
    internal static IReadOnlyList<RegistryChange> ChangesFor(string id) => id switch
    {
        "game-mode" => [new(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), new(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1)],
        "capture-off" =>
        [
            new(@"System\GameConfigStore", "GameDVR_Enabled", 0),
            new(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
        ],
        "quiet-toasts" => [new(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 0)],
        _ => [],
    };

    /// <summary>The Windows services behind each service tweak (names, not display names). Only these can ever be stopped.</summary>
    public static IReadOnlyList<string> ServicesFor(string id) => id switch
    {
        "svc-search" => ["WSearch"],
        "svc-updates" => ["DoSvc"],
        "svc-telemetry" => ["DiagTrack"],
        "svc-spooler" => ["Spooler"],
        _ => [],
    };
}

/// <summary>The part of the registry a tweak may touch: HKCU only. A seam, so tests never edit a real one.</summary>
public interface IUserRegistry
{
    /// <summary>The value, as "dword:N" or "string:text"; null when it is missing.</summary>
    string? Read(string key, string name);

    void Write(string key, string name, string encoded);

    void Delete(string key, string name);
}

/// <summary>The real current-user registry.</summary>
public sealed class CurrentUserRegistry : IUserRegistry
{
    public string? Read(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) switch
            {
                int i => "dword:" + i,
                string s => "string:" + s,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void Write(string key, string name, string encoded)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        if (encoded.StartsWith("dword:", StringComparison.Ordinal))
            k.SetValue(name, int.Parse(encoded[6..]), RegistryValueKind.DWord);
        else
            k.SetValue(name, encoded.StartsWith("string:", StringComparison.Ordinal) ? encoded[7..] : encoded, RegistryValueKind.String);
    }

    public void Delete(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }
}

/// <summary>Applies the user-setting tweaks for a game and puts everything back. The original of each value is written to the settings file BEFORE it is changed, so a
/// crash or a power cut cannot leave a setting changed: the next start restores it. A value the person changed in the meantime is never overwritten.</summary>
public sealed class GameTweakEngine(IUserRegistry registry, AppSettings settings, Action save)
{
    private const string Missing = "<missing>";
    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private readonly object _gate = new();

    /// <summary>True while changes made here have not been undone.</summary>
    public bool IsActive => settings.GameTweakUndo.Count > 0;

    /// <summary>Applies these user-setting tweaks (unknown ids and service tweaks are ignored). <paramref name="gameExecutable"/> is the game's exe, for the graphics preference.
    /// Returns the ids that were applied.</summary>
    public IReadOnlyList<string> Begin(IEnumerable<string> ids, string? gameExecutable)
    {
        lock (_gate)
        {
            var applied = new List<string>();
            foreach (var id in ids.Distinct())
            {
                if (GameTweakCatalog.ById(id) is not { Kind: TweakKind.UserSetting })
                    continue;

                var changes = ChangesFor(id, gameExecutable);
                if (changes.Count == 0)
                    continue;

                foreach (var (key, name, on) in changes)
                {
                    var slot = $"{id}|{key}|{name}";
                    if (settings.GameTweakUndo.ContainsKey(slot))
                        continue; // already in force (another game is running)

                    settings.GameTweakUndo[slot] = registry.Read(key, name) ?? Missing;
                    settings.GameTweakApplied[slot] = on;
                }

                save(); // the originals are on disk before anything is written

                foreach (var (key, name, on) in changes)
                {
                    try
                    {
                        registry.Write(key, name, on);

                        // Read back from Windows itself: the log says what the value really is now, not just what was asked for.
                        var was = settings.GameTweakUndo.GetValueOrDefault($"{id}|{key}|{name}");
                        var now = registry.Read(key, name);
                        Logger.Info($"Game tweak '{id}': {key}\\{name} {(was is null or Missing ? "<missing>" : was)} -> {now ?? "<missing>"}"
                            + (now == on ? " (took)" : " (DID NOT TAKE - Windows shows something else)"));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        Logger.Warn($"Game tweak '{id}': could not write {key}\\{name}.", ex);
                    }
                }

                applied.Add(id);
            }

            return applied;
        }
    }

    private List<(string Key, string Name, string Encoded)> ChangesFor(string id, string? gameExecutable)
    {
        if (id == GameTweakCatalog.WindowedOptimizations.Id)
        {
            // One string holds several options (windowed-game optimizations, variable refresh rate, Auto HDR): only this option is set, the others are kept.
            const string option = "SwapEffectUpgradeEnable";
            var current = registry.Read(GpuKey, "DirectXUserGlobalSettings");
            var text = current is not null && current.StartsWith("string:", StringComparison.Ordinal) ? current[7..] : "";
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)).ToList();
            parts.Add(option + "=1");
            return [(GpuKey, "DirectXUserGlobalSettings", "string:" + string.Join(';', parts) + ";")];
        }

        if (id == GameTweakCatalog.GpuHigh.Id)
        {
            // Per game: the exe's own entry. Without a real exe path there is nothing to set.
            return gameExecutable is { Length: > 0 } && Path.IsPathFullyQualified(gameExecutable) && !gameExecutable.Contains('\0')
                ? [(GpuKey, gameExecutable, "string:GpuPreference=2;")]
                : [];
        }

        return GameTweakCatalog.ChangesFor(id).Select(c => (c.Key, c.Name, "dword:" + Convert.ToInt32(c.On))).ToList();
    }

    /// <summary>Puts every changed value back (or removes the ones that did not exist), except values the person has changed since. Safe to call when nothing was applied
    /// (also the crash recovery at start).</summary>
    public void End()
    {
        lock (_gate)
        {
            if (settings.GameTweakUndo.Count == 0)
                return;

            foreach (var (slot, original) in settings.GameTweakUndo.ToList())
            {
                var parts = slot.Split('|', 3);
                if (parts.Length != 3)
                {
                    settings.GameTweakUndo.Remove(slot);
                    settings.GameTweakApplied.Remove(slot);
                    continue;
                }

                var (key, name) = (parts[1], parts[2]);
                try
                {
                    var applied = settings.GameTweakApplied.GetValueOrDefault(slot);
                    var current = registry.Read(key, name);
                    var untouched = applied is null || current == applied; // someone else's change in the meantime is left alone
                    if (untouched)
                    {
                        if (original == Missing)
                            registry.Delete(key, name);
                        else
                            registry.Write(key, name, original);

                        var after = registry.Read(key, name);
                        var wanted = original == Missing ? null : original;
                        Logger.Info($"Game tweak put back: {key}\\{name} -> {after ?? "<missing>"}" + (after == wanted ? " (as it was)" : $" (EXPECTED {wanted ?? "<missing>"})"));
                    }
                    else
                    {
                        Logger.Info($"Game tweak left alone: {key}\\{name} was changed by someone else while the game ran ({current ?? "<missing>"}).");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    Logger.Warn($"Game tweaks: could not restore {key}\\{name}; it will be tried again at the next start.", ex);
                    continue; // kept on the record
                }

                settings.GameTweakUndo.Remove(slot);
                settings.GameTweakApplied.Remove(slot);
            }

            save();
        }
    }
}

/// <summary>Stops Windows services for the length of a game, with administrator permission asked once per game, and starts them again after. The launcher starts a copy of
/// itself elevated (the same "run as administrator" prompt as everything else): that copy stops the services of the named tweaks (only those in
/// <see cref="GameTweakCatalog"/>, by id), notes which it stopped, waits until it is told to restore - or the launcher exits - and starts exactly those again.</summary>
public static class ServiceGuard
{
    internal const string Switch = "--service-guard";

    /// <summary>Test seam: stands in for starting the elevated copy. Receives the tweak ids and returns a handle that restores.</summary>
    internal static Func<IReadOnlyList<string>, IDisposable?>? StartForTest { get; set; }

    /// <summary>What the elevated copy did, written to the state file: the services it stopped and has not yet started again.</summary>
    internal sealed record State(List<string> Stopped);

    /// <summary>The launcher's side: starts the elevated copy for these service tweaks. Returns a handle whose Dispose tells it to restore the services, or null when
    /// permission was declined or there was nothing to do.</summary>
    public static IDisposable? Start(IReadOnlyList<string> tweakIds, bool raisePriority = false)
    {
        var ids = tweakIds.Where(i => GameTweakCatalog.ById(i) is { Kind: TweakKind.Service }).Distinct().ToList();
        if (ids.Count == 0 && !raisePriority)
            return null;

        if (StartForTest is { } seam)
            return seam(ids);

        if (!ElevatedCleaner.IsTheLauncher || Environment.ProcessPath is not { } exe)
            return null;

        // Windows' permission prompt blocks whoever asks for it until it is answered, so it is asked for off the UI thread: the game starts, and the window stays
        // alive, while the person decides. What is returned here is a promise of that start; disposing it restores the services whenever (and if ever) they were stopped.
        return new PendingHandle(Task.Run(() => StartElevated(exe, ids, raisePriority)));
    }

    /// <summary>True while the administrator helper is running for a game: a program that refuses High priority from the launcher can be handed to it.</summary>
    internal static volatile bool HelperActive;

    /// <summary>Test seam: receives the process ids handed to the helper instead of writing the file it reads.</summary>
    internal static Action<IReadOnlyList<int>>? RaiseRequestsForTest { get; set; }

    private const string RaiseFileName = "game-priority-pids.json";

    internal static string RaiseFilePath => Path.Combine(AppPaths.DataDir, RaiseFileName);

    /// <summary>Hands process ids to the administrator helper, which raises them to High priority with its own permissions (a game started by an anti-cheat service
    /// refuses a program that is not an administrator). Only the ids are passed; the helper never raises anything above High.</summary>
    internal static void RequestRaise(IEnumerable<int> pids)
    {
        var list = pids.Distinct().ToList();
        if (RaiseRequestsForTest is { } seam)
        {
            seam(list);
            return;
        }

        try
        {
            var all = new HashSet<int>(list);
            if (File.Exists(RaiseFilePath) && JsonSerializer.Deserialize<List<int>>(File.ReadAllText(RaiseFilePath)) is { } existing)
                all.UnionWith(existing);

            File.WriteAllText(RaiseFilePath, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn("High priority: could not hand the process to the administrator helper.", ex);
        }
    }

    private static IDisposable? StartElevated(string exe, List<string> ids, bool raisePriority)
    {
        var name = "Axis-ServiceGuard-" + Guid.NewGuid().ToString("N");
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        try
        {
            try { File.Delete(StatePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a leftover from an earlier run must not be read as this one's
            try { File.Delete(RaiseFilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var what = (ids.Count > 0 ? "pause " + string.Join(", ", ids) : "") + (ids.Count > 0 && raisePriority ? " and " : "") + (raisePriority ? "raise this game's priority" : "");
            Logger.Info($"Game tweaks: asking Windows for permission ({what}).");
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = true, // required for the "runas" verb
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{Switch} {(ids.Count > 0 ? string.Join(',', ids) : "none")} --parent {Environment.ProcessId} --event {name} --state \"{StatePath}\" --raise-file \"{RaiseFilePath}\"",
            };

            var process = Process.Start(start);
            if (process is null)
            {
                stop.Dispose();
                return null;
            }

            // Whether the helper really paused anything is told by its state file (written once the services are stopped, after the permission prompt):
            // say so in this log, so "did it work?" has an answer here and not only in the helper's own log.
            if (ids.Count > 0)
                _ = Task.Run(() => ReportWhatTheHelperDid(ids));

            HelperActive = true;
            return new Handle(stop, process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Logger.Info($"Game tweaks: the services were not paused ({ex.Message}) - permission was probably declined.");
            stop.Dispose();
            return null;
        }
    }

    private static void ReportWhatTheHelperDid(List<string> tweakIds)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120); // the permission prompt waits for the person
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(StatePath))
                {
                    Thread.Sleep(150);
                    var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
                    var asked = tweakIds.SelectMany(GameTweakCatalog.ServicesFor).Distinct().ToList();
                    var stopped = state?.Stopped ?? [];
                    var left = asked.Where(s => !stopped.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                    Logger.Info($"Game tweaks: services paused for the game: {(stopped.Count == 0 ? "none" : string.Join(", ", stopped))}"
                        + (left.Count > 0 ? $" (not paused: {string.Join(", ", left)} - not running, or Windows would not stop them)." : "."));
                    return;
                }

                Thread.Sleep(500);
            }

            Logger.Warn("Game tweaks: no word from the services helper after two minutes (the permission prompt may be waiting, or was closed).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warn("Game tweaks: could not read what the services helper did.", ex);
        }
    }

    private const string StateFileName = "game-tweaks-services.json";

    internal static string StatePath => Path.Combine(AppPaths.DataDir, StateFileName);

    private sealed class Handle(EventWaitHandle stop, Process process) : IDisposable
    {
        public void Dispose()
        {
            // "Restore now" is signalled at once; waiting for the services to come back (sc start can take seconds each) is not done on the thread that called this -
            // that is the UI thread, at the moment the launcher window is coming back after the game.
            HelperActive = false;
            try
            {
                stop.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var exited = process.WaitForExit(30_000);
                    Logger.Info(exited
                        ? $"Game tweaks: the services helper has finished (exit code {process.ExitCode}); the services it paused are started again."
                        : "Game tweaks: the services helper is still putting services back after 30 seconds.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    stop.Dispose();
                    process.Dispose();
                }
            });
        }
    }

    /// <summary>The start in progress: disposing it (the game has ended) restores the services as soon as the start has finished, whichever way that went.</summary>
    private sealed class PendingHandle(Task<IDisposable?> start) : IDisposable
    {
        public void Dispose() => start.ContinueWith(t => t.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>The elevated copy's work. Returns an exit code (0 normally).</summary>
    internal static int RunFromArguments(IReadOnlyList<string> args, Func<string, string, bool>? runSc = null, Func<int, RaiseOutcome>? raise = null, Action<TimeSpan>? pause = null)
    {
        string? Value(string option)
        {
            var at = args.ToList().IndexOf(option);
            return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
        }

        var switchAt = args.ToList().IndexOf(Switch);
        if (switchAt < 0 || switchAt + 1 >= args.Count)
            return 2;

        var sc = runSc ?? ServiceControl;
        var wanted = args[switchAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(GameTweakCatalog.ServicesFor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Only the launcher's own state file is ever written or deleted by this elevated copy, whatever the command line says.
        var statePath = Value("--state") is { } given && string.Equals(Path.GetFileName(given), StateFileName, StringComparison.OrdinalIgnoreCase) ? given : null;
        var stopped = new List<string>();
        Logger.Info($"Services helper: asked to pause {string.Join(", ", wanted)}.");
        foreach (var service in wanted)
        {
            var running = sc("query", service); // true only when the service is running
            var stoppedNow = running && sc("stop", service);
            if (stoppedNow)
                stopped.Add(service);

            Logger.Info($"Services helper: {service} - {(!running ? "not running, left alone" : stoppedNow ? "stopped" : "would not stop")}.");
        }

        if (statePath is not null)
        {
            try { File.WriteAllText(statePath, JsonSerializer.Serialize(new State(stopped))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // Wait until the launcher says restore, or goes away.
        EventWaitHandle? signal = null;
        try
        {
            if (Value("--event") is { } eventName)
                signal = EventWaitHandle.OpenExisting(eventName);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
        }

        // Programs the launcher could not raise (a game started by an anti-cheat service refuses it): this copy has the permissions, so it raises them to High and keeps
        // them there. Only the launcher's own file of process ids is read, and nothing is ever raised above High.
        var raiseFile = Value("--raise-file") is { } rf && string.Equals(Path.GetFileName(rf), RaiseFileName, StringComparison.OrdinalIgnoreCase) ? rf : null;
        var raiser = raise ?? ElevatedEnsureHigh;
        var seen = new Dictionary<int, RaiseOutcome>();

        void RaiseWhatIsAsked()
        {
            if (raiseFile is null || !File.Exists(raiseFile))
                return;

            List<int>? asked = null;
            try { asked = JsonSerializer.Deserialize<List<int>>(File.ReadAllText(raiseFile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

            foreach (var id in (asked ?? []).Distinct().Take(64))
            {
                if (seen.TryGetValue(id, out var before) && before == RaiseOutcome.Gone)
                    continue;

                var outcome = raiser(id);
                if (!seen.TryGetValue(id, out var last) || last != outcome || outcome == RaiseOutcome.Raised)
                {
                    var said = outcome switch
                    {
                        RaiseOutcome.AlreadyHigh => "is on High",
                        RaiseOutcome.Raised => "raised to High" + (seen.ContainsKey(id) ? " (the game had put it back)" : ""),
                        RaiseOutcome.Gone => "has exited",
                        _ => "would not take High even with administrator rights",
                    };
                    Logger.Info($"Priority helper: pid {id} - {said}.");
                }

                seen[id] = outcome;
            }
        }

        var parent = int.TryParse(Value("--parent"), out var pid) ? pid : 0;
        var limit = DateTime.UtcNow.AddHours(12);
        while (DateTime.UtcNow < limit)
        {
            if (signal?.WaitOne(1500) == true)
                break;

            if (signal is null)
                Thread.Sleep(1500);

            RaiseWhatIsAsked();
            if (parent != 0 && !IsRunning(parent))
                break;
        }

        var wait = pause ?? Thread.Sleep;
        foreach (var service in stopped)
            Logger.Info($"Services helper: {service} - {StartAgain(service, sc, wait)}.");

        if (raiseFile is not null)
        {
            try { File.Delete(raiseFile); }
            catch (IOException) { }
        }

        if (statePath is not null)
        {
            try { File.Delete(statePath); }
            catch (IOException) { }
        }

        return 0;
    }

    /// <summary>What was found when the helper was asked to put a program on High.</summary>
    internal enum RaiseOutcome { AlreadyHigh, Raised, Failed, Gone }

    /// <summary>Starts a service again and makes sure it is running: a service that is still stopping refuses to start, and one Windows has already started again
    /// (Windows Search has triggers that do) reports an error to "start" although it is running - so it asks what state it is in, rather than trusting one command.</summary>
    internal static string StartAgain(string service, Func<string, string, bool> sc, Action<TimeSpan> pause)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (sc("query", service))
                return attempt == 0 ? "already running (Windows had started it again)" : "started again";

            sc("start", service); // refused while it is still stopping: asked again below
            pause(TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500));
        }

        return sc("query", service) ? "started again" : "COULD NOT BE STARTED AGAIN (it will start with Windows)";
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool _debugPrivilegeTried;

    /// <summary>Turns on the debug privilege an administrator token carries but keeps switched off (what Task Manager does), so a program that grants nothing to an
    /// ordinary caller can be opened. Used only by the elevated copy, only to read and set a priority class.</summary>
    private static void EnableDebugPrivilege()
    {
        if (_debugPrivilegeTried)
            return;

        _debugPrivilegeTried = true;
        try
        {
            using var self = Process.GetCurrentProcess();
            if (!OpenProcessToken(self.Handle, 0x20 | 0x8, out var token))
                return;

            try
            {
                if (LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
                {
                    var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                    AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static RaiseOutcome ElevatedEnsureHigh(int id)
    {
        EnableDebugPrivilege();
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.PriorityClass is ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                return RaiseOutcome.AlreadyHigh;

            process.PriorityClass = ProcessPriorityClass.High;
            process.Refresh();
            return process.PriorityClass == ProcessPriorityClass.High ? RaiseOutcome.Raised : RaiseOutcome.Failed;
        }
        catch (ArgumentException)
        {
            return RaiseOutcome.Gone;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return RaiseOutcome.Failed;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ServiceName = new("^[A-Za-z0-9_]{1,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>sc.exe: "query" is true when the service is RUNNING, "stop" and "start" true when the command was accepted.</summary>
    private static bool ServiceControl(string verb, string service)
    {
        if (!ServiceName.IsMatch(service) || verb is not ("query" or "stop" or "start"))
            return false;

        try
        {
            var start = new ProcessStartInfo("sc.exe", $"{verb} {service}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return verb == "query" ? output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) : process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
