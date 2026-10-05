using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>EA games are found wherever they were installed - not by trusting one registry key or one folder name. These run against a
/// made-up "drive" in a temp folder, so they say nothing about this PC and depend on nothing in it.</summary>
public sealed class EaScannerDiscoveryTests : IDisposable
{
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Ea-" + Guid.NewGuid()) + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        if (Directory.Exists(_drive))
            Directory.Delete(_drive, recursive: true);
    }

    private string Game(string relativeFolder, bool withMarker, string exe = "game.exe")
    {
        var folder = Path.Combine(_drive, relativeFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, exe), new byte[2048]);
        if (withMarker)
        {
            Directory.CreateDirectory(Path.Combine(folder, "__Installer"));
            File.WriteAllText(Path.Combine(folder, "__Installer", "installerdata.xml"), "<x/>");
        }

        return folder;
    }

    private List<GameEntry> Scan() => EaScanner.ScanFolders([_drive]);

    [Theory]
    [InlineData(@"Program Files\EA Games\Apex")]
    [InlineData(@"Program Files (x86)\EA Games\Apex")]
    [InlineData(@"Program Files (x86)\Origin Games\Apex")]
    [InlineData(@"EA Games\Apex")]
    [InlineData(@"Games\EA Games\Apex")]
    [InlineData(@"Origin Games\Apex")]
    public void TheUsualLibraryFolders_AreSearched_OnTheDriveTheyAreOn_EvenWithoutEAsMarker(string relative)
    {
        var folder = Game(relative, withMarker: false);

        var found = Assert.Single(Scan());

        Assert.Equal(GameSource.Ea, found.Source);
        Assert.Equal("Apex", found.Name);
        Assert.Equal(folder, found.InstallDir);
    }

    [Theory]
    [InlineData(@"Stuff\My EA Stuff\Sims 4")]                 // three folders down, no EA-looking name anywhere
    [InlineData(@"Games\Whatever\Battlefield")]
    [InlineData(@"Program Files\Some Folder\Dragon Age")]
    [InlineData(@"Dragon Age")]                                // straight at the drive root
    public void AGameWithEAsMarker_IsFoundWhereverItIs_WithinAFewFoldersOfTheDrive(string relative)
    {
        Game(relative, withMarker: true);

        var found = Assert.Single(Scan());

        Assert.Equal(Path.GetFileName(relative), found.Name);
    }

    [Fact]
    public void AGameDeeperThanTheSweepGoes_WithoutAUsualFolder_IsNotGuessedAt()
    {
        Game(@"a\b\c\d\Too Deep", withMarker: true);

        Assert.Empty(Scan());
    }

    [Fact]
    public void AFolderWithoutTheMarker_OutsideTheUsualFolders_IsNotTakenForAnEAGame()
    {
        Game(@"Stuff\Some Other Game", withMarker: false);

        Assert.Empty(Scan());
    }

    [Fact]
    public void EAsOwnApps_AreNeverListedAsGames()
    {
        Game(@"Program Files\Electronic Arts\EA Desktop", withMarker: true, exe: "EADesktop.exe");
        Game(@"Program Files\EA Games\EA app", withMarker: false);
        Game(@"Program Files\EA Games\Real Game", withMarker: false);

        var found = Assert.Single(Scan());

        Assert.Equal("Real Game", found.Name);
    }

    [Fact]
    public void ElectronicArtsFolder_CountsOnlyForFoldersWithTheMarker()
    {
        Game(@"Program Files\Electronic Arts\Marked Game", withMarker: true);
        Game(@"Program Files\Electronic Arts\Some Tool", withMarker: false);

        var found = Assert.Single(Scan());

        Assert.Equal("Marked Game", found.Name);
    }

    [Fact]
    public void OtherLaunchersFolders_AreNotWalked()
    {
        Game(@"Program Files (x86)\Steam\steamapps\common\Some Game", withMarker: true); // a marker, but in Steam's library

        Assert.Empty(Scan());
    }

    [Fact]
    public void TheSameGameReachedTwoWays_IsListedOnce()
    {
        Game(@"Program Files\EA Games\Apex", withMarker: true); // found by the library folder AND by the sweep

        Assert.Single(Scan());
    }

    [Fact]
    public void SeveralGames_OnSeveralDrives_AreAllFound()
    {
        var second = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Ea2-" + Guid.NewGuid()) + Path.DirectorySeparatorChar;
        try
        {
            Game(@"EA Games\One", withMarker: false);
            Directory.CreateDirectory(Path.Combine(second, "Games", "Two"));
            File.WriteAllBytes(Path.Combine(second, "Games", "Two", "two.exe"), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(second, "Games", "Two", "__Installer"));
            File.WriteAllText(Path.Combine(second, "Games", "Two", "__Installer", "installerdata.xml"), "<x/>");

            var found = EaScanner.ScanFolders([_drive, second]);

            Assert.Equal(["One", "Two"], found.Select(g => g.Name).OrderBy(n => n));
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    // ---- Origin's own manifests ----------------------------------------------------------------------------

    [Theory]
    [InlineData("?currentstate=kReadyToStart&dipinstallpath=C%3A%5CProgram%20Files%20(x86)%5COrigin%20Games%5CTitanfall2&timestamp=1", @"C:\Program Files (x86)\Origin Games\Titanfall2")]
    [InlineData("dipinstallpath=D%3A%5CGames%5CApex", @"D:\Games\Apex")]
    [InlineData("a=1&DIPINSTALLPATH=E%3A%5CX&b=2", @"E:\X")]
    public void AnOriginManifest_GivesTheInstallPath(string manifest, string expected) =>
        Assert.Equal(expected, EaScanner.ParseManifestInstallPath(manifest));

    [Theory]
    [InlineData("")]
    [InlineData("nothing=here")]
    [InlineData("dipinstallpath=")]
    [InlineData("dipinstallpath=relative%5Cpath")]
    public void AManifestWithoutAUsablePath_GivesNothing(string manifest) =>
        Assert.Null(EaScanner.ParseManifestInstallPath(manifest));
}
