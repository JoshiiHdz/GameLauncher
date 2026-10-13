using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>Game tweaks: temporary, reversible, and never able to leave a setting or a service changed.</summary>
public sealed class GameTweaksTests
{
    private static (GameTweakEngine Engine, PretendRegistry Registry, AppSettings Settings, List<int> Saves) Make()
    {
        var registry = new PretendRegistry();
        var settings = new AppSettings();
        var saves = new List<int>();
        return (new GameTweakEngine(registry, settings, () => saves.Add(settings.GameTweakUndo.Count)), registry, settings, saves);
    }

    [Fact]
    public void TheCatalogue_IsSafeByConstruction()
    {
        Assert.Equal(GameTweakCatalog.All.Count, GameTweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var tweak in GameTweakCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tweak.Description));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Effect));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Name));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Group));
            Assert.False(string.IsNullOrWhiteSpace(tweak.Requirement));
            if (tweak.Kind == TweakKind.Launcher)
            {
                // The launcher's own: memory and the power plan. No registry value, no service, no permission.
                Assert.Empty(GameTweakCatalog.ChangesFor(tweak.Id));
                Assert.Empty(GameTweakCatalog.ServicesFor(tweak.Id));
                Assert.False(tweak.NeedsPermission);
            }
            else if (tweak.Kind == TweakKind.Service)
            {
                Assert.NotEmpty(GameTweakCatalog.ServicesFor(tweak.Id));
                Assert.True(tweak.NeedsPermission);
            }
            else if (tweak.Id != GameTweakCatalog.GpuHigh.Id && tweak.Id != GameTweakCatalog.WindowedOptimizations.Id) // (these two are worked out per game or merged into a value)
            {
                Assert.NotEmpty(GameTweakCatalog.ChangesFor(tweak.Id));
                Assert.All(GameTweakCatalog.ChangesFor(tweak.Id), c => Assert.DoesNotContain("HKEY", c.Key)); // current-user paths only
            }
        }

        // Only the services this list names can ever be stopped, and none of the ones that matter for games or for safety.
        var all = GameTweakCatalog.All.SelectMany(t => GameTweakCatalog.ServicesFor(t.Id)).ToList();
        Assert.DoesNotContain(all, s => s.StartsWith("Xbl", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Xbox", StringComparison.OrdinalIgnoreCase)
            || s is "WinDefend" or "SysMain" or "mpssvc" or "BFE" or "Dhcp" or "Dnscache" or "Audiosrv" or "RpcSs");
    }

    [Fact]
    public void Begin_WritesTheTweaks_RecordsTheOriginalsFirst_AndEndPutsEverythingBack()
    {
        var (engine, registry, settings, saves) = Make();
        registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")] = "dword:0";
        // GameDVR_Enabled does not exist yet.

        var applied = engine.Begin(["game-mode", "capture-off"], null);

        Assert.Equal(["game-mode", "capture-off"], applied);
        Assert.Equal("dword:1", registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")]);
        Assert.Equal("dword:0", registry.Values[(@"System\GameConfigStore", "GameDVR_Enabled")]);
        Assert.True(engine.IsActive);
        Assert.Equal("dword:0", settings.GameTweakUndo[@"game-mode|Software\Microsoft\GameBar|AutoGameModeEnabled"]);
        Assert.Equal("<missing>", settings.GameTweakUndo[@"capture-off|System\GameConfigStore|GameDVR_Enabled"]);
        Assert.NotEmpty(saves);                                     // the record was written to disk

        engine.End();

        Assert.Equal("dword:0", registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")]);   // what it was
        Assert.False(registry.Values.ContainsKey((@"System\GameConfigStore", "GameDVR_Enabled")));          // what did not exist, does not
        Assert.False(engine.IsActive);
        Assert.Empty(settings.GameTweakApplied);
    }

    [Fact]
    public void AValueTheUserChangedInTheMeantime_IsNotOverwrittenOnRestore()
    {
        var (engine, registry, _, _) = Make();
        registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")] = "dword:0";
        engine.Begin(["game-mode"], null);

        registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")] = "dword:7"; // changed by someone else while the game ran
        engine.End();

        Assert.Equal("dword:7", registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")]);
    }

    [Fact]
    public void ACrash_IsUndoneAtTheNextStart_ByANewEngineReadingTheSavedRecord()
    {
        var (engine, registry, settings, _) = Make();
        registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")] = "dword:0";
        engine.Begin(["game-mode", "quiet-toasts"], null);
        // ... the app dies here; the settings file keeps the record.

        var next = new GameTweakEngine(registry, settings, () => { });
        Assert.True(next.IsActive);
        next.End();

        Assert.Equal("dword:0", registry.Values[(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")]);
        Assert.Empty(registry.Values.Where(v => v.Key.Name == "NOC_GLOBAL_SETTING_TOASTS_ENABLED"));
    }

    [Fact]
    public void ServiceTweaksAndUnknownIds_AreNeverWrittenToTheRegistry()
    {
        var (engine, registry, _, _) = Make();

        var applied = engine.Begin(["svc-search", "svc-updates", "no-such-tweak", @"..\evil"], null);

        Assert.Empty(applied);
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void TheGraphicsPreference_IsSetForTheGamesOwnExe_AndRemovedAfter()
    {
        var (engine, registry, _, _) = Make();
        var exe = @"C:\Games\Apex\apex.exe";

        engine.Begin(["gpu-high"], exe);
        Assert.Equal("string:GpuPreference=2;", registry.Values[(@"Software\Microsoft\DirectX\UserGpuPreferences", exe)]);
        engine.End();
        Assert.False(registry.Values.ContainsKey((@"Software\Microsoft\DirectX\UserGpuPreferences", exe)));

        engine.Begin(["gpu-high"], "apex.exe");   // not a full path: nothing to set
        engine.Begin(["gpu-high"], null);
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void TheServiceGuard_StopsOnlyRunningServicesOfTheNamedTweaks_AndStartsExactlyThoseAgain()
    {
        var calls = new List<string>();
        var gate = new object();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WSearch", "DoSvc" }; // the spooler is not running
        bool Sc(string verb, string service)
        {
            lock (gate)
            {
                calls.Add(verb + " " + service);

                // The services behave: a stopped one is not running until it is started again.
                if (verb == "stop")
                    running.Remove(service);
                else if (verb == "start")
                    running.Add(service);

                return verb != "query" || running.Contains(service);
            }
        }

        var name = "Axis-Test-" + Guid.NewGuid().ToString("N");
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var stateDir = Path.Combine(Path.GetTempPath(), "axis-guard-" + Guid.NewGuid());
        Directory.CreateDirectory(stateDir);
        var state = Path.Combine(stateDir, "game-tweaks-services.json");            // the one file name the elevated copy will write or delete
        var task = Task.Run(() => ServiceGuard.RunFromArguments(
            [ServiceGuard.Switch, "svc-search,svc-updates,svc-spooler,not-a-tweak,svc-nope", "--parent", "0", "--event", name, "--state", state], Sc));

        // Wait for it to have stopped them (the state file appears), then ask it to restore.
        for (var i = 0; i < 150 && !File.Exists(state); i++)
            Thread.Sleep(20);
        Assert.True(File.Exists(state));
        Assert.Contains("WSearch", File.ReadAllText(state));
        signal.Set();

        Assert.Equal(0, task.Result);
        lock (gate)
        {
            Assert.Contains("stop WSearch", calls);
            Assert.Contains("stop DoSvc", calls);
            Assert.DoesNotContain("stop Spooler", calls);                // it was not running: left alone
            Assert.DoesNotContain("stop wuauserv", calls);               // Windows Update itself is never stopped
            Assert.Contains("start WSearch", calls);
            Assert.Contains("start DoSvc", calls);
            Assert.DoesNotContain("start Spooler", calls);               // and never started
            Assert.False(calls.Any(c => c.Contains("not-a-tweak") || c.Contains("svc-nope")));
        }

        Assert.False(File.Exists(state));                                // the record is gone once everything is back
        Assert.Equal(2, ServiceGuard.RunFromArguments([ServiceGuard.Switch]));   // nothing to say: it does nothing
        Directory.Delete(stateDir, true);
    }

    [Fact]
    public void TheServiceGuard_NeverWritesOrDeletesAFileOtherThanItsOwnStateFile()
    {
        var other = Path.Combine(Path.GetTempPath(), "axis-not-mine-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(other, "keep me");
        try
        {
            var name = "Axis-Test-" + Guid.NewGuid().ToString("N");
            using var signal = new EventWaitHandle(true, EventResetMode.ManualReset, name); // already set: it restores at once

            var code = ServiceGuard.RunFromArguments([ServiceGuard.Switch, "svc-search", "--parent", "0", "--event", name, "--state", other], (_, _) => false);

            Assert.Equal(0, code);
            Assert.Equal("keep me", File.ReadAllText(other));                        // not overwritten, not deleted
        }
        finally
        {
            File.Delete(other);
        }
    }

    // ---- Starting a service again, and the helper that raises priority with administrator rights ----------------------------------

    [Fact]
    public void AServiceIsStartedAgainUntilItIsRunning_AndAnAlreadyRunningOneIsNotAnError()
    {
        // Still stopping at first (refuses to start), running by the third look.
        var queries = 0;
        var starts = 0;
        bool Slow(string verb, string service)
        {
            if (verb == "start")
            {
                starts++;
                return false;
            }

            return ++queries >= 3;
        }

        Assert.Equal("started again", ServiceGuard.StartAgain("WSearch", Slow, _ => { }));
        Assert.True(starts >= 2);                                                    // it was asked more than once, not given up on after one refusal

        // Windows had already started it again (Windows Search has triggers that do): "start" says error, but it is running.
        var calls = new List<string>();
        Assert.StartsWith("already running", ServiceGuard.StartAgain("WSearch", (verb, _) => { calls.Add(verb); return verb == "query"; }, _ => { }));
        Assert.DoesNotContain("start", calls);

        // Never running: said plainly, after trying a handful of times and not forever.
        var tries = 0;
        Assert.StartsWith("COULD NOT BE STARTED AGAIN", ServiceGuard.StartAgain("WSearch", (verb, _) => { if (verb == "start") tries++; return false; }, _ => { }));
        Assert.InRange(tries, 2, 12);
    }

    [Fact]
    public void TheHelper_RaisesTheProgramsItIsHandedInAFile_AndNothingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "axis-raise-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var raiseFile = Path.Combine(dir, "game-priority-pids.json");
            File.WriteAllText(raiseFile, "[4242, 4243]");
            var name = "Axis-Test-" + Guid.NewGuid().ToString("N");
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            var raised = new List<int>();
            var task = Task.Run(() => ServiceGuard.RunFromArguments(
                [ServiceGuard.Switch, "none", "--parent", "0", "--event", name, "--raise-file", raiseFile],
                (_, _) => false,
                id => { lock (raised) raised.Add(id); return id == 4243 ? ServiceGuard.RaiseOutcome.Gone : ServiceGuard.RaiseOutcome.Raised; },
                _ => { }));

            for (var i = 0; i < 200 && raised.Count < 2; i++)
                Thread.Sleep(20);

            signal.Set();
            Assert.Equal(0, task.Result);
            lock (raised)
            {
                Assert.Contains(4242, raised);
                Assert.Contains(4243, raised);
                Assert.Equal(2, raised.Distinct().Count());
            }

            Assert.False(File.Exists(raiseFile));                                     // the request file is removed when the helper is done

            // A file with any other name is never read.
            var other = Path.Combine(dir, "other.json");
            File.WriteAllText(other, "[1]");
            var name2 = "Axis-Test-" + Guid.NewGuid().ToString("N");
            using var signal2 = new EventWaitHandle(true, EventResetMode.ManualReset, name2);
            var seen = 0;
            ServiceGuard.RunFromArguments([ServiceGuard.Switch, "none", "--parent", "0", "--event", name2, "--raise-file", other], (_, _) => false,
                _ => { seen++; return ServiceGuard.RaiseOutcome.Raised; }, _ => { });
            Assert.Equal(0, seen);
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
