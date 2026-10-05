using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>The "don't search this drive" switch across the app: the library, the sidebar, Settings and the scan.</summary>
public sealed class IgnoredDriveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Ignored-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static GameEntry Game(string name, string dir, GameSource source = GameSource.Steam) => new()
    {
        Id = name, Name = name, ExecutablePath = Path.Combine(dir, name + ".exe"), InstallDir = dir, Source = source,
    };

    private (LibraryViewModel Vm, List<string> Rescans) Library(params GameEntry[] games)
    {
        var rescans = new List<string>();
        var vm = new LibraryViewModel(new SettingsService(_directory), new PendingUpdateNotesService(_directory))
        {
            InstallSizeEstimatorForTest = (_, _) => null,
            VolumeSerialOf = _ => null, // letters decide, whatever disks this PC has
        };
        vm.RescanAfterChange = () => { rescans.Add("rescan"); return Task.CompletedTask; };
        vm.SimulateRefreshResult([.. games]);
        vm.SelectViewCommand.Execute("all");
        return (vm, rescans);
    }

    [Fact]
    public async Task SwitchingADriveOff_RemovesItsGamesAtOnce_WithoutARescan()
    {
        var (vm, rescans) = Library(Game("Alpha", @"Y:\Games\Alpha"), Game("Beta", @"Z:\Games\Beta", GameSource.Epic), Game("Gamma", @"Z:\Other\Gamma"));
        Assert.Equal(3, vm.Games.Count);

        await vm.SetDriveIgnoredAsync("Z:", ignored: true);

        Assert.Equal(["Alpha"], vm.Games.Select(g => g.Name));
        Assert.Empty(rescans); // instant: nothing waits on the disks
        Assert.Contains(vm.SourceItems, s => s.Source == GameSource.Steam && s.HasGames);
        Assert.DoesNotContain(vm.SourceItems, s => s.Source == GameSource.Epic && s.HasGames); // its launcher row goes with its last game
    }

    [Fact]
    public async Task TheChoice_IsSaved_ByLetterAndSerial_AndSurvivesARestart()
    {
        var (vm, _) = Library(Game("Beta", @"Z:\Games\Beta"));
        vm.VolumeSerialOf = root => root.StartsWith("Z") ? 4242u : null;

        await vm.SetDriveIgnoredAsync("z", ignored: true);

        var saved = new SettingsService(_directory).Load().IgnoredDrives.Single();
        Assert.Equal("Z:", saved.Letter);
        Assert.Equal(4242u, saved.VolumeSerial);
    }

    [Fact]
    public async Task SwitchingItBackOn_ScansAgain_AndRemovesTheRecord()
    {
        var (vm, rescans) = Library(Game("Beta", @"Z:\Games\Beta"));
        await vm.SetDriveIgnoredAsync("Z:", ignored: true);
        Assert.Empty(vm.Games);

        await vm.SetDriveIgnoredAsync("Z:", ignored: false);

        Assert.Single(rescans); // the games come back with a (full-disk) scan
        Assert.Empty(new SettingsService(_directory).Load().IgnoredDrives);
    }

    [Fact]
    public async Task AScanThatFinishesAfterTheDriveWasSwitchedOff_DoesNotBringItsGamesBack()
    {
        var (vm, _) = Library();
        await vm.SetDriveIgnoredAsync("Z:", ignored: true);

        vm.SimulateRefreshResult([Game("Late", @"Z:\Games\Late"), Game("Fine", @"Y:\Games\Fine")]);
        vm.SelectViewCommand.Execute("all");

        Assert.Equal(["Fine"], vm.Games.Select(g => g.Name));
    }

    [Fact]
    public async Task SwitchingTheSelectedDriveOff_ClearsTheDriveFilter()
    {
        var (vm, _) = Library(Game("Beta", @"Z:\Games\Beta"), Game("Alpha", @"Y:\Games\Alpha"));
        vm.SelectedDriveLetter = "Z:";

        await vm.SetDriveIgnoredAsync("Z:", ignored: true);

        Assert.Null(vm.SelectedDriveLetter);
        Assert.Equal(["Alpha"], vm.Games.Select(g => g.Name));
    }

    [Fact]
    public async Task SwitchingTheSameDriveOffTwice_ChangesNothingTheSecondTime()
    {
        var (vm, _) = Library(Game("Beta", @"Z:\Games\Beta"));

        await vm.SetDriveIgnoredAsync("Z:", ignored: true);
        await vm.SetDriveIgnoredAsync("Z:", ignored: true);

        Assert.Single(new SettingsService(_directory).Load().IgnoredDrives);
    }

    [Fact]
    public async Task ARealDriveSwitchedOff_StaysInTheSidebarDimmed_AndInSettings_SoItCanComeBack()
    {
        var system = DriveFilter.NormalizeLetter(Path.GetTempPath()); // a drive that certainly exists
        var (vm, _) = Library(Game("Alpha", Path.Combine(Path.GetTempPath(), "Alpha")));
        vm.VolumeSerialOf = _ => null;

        await vm.SetDriveIgnoredAsync(system, ignored: true);

        var row = vm.Drives.Single(d => d.Letter.Equals(system, StringComparison.OrdinalIgnoreCase));
        Assert.True(row.IsIgnored);
        Assert.False(vm.SearchableDrives.Single(d => d.Letter.Equals(system, StringComparison.OrdinalIgnoreCase)).IsSearched);
        Assert.Empty(vm.Games);

        vm.SelectDriveCommand.Execute(system); // clicking a switched-off drive opens nothing
        Assert.Null(vm.SelectedDriveLetter);
    }

    [Fact]
    public async Task TheSettingsSwitch_DoesTheSameAsTheSidebarMenu()
    {
        var system = DriveFilter.NormalizeLetter(Path.GetTempPath());
        var (vm, _) = Library(Game("Alpha", Path.Combine(Path.GetTempPath(), "Alpha")));
        await vm.SetDriveIgnoredAsync("Q:", ignored: true); // builds the drive lists
        var item = vm.SearchableDrives.Single(d => d.Letter.Equals(system, StringComparison.OrdinalIgnoreCase));
        Assert.True(item.IsSearched);

        item.IsSearched = false; // the switch in Settings

        Assert.Equal(["Q:", system], new SettingsService(_directory).Load().IgnoredDrives.Select(d => d.Letter));
        Assert.Empty(vm.Games);
        Assert.True(vm.Drives.Single(d => d.Letter.Equals(system, StringComparison.OrdinalIgnoreCase)).IsIgnored);
    }

    [Fact]
    public void FindingGamesWithoutALauncher_IsOnByDefault_Saved_AndAScanFollowsTheSwitch()
    {
        var (vm, rescans) = Library();
        Assert.True(vm.FindGamesWithoutLauncher);

        vm.FindGamesWithoutLauncher = false;

        Assert.False(new SettingsService(_directory).Load().FindGamesWithoutLauncher);
        Assert.Single(rescans);
    }

    [Fact]
    public void ResettingSettings_TurnsItBackOn_WithoutStartingAScanOfItsOwn()
    {
        var (vm, rescans) = Library();
        vm.FindGamesWithoutLauncher = false;
        rescans.Clear();

        vm.ResetSettingsCore();

        Assert.True(vm.FindGamesWithoutLauncher);
        Assert.Empty(rescans); // the reset rescans once itself
    }

    [Fact]
    public void TheUsersRescan_IsADifferentCommandFromTheStartupScan()
    {
        var (vm, _) = Library();

        Assert.NotSame(vm.RescanCommand, vm.RefreshCommand);
    }
}
