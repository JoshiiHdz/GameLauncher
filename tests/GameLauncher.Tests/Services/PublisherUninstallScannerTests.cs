using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>Where a game installed through a launcher's installer lives, from what its installed-programs entry says.</summary>
public sealed class PublisherUninstallScannerTests
{
    private static bool Dir(string path) => path is @"D:\Games\Foo" or @"D:\Games\Bar";
    private static bool File(string path) => path is @"D:\Games\Foo\foo.exe";

    [Theory]
    [InlineData("Rockstar Games SDK")]
    [InlineData("Rockstar Games Redistributable")]
    [InlineData("EA Runtime Package")]
    [InlineData("Ubisoft Game Launcher Prerequisites")]
    [InlineData("Some Publisher - Driver (x64)")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable")]
    [InlineData("Foo SDK v2")]
    public void APublishersSoftware_IsNeverTakenForAGame(string displayName) =>
        Assert.True(PublisherUninstallScanner.IsComponentName(displayName));

    [Theory]
    [InlineData("Grand Theft Auto V")]
    [InlineData("Max Payne 3")]
    [InlineData("Red Dead Redemption 2")]
    [InlineData("Diablo IV")]
    [InlineData("SDKing Adventures")]       // "SDK" inside a longer word is just part of a name
    [InlineData("Runtimeless")]
    public void ARealGame_IsNotMistakenForSoftware(string displayName) =>
        Assert.False(PublisherUninstallScanner.IsComponentName(displayName));

    [Fact]
    public void TheInstallLocation_IsUsedWhenItIsThere() =>
        Assert.Equal(@"D:\Games\Bar", PublisherUninstallScanner.ResolveInstallDir(@"D:\Games\Bar", @"D:\Games\Foo\foo.exe", Dir, File));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"D:\Gone")]
    public void WhenTheInstallLocationIsBlankOrGone_TheFolderOfTheDisplayIconExeIsUsed(string? installLocation) =>
        Assert.Equal(@"D:\Games\Foo", PublisherUninstallScanner.ResolveInstallDir(installLocation, @"D:\Games\Foo\foo.exe", Dir, File));

    [Theory]
    [InlineData("\"D:\\Games\\Foo\\foo.exe\",0")]
    [InlineData("D:\\Games\\Foo\\foo.exe,-1")]
    [InlineData("  \"D:\\Games\\Foo\\foo.exe\"  ")]
    public void AnIconIndexOrQuotesAreNotPartOfThePath(string icon) =>
        Assert.Equal(@"D:\Games\Foo", PublisherUninstallScanner.ResolveInstallDir(null, icon, Dir, File));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"D:\Games\Foo\missing.exe")]       // not there
    [InlineData(@"D:\Games\Foo\foo.ico")]            // not an exe
    [InlineData(@"foo.exe")]                         // not a full path
    public void WithNothingUsable_NoFolderIsInvented(string? icon) =>
        Assert.Null(PublisherUninstallScanner.ResolveInstallDir(null, icon, Dir, File));
}
