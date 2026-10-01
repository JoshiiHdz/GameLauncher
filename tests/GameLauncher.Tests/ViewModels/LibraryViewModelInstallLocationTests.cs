using System.IO;
using GameLauncher.Models;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.ViewModels;

/// <summary>"Open install location" lands on the game's exact executable (selected in its folder) when that file exists, and on the
/// folder otherwise - so a game whose exe has moved, or a packaged game with no confirmed exe, still opens somewhere useful.</summary>
public class LibraryViewModelInstallLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GameLauncherTests-InstallLoc-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameEntry Game(string exe, string? dir = null) => new()
    {
        Id = "g", Name = "G", ExecutablePath = exe, InstallDir = dir ?? _root, Source = GameSource.Manual,
    };

    [Fact]
    public void AnExistingExecutable_IsSelectedInsideItsFolder()
    {
        var exe = Path.Combine(_root, "Binaries", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, [0]);

        Assert.Equal((exe, true), LibraryViewModel.ResolveInstallLocationTarget(Game(exe)));
    }

    [Fact]
    public void AMissingExecutable_FallsBackToTheInstallFolder() =>
        Assert.Equal((_root, false), LibraryViewModel.ResolveInstallLocationTarget(Game(Path.Combine(_root, "gone.exe"))));

    [Fact]
    public void AnExecutablePathThatIsAFolder_IsNotSelectedAsIfItWereAFile()
    {
        // A packaged game with no confirmed exe records its install root as ExecutablePath.
        Assert.Equal((_root, false), LibraryViewModel.ResolveInstallLocationTarget(Game(_root)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankExecutablePath_FallsBackToTheInstallFolder(string exe) =>
        Assert.Equal((_root, false), LibraryViewModel.ResolveInstallLocationTarget(Game(exe)));

    [Fact]
    public void NeitherExisting_IsNull() =>
        Assert.Null(LibraryViewModel.ResolveInstallLocationTarget(Game(Path.Combine(_root, "x.exe"), Path.Combine(_root, "no-such-dir"))));
}
