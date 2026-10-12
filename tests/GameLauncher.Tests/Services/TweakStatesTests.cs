using System.IO;
using GameLauncher.Services;
using GameLauncher.Tests.TestSupport;

namespace GameLauncher.Tests.Services;

/// <summary>What Windows has right now for each tweak, read-only: the line under each switch that says "Game Mode is already on".</summary>
public sealed class TweakStatesTests
{
    private const string GameBar = @"Software\Microsoft\GameBar";

    private static TweakWindowsState? Read(string id, PretendRegistry registry, NoServices? services = null) =>
        TweakStateReader.Read(id, registry, services ?? new NoServices());

    [Fact]
    public void GameMode_IsOnWhenWindowsHasNeverBeenTold_AndOffOnlyWhenItWasTurnedOff()
    {
        var registry = new PretendRegistry();
        Assert.True(Read("game-mode", registry)!.Applied);                    // Windows 11's default: on (the user's PC that showed "off")

        registry.Values[(GameBar, "AutoGameModeEnabled")] = "dword:0";
        var off = Read("game-mode", registry)!;
        Assert.False(off.Applied);
        Assert.Contains("Off in Windows", off.Text);

        registry.Values[(GameBar, "AutoGameModeEnabled")] = "dword:1";
        var on = Read("game-mode", registry)!;
        Assert.True(on.Applied);
        Assert.Contains("Already on", on.Text);
    }

    [Fact]
    public void Capture_SaysOnWhenEitherValueIsOn_OffWhenSetOff_AndDefaultWhenNothingIsSet()
    {
        var registry = new PretendRegistry();
        Assert.Null(Read("capture-off", registry)!.Applied);                   // nothing set: Windows' own default, not claimed either way

        registry.Values[(@"System\GameConfigStore", "GameDVR_Enabled")] = "dword:0";
        Assert.True(Read("capture-off", registry)!.Applied);

        registry.Values[(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")] = "dword:1";
        Assert.False(Read("capture-off", registry)!.Applied);
    }

    [Fact]
    public void Toasts_AreOnUnlessTheyWereSwitchedOff()
    {
        var registry = new PretendRegistry();
        Assert.False(Read("quiet-toasts", registry)!.Applied);

        registry.Values[(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED")] = "dword:0";
        Assert.True(Read("quiet-toasts", registry)!.Applied);
    }

    [Fact]
    public void WindowedOptimizations_AreReadFromTheOneStringThatHoldsSeveralOptions()
    {
        var registry = new PretendRegistry();
        var key = (@"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");
        Assert.False(Read("windowed-opt", registry)!.Applied);

        registry.Values[key] = "string:VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;";
        Assert.False(Read("windowed-opt", registry)!.Applied);

        registry.Values[key] = "string:VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;";
        Assert.True(Read("windowed-opt", registry)!.Applied);
    }

    [Fact]
    public void AService_IsRunning_Stopped_OrNotOnThisPc()
    {
        var registry = new PretendRegistry();
        var services = new NoServices();

        Assert.Null(Read("svc-search", registry, services)!.Applied);          // not on this PC / cannot be read: nothing claimed

        services.Running["WSearch"] = true;
        var running = Read("svc-search", registry, services)!;
        Assert.False(running.Applied);
        Assert.Contains("Running", running.Text);

        services.Running["WSearch"] = false;
        Assert.True(Read("svc-search", registry, services)!.Applied);
    }

    [Fact]
    public void TheRealServiceManager_IsReadWithoutStartingAnything()
    {
        var real = new SystemServiceStates();

        Assert.True(real.IsRunning("RpcSs"));                                  // always running on Windows
        Assert.Null(real.IsRunning("AxisNoSuchService"));                      // not installed: nothing claimed
        Assert.Null(real.IsRunning("bad name; &"));                            // not a service name at all
    }

    [Fact]
    public void EveryTweakThatCanBeRead_SaysSomething_AndTheLaunchersOwnAreLeftToTheLibrary()
    {
        var registry = new PretendRegistry();
        foreach (var tweak in GameTweakCatalog.All)
        {
            var state = Read(tweak.Id, registry);
            if (tweak.Kind == TweakKind.Launcher)
                Assert.Null(state); // memory and the power plan are read by the library, which has the memory and power-plan access
            else
                Assert.False(string.IsNullOrWhiteSpace(state?.Text), tweak.Id);
        }
    }

    [Fact]
    public void AStartupCommand_YieldsItsProgramsFile_QuotedOrNot()
    {
        Assert.Equal(@"C:\Program Files\Discord\Update.exe", StartupAppInfo.ExecutableOf("\"C:\\Program Files\\Discord\\Update.exe\" --processStart Discord.exe"));
        Assert.Equal(@"C:\Program Files\Steam\steam.exe", StartupAppInfo.ExecutableOf(@"C:\Program Files\Steam\steam.exe -silent"));
        Assert.Null(StartupAppInfo.ExecutableOf("cmd /c start x"));            // no path to read
        Assert.Null(StartupAppInfo.ExecutableOf(""));
    }

    [Fact]
    public void AStartupProgram_IsDescribedByItsOwnFile_AndAMissingOneIsNull()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var text = StartupAppInfo.Describe($"\"{cmd}\" /c");

        Assert.NotNull(text);
        Assert.Contains("Windows Command Processor", text);
        Assert.Contains("Microsoft", text);
        Assert.Null(StartupAppInfo.Describe(@"C:\definitely\not\here\missing.exe"));
    }

    [Fact]
    public void TheElevatedHelper_IsStartedByTheLauncherWhateverItsFileIsCalled_AndByNothingElse()
    {
        // A copy of the launcher may carry any file name (the test build is GameLauncher-controller-test.exe): what counts is the program, not the name. This was the bug
        // where "Clear files" raised no permission prompt on that copy.
        Assert.True(ElevatedCleaner.IsLauncherProcess("GameLauncher", @"C:\Dev\GameLauncher-controller-test.exe"));
        Assert.True(ElevatedCleaner.IsLauncherProcess("GameLauncher", @"C:\Program Files\Axis\GameLauncher.exe"));
        Assert.False(ElevatedCleaner.IsLauncherProcess("testhost", @"C:\x\testhost.exe"));     // a test host never raises a permission prompt
        Assert.False(ElevatedCleaner.IsLauncherProcess("GameLauncher", null));
        Assert.False(ElevatedCleaner.IsTheLauncher);                                          // and under this test run there is no launcher to elevate
    }
}
