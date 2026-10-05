using System.ComponentModel;
using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>"Uninstall..." (hands the game to its own launcher - nothing is ever deleted from here) and the shutdown button (asks first).</summary>
public class LibraryViewModelActionsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-" + Guid.NewGuid());
    private readonly LibraryViewModel _sut;

    public LibraryViewModelActionsTests()
    {
        _sut = new LibraryViewModel(new SettingsService(_dataDir), new PendingUpdateNotesService(_dataDir));
        _sut.LauncherExeFinder = _ => null; // what is installed on this machine must never matter
        _sut.UninstallWizardFinder = _ => null; // nor which programs Windows lists
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private static GameEntry Game(string id, string name, GameSource source) => new()
    {
        Id = id, Name = name, ExecutablePath = @"C:\G\" + id + ".exe", InstallDir = @"C:\G\" + id, Source = source,
    };

    // ---- where Uninstall goes ----------------------------------------------------------------------

    [Fact]
    public void ASteamGame_GoesStraightToSteamsUninstallPrompt()
    {
        var target = UninstallRouter.Resolve(Game("steam-1245620", "Elden Ring", GameSource.Steam), _ => null);

        Assert.Equal(UninstallRoute.LauncherPage, target.Route);
        Assert.Equal("steam://uninstall/1245620", target.Target);
        Assert.Contains("Elden Ring", target.Explanation);
    }

    [Fact]
    public void AGogGame_OpensItsPageInGalaxy_WhenGalaxyIsInstalled()
    {
        var target = UninstallRouter.Resolve(Game("gog-1207658930", "Witcher", GameSource.Gog), s => s == GameSource.Gog ? @"C:\GOG\GalaxyClient.exe" : null);

        Assert.Equal(UninstallRoute.LauncherPage, target.Route);
        Assert.Equal("goggalaxy://openGameView/1207658930", target.Target);
    }

    [Fact]
    public void AGogGame_WithoutGalaxy_OrAnUninstaller_HasNothingToRun_AndIsNeverSentToASettingsList()
    {
        var target = UninstallRouter.Resolve(Game("gog-1", "Witcher", GameSource.Gog), _ => null);

        Assert.Equal(UninstallRoute.NoUninstaller, target.Route);
    }

    [Fact]
    public void OtherLaunchers_OpenTheLauncherItself_AndSayWhatToDoThere()
    {
        var target = UninstallRouter.Resolve(Game("epic-abc", "Fortnite", GameSource.Epic), s => s == GameSource.Epic ? @"C:\Epic\EpicGamesLauncher.exe" : null);

        Assert.Equal(UninstallRoute.LauncherApp, target.Route);
        Assert.Equal(@"C:\Epic\EpicGamesLauncher.exe", target.Target);
        Assert.Equal("Opened Epic - find Fortnite in your library and choose Uninstall.", target.Explanation);
    }

    [Theory]
    [InlineData(GameSource.Manual)]
    [InlineData(GameSource.Epic)] // launcher not found on this PC
    public void WithNoUninstallerAndNoLauncher_TheRouteIsNoUninstaller_NeverWindowsSettings(GameSource source)
    {
        var target = UninstallRouter.Resolve(Game("x-1", "Some Game", source), _ => null);

        Assert.Equal(UninstallRoute.NoUninstaller, target.Route);
        Assert.DoesNotContain("ms-settings", target.Target);
    }

    [Fact]
    public void AnXboxGame_IsUninstalledAsAPackage_NotSentToSettings()
    {
        var game = Game("xbox-1", "Fortnite", GameSource.Xbox);

        var target = UninstallRouter.Resolve(game, _ => null);

        Assert.Equal(UninstallRoute.XboxPackage, target.Route);
        Assert.Equal(game.InstallDir, target.Arguments);
    }

    [Theory]
    [InlineData("steam-")]
    [InlineData("steam-abc")]
    [InlineData("steam-12 34")]
    [InlineData("steam-1; calc")]
    public void ASteamIdThatIsNotPlainDigits_NeverBecomesALink(string id)
    {
        var target = UninstallRouter.Resolve(Game(id, "Odd", GameSource.Steam), _ => null);

        Assert.DoesNotContain("steam://uninstall", target.Target);
    }

    [Fact]
    public void TheUninstallCommand_OpensTheTarget_AndSaysWhatOpened()
    {
        var opened = new List<string>();
        _sut.OpenUninstallTargetForTest = opened.Add;

        _sut.UninstallGameCommand.Execute(Game("steam-730", "Counter-Strike 2", GameSource.Steam));

        Assert.Equal(["steam://uninstall/730"], opened);
        Assert.Equal("Opened Steam's uninstall prompt for Counter-Strike 2 - confirm there to remove it.", _sut.StatusText);
    }

    [Fact]
    public void WhenTheLaunchersLinkCannotBeOpened_TheFailureIsReported_AndNothingElseOpens()
    {
        var attempts = new List<string>();
        _sut.OpenUninstallTargetForTest = target =>
        {
            attempts.Add(target);
            throw new Win32Exception("no handler registered");
        };

        _sut.UninstallGameCommand.Execute(Game("steam-730", "Counter-Strike 2", GameSource.Steam));

        Assert.Equal(["steam://uninstall/730"], attempts); // no fallback list
        Assert.StartsWith("Couldn't start the uninstaller for Counter-Strike 2", _sut.StatusText);
    }

    [Fact]
    public void AGameWithNoUninstaller_SaysSo_AndOffersItsFolder_NothingElse() => RunAsync(async () =>
    {
        var folders = new List<string>();
        var opened = new List<string>();
        var questions = new List<string>();
        _sut.OpenFolderInExplorerForTest = folders.Add;
        _sut.OpenUninstallTargetForTest = opened.Add;
        var installDir = Path.Combine(_dataDir, "Copied");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "game.exe"), "x");
        var game = new GameEntry { Id = "manual-2", Name = "Copied Game", ExecutablePath = Path.Combine(installDir, "game.exe"), InstallDir = installDir, Source = GameSource.Manual };

        _sut.ConfirmUninstallForTest = q => { questions.Add(q); return false; };
        await _sut.UninstallGameCommand.ExecuteAsync(game);
        Assert.Contains("no uninstaller", questions.Single());
        Assert.Empty(folders);
        Assert.Empty(opened);

        _sut.ConfirmUninstallForTest = _ => true;
        await _sut.UninstallGameCommand.ExecuteAsync(game);
        Assert.Single(folders);
        Assert.Empty(opened); // never Windows' settings or any list
        Assert.True(File.Exists(game.ExecutablePath));
    });

    [Fact]
    public void AnXboxGame_IsRemovedByWindows_OnlyAfterYes_ThenTheLibraryRescans() => RunAsync(async () =>
    {
        var removed = new List<(string Name, string? Family, string Dir)>();
        var rescans = 0;
        var game = new GameEntry
        {
            Id = "xbox-1", Name = "Fortnite", ExecutablePath = @"X:\XboxGames\Fortnite\Content\FortniteLauncher.exe",
            InstallDir = @"X:\XboxGames\Fortnite\Content", Source = GameSource.Xbox,
            LaunchUri = @"shell:appsFolder\Epic.Fortnite_8wekyb3d8bbwe!Game",
        };
        _sut.RemoveXboxPackageForTest = (name, family, dir) => { removed.Add((name, family, dir)); return Task.FromResult((true, "Uninstalled Fortnite.")); };
        _sut.RescanAfterChange = () => { rescans++; return Task.CompletedTask; };

        _sut.ConfirmUninstallForTest = _ => false;
        await _sut.UninstallGameCommand.ExecuteAsync(game);
        Assert.Empty(removed);
        Assert.Equal("Nothing was uninstalled.", _sut.StatusText);
        Assert.Equal(0, rescans);

        _sut.ConfirmUninstallForTest = q => { Assert.Contains("Uninstall Fortnite?", q); return true; };
        await _sut.UninstallGameCommand.ExecuteAsync(game);
        Assert.Equal([("Fortnite", (string?)"Epic.Fortnite_8wekyb3d8bbwe", @"X:\XboxGames\Fortnite\Content")], removed);
        Assert.Equal("Uninstalled Fortnite.", _sut.StatusText);
        Assert.Equal(1, rescans);
    });

    [Fact]
    public void WhenWindowsCannotRemoveTheXboxGame_TheReasonIsShown_AndTheLibraryIsLeftAlone() => RunAsync(async () =>
    {
        var rescans = 0;
        var game = new GameEntry { Id = "xbox-1", Name = "Halo", ExecutablePath = @"X:\XboxGames\Halo\Content\halo.exe", InstallDir = @"X:\XboxGames\Halo\Content", Source = GameSource.Xbox };
        _sut.ConfirmUninstallForTest = _ => true;
        _sut.RemoveXboxPackageForTest = (_, _, _) => Task.FromResult((false, "Windows couldn't uninstall Halo: access denied."));
        _sut.RescanAfterChange = () => { rescans++; return Task.CompletedTask; };

        await _sut.UninstallGameCommand.ExecuteAsync(game);

        Assert.Equal("Windows couldn't uninstall Halo: access denied.", _sut.StatusText);
        Assert.Equal(0, rescans);
    });

    private static void RunAsync(Func<Task> body) => body().GetAwaiter().GetResult();

    [Fact]
    public void Uninstall_NeverTouchesTheGamesFilesOrTheLibrary()
    {
        var installDir = Path.Combine(_dataDir, "Game");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "game.exe"), "x");
        var game = new GameEntry { Id = "manual-1", Name = "Game", ExecutablePath = Path.Combine(installDir, "game.exe"), InstallDir = installDir, Source = GameSource.Manual };
        _sut.SimulateRefreshResult([game]);
        _sut.OpenUninstallTargetForTest = _ => { };
        _sut.ConfirmUninstallForTest = _ => false; // a game with no uninstaller asks whether to open its folder; the answer here is no

        _sut.UninstallGameCommand.Execute(game);

        Assert.True(File.Exists(game.ExecutablePath));
        Assert.True(Directory.Exists(installDir));
    }

    [Fact]
    public void TheDetailsPage_Uninstall_GoesThroughTheLibrary()
    {
        var game = Game("steam-730", "Counter-Strike 2", GameSource.Steam);
        _sut.SimulateRefreshResult([game]);
        var opened = new List<string>();
        _sut.OpenUninstallTargetForTest = opened.Add;
        _sut.InstallSizeEstimatorForTest = (_, _) => null;
        _sut.GameDetailsDialogForTest = vm => vm.RequestUninstall();

        _sut.ShowGameDetailsCommand.Execute(game);

        Assert.Equal(["steam://uninstall/730"], opened);
    }

    // ---- shutdown ----------------------------------------------------------------------------------

    [Fact]
    public void Shutdown_AsksFirst_AndDoesNothingOnNo()
    {
        string? asked = null;
        var ran = new List<string>();
        _sut.ConfirmShutdownForTest = q => { asked = q; return false; };
        _sut.StartShutdownForTest = ran.Add;

        _sut.ShutdownPcCommand.Execute(null);

        Assert.Equal("Shut down your PC?", asked);
        Assert.Empty(ran);
        Assert.False(_sut.IsShutdownPending);
        Assert.Equal("Shutdown cancelled.", _sut.StatusText);
    }

    [Fact]
    public void Shutdown_OnYes_RunsShutdownWithAShortNotice_AndNeverForced()
    {
        var ran = new List<string>();
        _sut.ConfirmShutdownForTest = _ => true;
        _sut.StartShutdownForTest = ran.Add;
        _sut.ShutdownCountdownDelay = (_, token) => Task.Delay(Timeout.Infinite, token);

        _sut.ShutdownPcCommand.Execute(null);

        var args = Assert.Single(ran);
        Assert.StartsWith("/s /t 10 ", args);
        Assert.DoesNotContain("/f", args);
        Assert.DoesNotContain("/r", args);
        Assert.True(_sut.IsShutdownPending);
        Assert.Equal("Shutting down in 10 seconds - press Cancel shutdown to stop it.", _sut.StatusText);
    }

    [Fact]
    public void Shutdown_NamesARunningGameInTheQuestion()
    {
        var game = Game("a", "Apex", GameSource.Manual);
        _sut.SimulateRefreshResult([game]);
        _sut.MarkGameRunning(game);
        string? asked = null;
        _sut.ConfirmShutdownForTest = q => { asked = q; return false; };

        _sut.ShutdownPcCommand.Execute(null);

        Assert.Equal("Apex is still running. Shut down your PC anyway?", asked);
    }

    [Fact]
    public void CancellingAPendingShutdown_RunsShutdownAbort_AndClearsThePendingState()
    {
        var ran = new List<string>();
        _sut.ConfirmShutdownForTest = _ => true;
        _sut.StartShutdownForTest = ran.Add;
        _sut.ShutdownCountdownDelay = (_, token) => Task.Delay(Timeout.Infinite, token);
        _sut.ShutdownPcCommand.Execute(null);

        _sut.CancelShutdownCommand.Execute(null);

        Assert.Equal("/a", ran.Last());
        Assert.False(_sut.IsShutdownPending);
        Assert.Equal("Shutdown cancelled.", _sut.StatusText);
    }

    [Fact]
    public async Task ThePendingState_ClearsItselfOnceTheCountdownHasPassed()
    {
        var release = new TaskCompletionSource();
        _sut.ConfirmShutdownForTest = _ => true;
        _sut.StartShutdownForTest = _ => { };
        _sut.ShutdownCountdownDelay = (_, _) => release.Task;

        _sut.ShutdownPcCommand.Execute(null);
        Assert.True(_sut.IsShutdownPending);

        release.SetResult();
        await Task.Delay(50);

        Assert.False(_sut.IsShutdownPending);
    }

    [Fact]
    public void WhenShutdownExeCannotStart_TheFailureIsReported_AndNothingIsLeftPending()
    {
        _sut.ConfirmShutdownForTest = _ => true;
        _sut.StartShutdownForTest = _ => throw new Win32Exception("access denied");

        _sut.ShutdownPcCommand.Execute(null);

        Assert.False(_sut.IsShutdownPending);
        Assert.StartsWith("Couldn't shut down the PC", _sut.StatusText);
    }
}
