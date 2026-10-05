using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>Finding games no launcher installed, and the drive switch that keeps whole disks out of every search.</summary>
public sealed class DiskGameFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Disk-" + Guid.NewGuid());

    public DiskGameFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Exe(string folder, string name, int kb = 400)
    {
        var path = Path.Combine(folder, name);
        using var stream = File.Create(path);
        stream.SetLength(kb * 1024L);
        return path;
    }

    private static void Touch(string folder, string name) => File.WriteAllText(Path.Combine(folder, name), "x");

    private static IReadOnlySet<string> NoKnownDirs() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A small disk: a Unity game, a repack with Steamworks, an Unreal game whose real exe is in Binaries\Win64, some ordinary
    /// software, a launcher's own folder and a folder Windows keeps for itself.</summary>
    private void BuildDisk()
    {
        var unity = Dir("Games", "Hollow Cave");
        Exe(unity, "HollowCave.exe", 650);
        Touch(unity, "UnityPlayer.dll");
        Dir("Games", "Hollow Cave", "HollowCave_Data");

        var repack = Dir("Repacks", "Star Drifter");
        Exe(repack, "drifter.exe", 900);
        Touch(repack, "steam_api64.dll");

        var unreal = Dir("Games", "Iron Coast");
        Exe(unreal, "IronCoast.exe", 80); // the root stub is small
        Dir("Games", "Iron Coast", "Engine");
        Dir("Games", "Iron Coast", "IronCoast", "Binaries", "Win64");
        Dir("Games", "Iron Coast", "IronCoast", "Content");
        Exe(Path.Combine(unreal, "IronCoast", "Binaries", "Win64"), "IronCoast-Win64-Shipping.exe", 5000);

        var tool = Dir("Tools", "PhotoThing");
        Exe(tool, "photothing.exe", 3000);
        Dir("Tools", "PhotoThing", "Data");
        Dir("Tools", "PhotoThing", "Assets");

        var steam = Dir("Steam");
        Exe(steam, "steam.exe", 3000);
        Touch(steam, "steam_api64.dll");

        var windows = Dir("Windows", "System32", "Game");
        Exe(windows, "game.exe");
        Touch(windows, "UnityPlayer.dll");
    }

    private static string Names(IEnumerable<string> folders) =>
        string.Join(",", folders.Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void ASweep_FindsEveryEnginesGame_AndNothingElse()
    {
        BuildDisk();

        var found = DiskGameFinder.Sweep([_root], NoKnownDirs(), CancellationToken.None);

        Assert.Equal("Hollow Cave,Iron Coast,Star Drifter", Names(found));
    }

    [Fact]
    public void AFolderTheLaunchersAlreadyReported_IsNotEntered()
    {
        BuildDisk();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(_root, "Games", "Hollow Cave") };

        var found = DiskGameFinder.Sweep([_root], known, CancellationToken.None);

        Assert.Equal("Iron Coast,Star Drifter", Names(found));
    }

    [Fact]
    public void AFoundGame_GetsItsRealExe_AndTheNameOfItsFolder()
    {
        BuildDisk();
        var games = DiskGameFinder.Resolve(DiskGameFinder.Sweep([_root], NoKnownDirs(), CancellationToken.None), new DriveFilter([]), NoKnownDirs());

        var unreal = games.Single(g => g.Name == "Iron Coast");
        Assert.EndsWith(@"IronCoast\Binaries\Win64\IronCoast-Win64-Shipping.exe", unreal.ExecutablePath);
        Assert.Equal(GameSource.Manual, unreal.Source); // "No launcher"
        Assert.Equal(Path.Combine(_root, "Games", "Iron Coast"), unreal.InstallDir);
        Assert.StartsWith("manual-", unreal.Id);        // the same id a watched folder would give it, so the two merge
    }

    [Fact]
    public void AUnityFolder_PrefersTheExeThatHasItsOwnDataFolder()
    {
        var game = Dir("Games", "Unity Thing");
        Exe(game, "UnityThing.exe", 650);
        Exe(game, "BiggerHelperTool.exe", 3000);
        Dir("Games", "Unity Thing", "UnityThing_Data");

        var found = DiskGameFinder.Resolve([game], new DriveFilter([]), NoKnownDirs());

        Assert.EndsWith("UnityThing.exe", found.Single().ExecutablePath);
    }

    [Fact]
    public void AFolderWithGameSignsButNoGameExe_IsNotAGame()
    {
        var folder = Dir("Data", "Assets only");
        Touch(folder, "UnityPlayer.dll");
        Exe(folder, "unins000.exe", 900); // an uninstaller is never the game

        Assert.Empty(DiskGameFinder.Sweep([_root], NoKnownDirs(), CancellationToken.None));
    }

    [Theory]
    [InlineData(new[] { "game.exe", "UnityPlayer.dll" }, new string[0], 1, 0)]
    [InlineData(new[] { "game.exe" }, new[] { "game_Data" }, 1, 0)]
    [InlineData(new[] { "game.exe" }, new[] { "other_Data" }, 0, 0)]                  // "_Data" counts only next to its own exe
    [InlineData(new[] { "game.exe", "steam_api64.dll" }, new string[0], 1, 0)]
    [InlineData(new[] { "game.exe", "pack.pck" }, new string[0], 1, 0)]               // Godot
    [InlineData(new[] { "game.exe", "data.win" }, new string[0], 1, 0)]               // GameMaker
    [InlineData(new[] { "game.exe" }, new[] { "EasyAntiCheat" }, 1, 0)]
    [InlineData(new[] { "game.exe" }, new[] { "Binaries", "Content" }, 1, 0)]         // Unreal
    [InlineData(new[] { "game.exe" }, new[] { "Engine", "Content" }, 1, 1)]
    [InlineData(new[] { "app.exe", "unins000.exe" }, new[] { "Data", "Assets" }, 0, 1)] // ordinary software stays under the bar
    [InlineData(new[] { "app.exe", "level1.pak" }, new[] { "Redist" }, 0, 2)]
    public void TheSignsAFolderShows_AreCountedByStrength(string[] files, string[] folders, int strong, int weak)
    {
        var (s, w) = DiskGameFinder.Score(files, folders);

        Assert.Equal(strong, s);
        Assert.Equal(weak, w);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(0, 0, false)]
    public void OneStrongSign_OrTwoWeakOnes_IsEnough(int strong, int weak, bool game) =>
        Assert.Equal(game, DiskGameFinder.LooksLikeAGame(strong, weak));

    // ---- remembering a sweep ---------------------------------------------------------------------------------------------

    [Fact]
    public void ARecentSweepOfTheSameDrives_IsReused_ButNotWhenForced_StaleOrForOtherDrives()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        string[] folders = ["x"];

        Assert.True(DiskGameFinder.CanReuse(folders, now.AddHours(-1), "C:|D:", "C:|D:", now, force: false));
        Assert.False(DiskGameFinder.CanReuse(folders, now.AddHours(-1), "C:|D:", "C:|D:", now, force: true));   // the user's own rescan
        Assert.False(DiskGameFinder.CanReuse(folders, now.AddHours(-13), "C:|D:", "C:|D:", now, force: false)); // too old
        Assert.False(DiskGameFinder.CanReuse(folders, now.AddHours(-1), "C:", "C:|D:", now, force: false));     // a drive appeared or came back
        Assert.False(DiskGameFinder.CanReuse(null, null, null, "C:|D:", now, force: false));                    // never swept
        Assert.False(DiskGameFinder.CanReuse(folders, now.AddHours(2), "C:|D:", "C:|D:", now, force: false));   // clock went backwards
    }

    [Fact]
    public void Find_SweepsOnTheFirstScan_ThenOnlyRechecksTheRememberedFolders_UntilForced()
    {
        BuildDisk();
        var filter = new DriveFilter([]);
        var now = DateTime.UtcNow;

        var (first, sweep) = DiskGameFinder.Find(filter, NoKnownDirs(), null, null, null, force: false, CancellationToken.None, [_root], now);
        Assert.NotNull(sweep);
        Assert.Equal(3, first.Count);

        // A game added after the sweep is not seen by a re-check (that is the point: no walk) ...
        var late = Dir("Games", "Late One");
        Exe(late, "late.exe");
        Touch(late, "UnityPlayer.dll");
        var (second, none) = DiskGameFinder.Find(filter, NoKnownDirs(), sweep!.Folders, sweep.SweptUtc, sweep.Signature, force: false,
            CancellationToken.None, [_root], now.AddHours(1));
        Assert.Null(none);
        Assert.Equal(3, second.Count);

        // ... but a rescan the user asked for walks again and finds it.
        var (third, again) = DiskGameFinder.Find(filter, NoKnownDirs(), sweep.Folders, sweep.SweptUtc, sweep.Signature, force: true,
            CancellationToken.None, [_root], now.AddHours(1));
        Assert.NotNull(again);
        Assert.Equal(4, third.Count);
    }

    [Fact]
    public void ARememberedFolderThatHasBeenDeleted_DropsOutWithoutASweep()
    {
        BuildDisk();
        var filter = new DriveFilter([]);
        var now = DateTime.UtcNow;
        var (_, sweep) = DiskGameFinder.Find(filter, NoKnownDirs(), null, null, null, false, CancellationToken.None, [_root], now);
        Directory.Delete(Path.Combine(_root, "Repacks"), recursive: true);

        var (games, none) = DiskGameFinder.Find(filter, NoKnownDirs(), sweep!.Folders, sweep.SweptUtc, sweep.Signature, false,
            CancellationToken.None, [_root], now);

        Assert.Null(none);
        Assert.DoesNotContain(games, g => g.Name == "Star Drifter");
    }

    // ---- switching drives off --------------------------------------------------------------------------------------------

    [Fact]
    public void AnIgnoredDrive_IsNotSearched_AndItsRememberedGamesAreNotShown()
    {
        BuildDisk();
        var letter = DriveFilter.NormalizeLetter(_root);
        var ignored = new DriveFilter([new IgnoredDrive { Letter = letter }], _ => null);

        Assert.Empty(DiskGameFinder.SearchableDrives(ignored, [_root]));

        // Even a folder remembered from before the drive was switched off stays out.
        var (games, _) = DiskGameFinder.Find(ignored, NoKnownDirs(), [Path.Combine(_root, "Games", "Hollow Cave")], DateTime.UtcNow,
            DiskGameFinder.SignatureOf([]), force: false, CancellationToken.None, [_root]);
        Assert.Empty(games);
    }

    [Fact]
    public void WhenADriveComesBackUnderAnotherLetter_ItIsStillTheOneThatWasIgnored()
    {
        // D: (serial 7) was ignored; the same disk is now E:, and a different disk (serial 9) took D:.
        uint? SerialOf(string root) => root.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? 7u : 9u;
        var filter = new DriveFilter([new IgnoredDrive { Letter = "D:", VolumeSerial = 7 }], SerialOf);

        Assert.True(filter.IsIgnored(@"E:\Games\Foo"));
        Assert.False(filter.IsIgnored(@"D:\Games\Foo"));
    }

    [Fact]
    public void WithoutASerial_TheLetterDecides_AndPathsAreMatchedWhateverTheirCase()
    {
        var filter = new DriveFilter([new IgnoredDrive { Letter = "d:" }], _ => null);

        Assert.True(filter.IsIgnored(@"D:\Games\Foo"));
        Assert.True(filter.IsIgnored(@"d:\"));
        Assert.True(filter.IsIgnored("D:"));
        Assert.False(filter.IsIgnored(@"C:\Games\Foo"));
        Assert.False(filter.IsIgnored(@"\\server\share\Foo")); // no letter, never ignored
        Assert.False(filter.IsIgnored(null));
    }

    [Fact]
    public void WithNothingIgnored_NoQuestionIsEverAskedOfTheDisks()
    {
        var asked = 0;
        var filter = new DriveFilter([], _ => { asked++; return 1; });

        Assert.False(filter.IsIgnored(@"C:\Games\Foo"));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AllDrivesAreAskedAboutOnlyOnce_NoMatterHowManyGamesAreChecked()
    {
        var asked = 0;
        var filter = new DriveFilter([new IgnoredDrive { Letter = "D:", VolumeSerial = 1 }], _ => { asked++; return 2; });

        for (var i = 0; i < 500; i++)
            filter.IsIgnored($@"C:\Games\Game{i}");

        Assert.Equal(1, asked);
    }
}
