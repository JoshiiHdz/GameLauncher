using System.Diagnostics;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace GameLauncher.Tests.Services;

/// <summary>An opt-in check of the Optimize section against the REAL Windows on the PC running the tests: each tweak is applied, read back from Windows itself, and put
/// back; the memory trim, the power plan, the service names, the startup list, the health check and the clean-up scans are run for real. Nothing is left changed and
/// the clean-up is only SCANNED, never run. It does nothing unless <c>AXIS_REAL_TWEAKS=1</c> is set (CI and an ordinary test run skip it), because it touches this
/// PC's own settings for a moment:
/// <c>$env:AXIS_REAL_TWEAKS=1; dotnet test --filter RealMachineTweakCheck --logger "console;verbosity=detailed"</c>.</summary>
public sealed class RealMachineTweakCheck(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("AXIS_REAL_TWEAKS") == "1";

    [Fact]
    public void EveryUserSettingTweak_ChangesWhatWindowsHonours_AndPutsItBack()
    {
        if (!Enabled)
            return;

        var registry = new CurrentUserRegistry();
        const string exe = @"C:\Windows\System32\notepad.exe";
        var ids = new[] { "game-mode", "capture-off", "quiet-toasts", "windowed-opt", "gpu-high" };

        // What each should leave behind while applied.
        var expected = new (string Key, string Name, string Value)[]
        {
            (@"Software\Microsoft\GameBar", "AutoGameModeEnabled", "dword:1"),
            (@"Software\Microsoft\GameBar", "AllowAutoGameMode", "dword:1"),
            (@"System\GameConfigStore", "GameDVR_Enabled", "dword:0"),
            (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "dword:0"),
            (@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", "dword:0"),
            (@"Software\Microsoft\DirectX\UserGpuPreferences", exe, "string:GpuPreference=2;"),
        };
        var before = expected.ToDictionary(e => (e.Key, e.Name), e => registry.Read(e.Key, e.Name));
        var globalBefore = registry.Read(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");

        var engine = new GameTweakEngine(registry, new AppSettings(), () => { });
        try
        {
            var applied = engine.Begin(ids, exe);
            output.WriteLine("applied: " + string.Join(", ", applied));
            Assert.Equal(ids.Length, applied.Count);

            foreach (var (key, name, value) in expected)
            {
                var now = registry.Read(key, name);
                output.WriteLine($"  {key}\\{name}: {before[(key, name)] ?? "<missing>"} -> {now ?? "<missing>"}");
                Assert.Equal(value, now);
            }

            var global = registry.Read(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
            output.WriteLine($"  DirectXUserGlobalSettings: {globalBefore ?? "<missing>"} -> {global}");
            Assert.Contains("SwapEffectUpgradeEnable=1;", global);
            Assert.True(engine.IsActive);
        }
        finally
        {
            engine.End();
        }

        Assert.False(engine.IsActive);
        foreach (var (key, name, _) in expected)
            Assert.Equal(before[(key, name)], registry.Read(key, name));      // back to exactly what it was (or missing again)

        Assert.Equal(globalBefore, registry.Read(@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings"));
        output.WriteLine("all user settings restored.");
    }

    [Fact]
    public void ThePowerPlan_SwitchesToAFastPlan_AndYourPlanComesBack()
    {
        if (!Enabled)
            return;

        var plans = new PowerPlans();
        var original = plans.GetActive();
        Assert.NotNull(original);
        var listBefore = PowerCfgList();
        output.WriteLine($"active before: {original}; plans: {listBefore.Count}");

        var service = new FocusPlayService(plans, new AppSettings(), () => { });
        try
        {
            var result = service.Begin(onlyWhenPluggedIn: false);
            output.WriteLine($"begin: {result.Outcome} - {result.Message}");
            Assert.True(result.Outcome is FocusPlayOutcome.Switched or FocusPlayOutcome.AlreadyFast, result.Outcome.ToString());

            var during = plans.GetActive();
            output.WriteLine($"active during: {during}");
            Assert.True(during == FocusPlayService.HighPerformance || during == FocusPlayService.UltimatePerformance || result.Outcome == FocusPlayOutcome.Switched);
        }
        finally
        {
            service.End();
        }

        Assert.Equal(original, plans.GetActive());                                  // your plan is back
        var listAfter = PowerCfgList();
        Assert.Equal(listBefore.Count, listAfter.Count);                            // and no plan was left behind
        output.WriteLine("power plan restored; plan list unchanged.");
    }

    [Fact]
    public void FreeMemory_ReadsTheRealMemory_AndTrimsWithoutClosingAnything()
    {
        if (!Enabled)
            return;

        var memory = new MemoryOptimizer();
        var status = memory.GetStatus();
        output.WriteLine($"memory: {status.AvailableBytes / (1 << 20)} MB free of {status.TotalBytes / (1 << 20)} MB");
        Assert.True(status.TotalBytes > 0);
        Assert.True(status.AvailableBytes > 0 && status.AvailableBytes <= status.TotalBytes);

        var result = memory.Trim([]);
        output.WriteLine($"trim: {result.ProcessesTrimmed} trimmed, {result.ProcessesSkipped} skipped, {result.AvailableBefore / (1 << 20)} MB -> {result.AvailableAfter / (1 << 20)} MB available");
        Assert.True(result.ProcessesTrimmed > 0);
        Assert.NotEmpty(Process.GetProcessesByName("explorer"));                    // nothing was closed
    }

    [Fact]
    public void TheServiceTweaks_NameServicesThatExist_AndTheirStateIsReadable()
    {
        if (!Enabled)
            return;

        var states = new SystemServiceStates();
        var registry = new CurrentUserRegistry();
        foreach (var id in new[] { "svc-search", "svc-updates", "svc-telemetry", "svc-spooler" })
        {
            foreach (var service in GameTweakCatalog.ServicesFor(id))
            {
                var running = states.IsRunning(service);
                var sc = Sc("query " + service);
                output.WriteLine($"{id}: {service} running={running?.ToString() ?? "unknown"}; sc query says: {sc.Split('\n').FirstOrDefault(l => l.Contains("STATE"))?.Trim()}");
                Assert.Contains("STATE", sc);                                       // the service exists on this PC
                Assert.NotNull(running);
                Assert.Equal(sc.Contains("RUNNING"), running);                      // the API read and "sc query" agree (what the elevated copy relies on)
            }

            var state = TweakStateReader.Read(id, registry, states);
            output.WriteLine($"   shown to the user: {state?.Text}");
            Assert.NotNull(state);
        }
    }

    [Fact]
    public void EveryTweakRow_HasAWindowsStateLine()
    {
        if (!Enabled)
            return;

        var registry = new CurrentUserRegistry();
        var states = new SystemServiceStates();
        foreach (var tweak in GameTweakCatalog.All.Where(t => t.Kind != TweakKind.Launcher))
        {
            var state = TweakStateReader.Read(tweak.Id, registry, states);
            output.WriteLine($"{tweak.Id}: applied={state?.Applied?.ToString() ?? "n/a"} - {state?.Text}");
            Assert.NotNull(state);
            Assert.False(string.IsNullOrWhiteSpace(state!.Text));
        }
    }

    [Fact]
    public void TheStartupList_ListsRealEntries_AndSwitchingOneOffAndOnWorksThroughWindows()
    {
        if (!Enabled)
            return;

        var name = "AxisTweakCheck-" + Guid.NewGuid().ToString("N")[..8];
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        using (var run = Registry.CurrentUser.CreateSubKey(runKey))
            run.SetValue(name, @"C:\Windows\System32\notepad.exe");

        try
        {
            var service = new StartupAppsService(new SystemStartupSource());
            var all = service.List();
            output.WriteLine($"startup entries on this PC: {all.Count} ({all.Count(a => a.Enabled)} on, {all.Count(a => a.Protected)} protected)");
            var mine = all.Single(a => a.Name == name);
            Assert.True(mine.Enabled);
            Assert.False(mine.Protected);

            Assert.True(service.SetEnabled(mine.Id, false));
            Assert.False(service.List().Single(a => a.Name == name).Enabled);
            using (var approved = Registry.CurrentUser.OpenSubKey(approvedKey))
            {
                var bytes = (byte[]?)approved?.GetValue(name);
                output.WriteLine($"StartupApproved after switching off: {(bytes is null ? "<missing>" : Convert.ToHexString(bytes)[..8])}");
                Assert.NotNull(bytes);
                Assert.True(bytes![0] % 2 == 1);                                    // Task Manager's own "disabled" marker (odd first byte)
            }

            Assert.True(service.SetEnabled(mine.Id, true));
            Assert.True(service.List().Single(a => a.Name == name).Enabled);
        }
        finally
        {
            using (var run = Registry.CurrentUser.OpenSubKey(runKey, writable: true))
                run?.DeleteValue(name, throwOnMissingValue: false);

            using (var approved = Registry.CurrentUser.OpenSubKey(approvedKey, writable: true))
                approved?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void TheHealthCheck_RunsAgainstThisPc_AndTheCleanUpScansTheRealFolders()
    {
        if (!Enabled)
            return;

        var facts = new SystemAdvisorFacts(new PowerPlans(), () => new MemoryOptimizer().GetStatus());
        var findings = GamingAdvisor.Check(facts);
        foreach (var f in findings)
            output.WriteLine($"health: [{f.Level}] {f.Title}");

        Assert.NotEmpty(findings);

        var total = 0L;
        foreach (var category in StorageCleaner.DefaultCategories())
        {
            var scan = category.Scan(CancellationToken.None);                      // a measurement only; nothing is deleted
            total += scan.Bytes;
            output.WriteLine($"clean up: {category.Id,-22} {scan.Bytes / (1 << 20),8} MB in {scan.Files,6} file(s)  (ticked by default: {category.SelectedByDefault})");
            Assert.True(scan.Bytes >= 0 && scan.Files >= 0);
        }

        output.WriteLine($"clean up total found: {total / (1 << 20)} MB (not deleted)");
    }

    private static string Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return text;
    }

    private static List<string> PowerCfgList()
    {
        using var process = Process.Start(new ProcessStartInfo("powercfg.exe", "/list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        return text.Split('\n').Where(l => l.Contains("GUID", StringComparison.OrdinalIgnoreCase) && l.Contains(':')).ToList();
    }
}
