using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Services;

/// <summary>"Uninstall..." opens the game's own uninstall wizard: found in the installed-programs list by its folder first (then by exact
/// name), or sitting in the game's folder. Everything here runs on made-up data, never on this PC's real installed programs.</summary>
public sealed class UninstallLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Uninstall-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static GameEntry Game(string name, string installDir, GameSource source = GameSource.Manual, string id = "g") => new()
    {
        Id = id, Name = name, ExecutablePath = Path.Combine(installDir, "game.exe"), InstallDir = installDir, Source = source,
    };

    private static bool Everything(string _) => true;

    // ---- the command line ---------------------------------------------------------------------------

    [Theory]
    [InlineData("\"C:\\Games\\Hades\\unins000.exe\"", "C:\\Games\\Hades\\unins000.exe", "")]
    [InlineData("\"C:\\Games\\Hades\\unins000.exe\" /LOG=\"x.txt\"", "C:\\Games\\Hades\\unins000.exe", "/LOG=\"x.txt\"")]
    [InlineData("C:\\Games\\Hades\\unins000.exe", "C:\\Games\\Hades\\unins000.exe", "")]
    [InlineData("C:\\Games\\Hades\\uninstall.exe /uninstall 42", "C:\\Games\\Hades\\uninstall.exe", "/uninstall 42")]
    public void AnUninstallString_IsSplitIntoTheExeAndItsArguments(string raw, string exe, string arguments)
    {
        var command = UninstallLocator.ParseCommand(raw, Everything);

        Assert.Equal(new UninstallCommand(exe, arguments), command);
    }

    [Fact]
    public void AWindowsInstallerEntry_BecomesAnUninstall_NotAModifyOrRepair()
    {
        var command = UninstallLocator.ParseCommand("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}", _ => false);

        Assert.Equal(new UninstallCommand("MsiExec.exe", "/X{12345678-1234-1234-1234-123456789ABC}"), command);
        Assert.Equal("/X{AAAA}", UninstallLocator.ParseCommand("\"C:\\Windows\\System32\\msiexec.exe\" /X{AAAA}", _ => false)!.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\Games\\unclosed.exe")]
    [InlineData("rundll32 something with no exe")]
    public void AnUnusableString_GivesNothing(string raw) => Assert.Null(UninstallLocator.ParseCommand(raw, Everything));

    [Fact]
    public void AnUninstallerThatIsNotThere_GivesNothing() =>
        Assert.Null(UninstallLocator.ParseCommand("\"C:\\Gone\\unins000.exe\"", _ => false));

    // ---- matching a game to a program ---------------------------------------------------------------

    [Fact]
    public void AProgramInTheGamesOwnFolder_IsItsUninstaller()
    {
        var game = Game("Hades", @"D:\Games\Hades");
        var programs = new[]
        {
            new InstalledProgram("Something else", "\"C:\\Other\\unins000.exe\"", @"C:\Other", null),
            new InstalledProgram("Hades", "\"D:\\Games\\Hades\\unins000.exe\"", @"D:\Games\Hades\", null),
        };

        var found = UninstallLocator.Find(game, programs, Everything, _ => []);

        Assert.Equal(@"D:\Games\Hades\unins000.exe", found!.Exe);
    }

    [Fact]
    public void AnUninstallerInsideTheGamesFolder_MatchesEvenWithNoInstallLocation()
    {
        var game = Game("Obscure Title", @"D:\Games\Obscure");
        var programs = new[] { new InstalledProgram("Totally Different Name", "\"D:\\Games\\Obscure\\bin\\remove.exe\"", null, null) };

        Assert.Equal(@"D:\Games\Obscure\bin\remove.exe", UninstallLocator.Find(game, programs, Everything, _ => [])!.Exe);
    }

    [Fact]
    public void ALaunchersOwnEntry_OrASharedParentFolder_IsNeverTakenForAGame()
    {
        var game = Game("Apex Legends", @"C:\Program Files\EA Games\Apex");
        var programs = new[]
        {
            // the parent folder that holds many games, and the launcher itself
            new InstalledProgram("EA app", "\"C:\\Program Files\\Electronic Arts\\EA Desktop\\uninstall.exe\"", @"C:\Program Files\EA Games", null),
            new InstalledProgram("Some Library", "\"C:\\Program Files\\EA Games\\unins000.exe\"", @"C:\Program Files\EA Games", null),
        };

        Assert.Null(UninstallLocator.Find(game, programs, Everything, _ => []));
    }

    [Fact]
    public void AnExactNameMatch_IsUsedWhenNoFolderMatches_IgnoringCaseAndTrademarks()
    {
        var game = Game("HADES", @"E:\Somewhere");
        var programs = new[] { new InstalledProgram("Hades™", "\"C:\\Hades Setup\\uninst.exe\"", null, null) };

        Assert.Equal(@"C:\Hades Setup\uninst.exe", UninstallLocator.Find(game, programs, Everything, _ => [])!.Exe);
    }

    [Fact]
    public void ANameThatOnlyContainsTheGamesName_IsNotAMatch()
    {
        var game = Game("Hades", @"E:\Somewhere");
        var programs = new[] { new InstalledProgram("Hades II Soundtrack Player", "\"C:\\x\\uninst.exe\"", null, null) };

        Assert.Null(UninstallLocator.Find(game, programs, Everything, _ => []));
    }

    [Fact]
    public void AFolderMatchBeatsANameMatch()
    {
        var game = Game("Hades", @"D:\Games\Hades");
        var programs = new[]
        {
            new InstalledProgram("Hades", "\"C:\\Wrong\\uninst.exe\"", null, null),
            new InstalledProgram("Some Other Label", "\"D:\\Games\\Hades\\unins000.exe\"", @"D:\Games\Hades", null),
        };

        Assert.Equal(@"D:\Games\Hades\unins000.exe", UninstallLocator.Find(game, programs, Everything, _ => [])!.Exe);
    }

    // ---- an uninstaller sitting in the folder -------------------------------------------------------

    [Fact]
    public void WithNoRegistryEntry_TheGamesOwnFolderIsSearched_InnoSetupFirst()
    {
        var folder = Path.Combine(_directory, "Game");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "game.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "uninstall.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "unins000.exe"), "x");

        var found = UninstallLocator.Find(Game("Game", folder), []);

        Assert.Equal(Path.Combine(folder, "unins000.exe"), found!.Exe);
    }

    [Fact]
    public void ADriveRoot_IsNeverSearchedForAnUninstaller()
    {
        // A loose exe in a watched folder's top makes that folder its "install folder"; a stray uninstaller there is not its own.
        var root = Path.GetPathRoot(_directory)!;
        var game = Game("Loose", root);
        var programs = new[] { new InstalledProgram("Other", "\"" + Path.Combine(root, "unins000.exe") + "\"", root, null) };

        Assert.Null(UninstallLocator.Find(game, programs, Everything, _ => [Path.Combine(root, "unins000.exe")]));
    }

    [Fact]
    public void AnUninstallFolderOneLevelDown_IsSearchedToo_AndAGameWithNoUninstallerGivesNothing()
    {
        var folder = Path.Combine(_directory, "Game");
        Directory.CreateDirectory(Path.Combine(folder, "_Uninstall"));
        File.WriteAllText(Path.Combine(folder, "game.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "_Uninstall", "uninstaller.exe"), "x");

        Assert.Equal(Path.Combine(folder, "_Uninstall", "uninstaller.exe"), UninstallLocator.Find(Game("Game", folder), [])!.Exe);

        var bare = Path.Combine(_directory, "Bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "game.exe"), "x");
        File.WriteAllText(Path.Combine(bare, "setup.exe"), "x"); // a setup program is not an uninstaller
        Assert.Null(UninstallLocator.Find(Game("Bare", bare), []));
    }

    // ---- the route -----------------------------------------------------------------------------------

    [Fact]
    public void TheRoute_PrefersSteamsPrompt_ThenTheGamesOwnWizard_ThenTheLauncher()
    {
        var wizard = new UninstallCommand(@"D:\G\unins000.exe", "/x");

        var steam = UninstallRouter.Resolve(Game("Hades", @"D:\G", GameSource.Steam, "steam-1145360"), _ => @"C:\Steam\steam.exe", _ => wizard);
        Assert.Equal(UninstallRoute.LauncherPage, steam.Route);

        var epic = UninstallRouter.Resolve(Game("Hades", @"D:\G", GameSource.Epic, "epic-x"), _ => @"C:\Epic\Launcher.exe", _ => wizard);
        Assert.Equal(UninstallRoute.Wizard, epic.Route);
        Assert.Equal(@"D:\G\unins000.exe", epic.Target);
        Assert.Equal("/x", epic.Arguments);
        Assert.Contains("uninstaller", epic.Explanation);

        var noWizard = UninstallRouter.Resolve(Game("Hades", @"D:\G", GameSource.Epic, "epic-x"), _ => @"C:\Epic\Launcher.exe", _ => null);
        Assert.Equal(UninstallRoute.LauncherApp, noWizard.Route);

        var gog = UninstallRouter.Resolve(Game("Witcher", @"D:\G", GameSource.Gog, "gog-1207658930"), s => s == GameSource.Gog ? @"C:\GOG\Galaxy.exe" : null, _ => wizard);
        Assert.Equal(UninstallRoute.Wizard, gog.Route); // GOG's own unins000.exe beats opening Galaxy
    }

    [Fact]
    public void TheCommand_StartsTheGamesWizard_NotTheLauncher()
    {
        using var sut = NewLibrary();
        var game = Game("Hades", @"D:\G", GameSource.Epic, "epic-x");
        sut.UninstallWizardFinder = _ => new UninstallCommand(@"D:\G\unins000.exe", "/SILENT");
        sut.LauncherExeFinder = _ => @"C:\Epic\Launcher.exe";
        var started = new List<UninstallCommand>();
        var opened = new List<string>();
        sut.StartUninstallWizardForTest = started.Add;
        sut.OpenUninstallTargetForTest = opened.Add;

        sut.UninstallGameCommand.Execute(game);

        Assert.Equal([new UninstallCommand(@"D:\G\unins000.exe", "/SILENT")], started);
        Assert.Empty(opened);
        Assert.Contains("uninstaller for Hades", sut.StatusText);
    }

    [Fact]
    public void ADeclinedAdministratorPrompt_OpensNothingElse()
    {
        using var sut = NewLibrary();
        sut.UninstallWizardFinder = _ => new UninstallCommand(@"D:\G\unins000.exe", "");
        var opened = new List<string>();
        sut.OpenUninstallTargetForTest = opened.Add;
        sut.StartUninstallWizardForTest = _ => throw new System.ComponentModel.Win32Exception(1223, "The operation was canceled by the user");

        sut.UninstallGameCommand.Execute(Game("Hades", @"D:\G"));

        Assert.Empty(opened); // no Installed-apps fallback after a "no"
        Assert.Contains("permission", sut.StatusText);
    }

    // ---- Xbox / Store packages ----------------------------------------------------------------------

    [Theory]
    [InlineData(@"shell:appsFolder\Epic.Fortnite_8wekyb3d8bbwe!Game", @"X:\XboxGames\Fortnite\Content", "Epic.Fortnite_8wekyb3d8bbwe")]
    [InlineData(null, @"C:\Program Files\WindowsApps\Microsoft.254428597CFE2_1.2.3.4_x64__8wekyb3d8bbwe", "Microsoft.254428597CFE2_8wekyb3d8bbwe")]
    [InlineData(null, @"C:\Program Files\WindowsApps\SomeGame_2.0.0.0_neutral__abcdefghij123\Content", "SomeGame_abcdefghij123")]
    public void AnXboxGamesPackageFamily_IsReadFromItsLaunchIdOrItsPackageFolder(string? launchUri, string installDir, string expected)
    {
        var game = new GameEntry { Id = "x", Name = "G", ExecutablePath = installDir + @"\g.exe", InstallDir = installDir, Source = GameSource.Xbox, LaunchUri = launchUri };

        Assert.Equal(expected, XboxPackageRemover.FamilyNameOf(game));
    }

    [Theory]
    [InlineData(null, @"X:\XboxGames\Fortnite\Content")]                                  // no package folder, no launch id
    [InlineData(@"shell:appsFolder\not a family; calc!App", @"X:\Games\G")]                 // never a command in disguise
    [InlineData(@"shell:appsFolder\Name_TOOSHORT!App", @"X:\Games\G")]
    public void WhenTheFamilyIsNotKnown_NoneIsInvented(string? launchUri, string installDir)
    {
        var game = new GameEntry { Id = "x", Name = "G", ExecutablePath = installDir + @"\g.exe", InstallDir = installDir, Source = GameSource.Xbox, LaunchUri = launchUri };

        Assert.Null(XboxPackageRemover.FamilyNameOf(game));
    }

    [Fact]
    public void TheRemovalScript_CarriesNoGameData_ItIsPassedInTheEnvironment()
    {
        var info = XboxPackageRemover.BuildStartInfo("Epic.Fortnite_8wekyb3d8bbwe", @"X:\XboxGames\Fortnite\Content");

        Assert.Equal("powershell.exe", info.FileName);
        Assert.DoesNotContain("Fortnite", XboxPackageRemover.RemoveScript);
        Assert.DoesNotContain("XboxGames", info.ArgumentList.Last());
        Assert.Equal("Epic.Fortnite_8wekyb3d8bbwe", info.Environment["AXIS_FAMILY"]);
        Assert.Equal(@"X:\XboxGames\Fortnite\Content", info.Environment["AXIS_DIR"]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);

        // A family name that is not a family name is dropped rather than handed to anything.
        Assert.Equal("", XboxPackageRemover.BuildStartInfo("x'; calc; '", @"X:\G").Environment["AXIS_FAMILY"]);
    }

    [Fact]
    public async Task RemovingAPackageThatIsNotInstalled_RealPowerShell_ReportsThereIsNothingToUninstall()
    {
        // The real plumbing, on a package that cannot exist: PowerShell starts, looks the package up, finds none and says so (exit 3).
        var (succeeded, message) = await XboxPackageRemover.RemoveAsync("Nothing Here", "Axis.DoesNotExist_abcdefghij123", @"Z:
o\sucholder");

        Assert.False(succeeded);
        Assert.Contains("nothing to uninstall", message);
    }

    private LibraryViewModelHolder NewLibrary() => new(_directory);

    /// <summary>A library on an isolated folder, disposable so each test cleans up after itself.</summary>
    private sealed class LibraryViewModelHolder : IDisposable
    {
        private readonly LibraryViewModel _vm;

        public LibraryViewModelHolder(string directory) =>
            _vm = new LibraryViewModel(new SettingsService(directory), new PendingUpdateNotesService(directory));

        public Func<GameEntry, UninstallCommand?> UninstallWizardFinder { set => _vm.UninstallWizardFinder = value; }
        public Func<GameSource, string?> LauncherExeFinder { set => _vm.LauncherExeFinder = value; }
        public Action<UninstallCommand>? StartUninstallWizardForTest { set => _vm.StartUninstallWizardForTest = value; }
        public Action<string>? OpenUninstallTargetForTest { set => _vm.OpenUninstallTargetForTest = value; }
        public CommunityToolkit.Mvvm.Input.IRelayCommand<GameEntry?> UninstallGameCommand => _vm.UninstallGameCommand;
        public string StatusText => _vm.StatusText;

        public void Dispose() { }
    }
}
