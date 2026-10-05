using System.Diagnostics;
using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>The cleaner that deletes files must be provably careful, so these run against real temp folders: only old files go, nothing
/// outside the root is touched (junctions included), and files in use are left alone.</summary>
public sealed class OptimizerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _base = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Optimizer-" + Guid.NewGuid());
    private readonly string _root;

    public OptimizerTests()
    {
        _root = Path.Combine(_base, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // junctions first, so deleting the tree can never reach through one
        foreach (var dir in Directory.Exists(_base) ? Directory.GetDirectories(_base, "*", SearchOption.AllDirectories) : [])
        {
            if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir);
        }

        if (Directory.Exists(_base))
        {
            foreach (var file in Directory.GetFiles(_base, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_base, recursive: true);
        }
    }

    private string Make(string relative, int bytes, double ageHours)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddHours(-ageHours));
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > _root.Length; dir = Path.GetDirectoryName(dir))
            Directory.SetLastWriteTimeUtc(dir, Now.AddHours(-ageHours));
        return path;
    }

    private CleanCategory Category(double minAgeHours = 24, params string[] extraRoots) =>
        StorageCleaner.Directories("t", "Test", "desc", [_root, .. extraRoots], TimeSpan.FromHours(minAgeHours), utcNow: () => Now);

    // ---- scanning ----------------------------------------------------------------------------------

    [Fact]
    public void Scan_CountsOnlyFilesOldEnough_AndDeletesNothing()
    {
        var old = Make("old.tmp", 1000, 48);
        var recent = Make(@"sub\recent.tmp", 500, 2);
        var nested = Make(@"a\b\nested.tmp", 250, 100);

        var outcome = Category().Scan(CancellationToken.None);

        Assert.Equal(1250, outcome.Bytes);
        Assert.Equal(2, outcome.Files);
        Assert.True(File.Exists(old) && File.Exists(recent) && File.Exists(nested));
    }

    [Fact]
    public void AMissingFolder_IsSimplyEmpty()
    {
        var category = StorageCleaner.Directories("t", "Test", "d", [Path.Combine(_base, "nope")], TimeSpan.Zero);

        Assert.Equal(new ScanOutcome(0, 0), category.Scan(CancellationToken.None));
        Assert.Equal(new CleanOutcome(0, 0, 0), category.Clean(CancellationToken.None));
    }

    [Fact]
    public void ACancelledScan_Stops()
    {
        Make("a.tmp", 10, 48);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Category().Scan(cts.Token));
    }

    // ---- cleaning ----------------------------------------------------------------------------------

    [Fact]
    public void Clean_DeletesOldFiles_KeepsRecentOnes_AndNeverTheRootItself()
    {
        var old = Make("old.tmp", 1000, 48);
        var recent = Make("recent.tmp", 500, 2);
        var nestedOld = Make(@"a\b\nested.tmp", 250, 100);

        var outcome = Category().Clean(CancellationToken.None);

        Assert.Equal(new CleanOutcome(1250, 2, 0), outcome);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(nestedOld));
        Assert.True(File.Exists(recent));
        Assert.True(Directory.Exists(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "a")), "Folders left empty by the clean-up are tidied away.");
    }

    [Fact]
    public void Clean_KeepsAFolderThatStillHoldsARecentFile_AndAnEmptyFolderThatIsTooNew()
    {
        Make(@"keep\old.tmp", 10, 48);
        Make(@"keep\recent.tmp", 10, 1);
        var freshEmpty = Path.Combine(_root, "fresh-empty");
        Directory.CreateDirectory(freshEmpty);
        Directory.SetLastWriteTimeUtc(freshEmpty, Now.AddMinutes(-5));

        Category().Clean(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "keep", "recent.tmp")));
        Assert.True(Directory.Exists(freshEmpty), "An empty folder newer than the age limit may belong to a running app.");
    }

    [Fact]
    public void Clean_WithNoAgeLimit_ClearsEverythingInsideButNotTheRoot()
    {
        Make("x.dat", 10, 0);
        Make(@"deep\y.dat", 10, 0);

        var outcome = Category(minAgeHours: 0).Clean(CancellationToken.None);

        Assert.Equal(2, outcome.Deleted);
        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Clean_RemovesReadOnlyFiles_Too()
    {
        var path = Make("locked.tmp", 10, 48);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var outcome = Category().Clean(CancellationToken.None);

        Assert.Equal(1, outcome.Deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileInUse_IsSkippedAndCounted_NotForced()
    {
        var busy = Make("busy.tmp", 100, 48);
        var free = Make("free.tmp", 50, 48);
        using var held = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = Category().Clean(CancellationToken.None);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(50, outcome.FreedBytes);
        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void ALinkInsideTheRoot_IsNeverFollowed_SoNothingOutsideTheRootIsDeleted()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.doc");
        File.WriteAllText(precious, "keep me");
        File.SetLastWriteTimeUtc(precious, Now.AddDays(-400));

        var link = Path.Combine(_root, "link");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false });
        made!.WaitForExit();
        Assert.True(Directory.Exists(link), "The test needs to create a directory junction.");

        var category = Category(minAgeHours: 24);
        var scan = category.Scan(CancellationToken.None);
        var outcome = category.Clean(CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.Deleted);
        Assert.True(File.Exists(precious), "A file reached through a junction must never be deleted.");
        Assert.True(Directory.Exists(link), "The link itself is left alone too.");
    }

    [Fact]
    public void ACancelledClean_StopsBeforeDeleting()
    {
        var path = Make("a.tmp", 10, 48);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Category().Clean(cts.Token));
        Assert.True(File.Exists(path));
    }

    // ---- recycle bin -------------------------------------------------------------------------------

    [Fact]
    public void TheRecycleBin_ReportsItsSize_AndEmptiesThroughTheSuppliedCall()
    {
        var emptied = 0;
        var category = StorageCleaner.RecycleBin(() => (4096, 3), () => { emptied++; return true; });

        Assert.False(category.SelectedByDefault);
        Assert.Equal(new ScanOutcome(4096, 3), category.Scan(CancellationToken.None));
        Assert.Equal(0, emptied); // measuring never empties it

        Assert.Equal(new CleanOutcome(4096, 3, 0), category.Clean(CancellationToken.None));
        Assert.Equal(1, emptied);
    }

    [Fact]
    public void TheRealShellQuery_IsAccepted_AndItsStructMatchesTheSizeWindowsExpects()
    {
        // Windows rejects the call (E_INVALIDARG) unless cbSize is 24; a packed 20-byte struct made every real bin look empty.
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.SizeOf<StorageCleaner.ShQueryRbInfo>());

        var (result, bytes, items) = StorageCleaner.QueryRecycleBinNative();

        Assert.Equal(0, result);
        Assert.True(bytes >= 0 && items >= 0);
    }

    [Fact]
    public void ARecycleBinThatCannotBeEmptied_ReportsNothingFreed()
    {
        var category = StorageCleaner.RecycleBin(() => (4096, 3), () => false);

        Assert.Equal(new CleanOutcome(0, 0, 3), category.Clean(CancellationToken.None));
    }

    // ---- the real list -----------------------------------------------------------------------------

    [Fact]
    public void TheDefaultList_OnlyPreTicksWhatNobodyWouldMiss()
    {
        var categories = StorageCleaner.DefaultCategories();

        Assert.Equal(["user-temp", "windows-temp", "crash-dumps", "shader-cache", "recycle-bin"], categories.Select(c => c.Id));
        Assert.Equal(["user-temp", "crash-dumps"], categories.Where(c => c.SelectedByDefault).Select(c => c.Id));
        Assert.All(categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void TheRealScan_RunsAgainstThisMachine_WithoutDeletingAnything()
    {
        foreach (var category in StorageCleaner.DefaultCategories())
        {
            var outcome = category.Scan(CancellationToken.None);
            Assert.True(outcome.Bytes >= 0 && outcome.Files >= 0, category.Id);
        }
    }

    // ---- memory ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Games\Foo\bin\game.exe", true)]
    [InlineData(@"c:\games\foo\GAME.EXE", true)]
    [InlineData(@"C:\Games\FooBar\game.exe", false)]
    [InlineData(@"C:\Games\Foo", false)] // the folder itself is not a program inside it
    [InlineData(@"D:\Games\Foo\game.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProtectedFolders_MatchWholePathSegmentsOnly(string? path, bool expected) =>
        Assert.Equal(expected, MemoryOptimizer.IsInside(path, [@"C:\Games\Foo"]));

    [Fact]
    public void TheMemoryStatus_ReadsThisMachine()
    {
        var status = new MemoryOptimizer().GetStatus();

        Assert.True(status.TotalBytes > 0);
        Assert.InRange(status.AvailableBytes, 1, status.TotalBytes);
        Assert.InRange(status.UsedFraction, 0, 1);
    }

    [Fact]
    public void TheMemoryText_IsPlain()
    {
        const long gb = 1L << 30;
        var status = new MemoryStatus(16 * gb, 4 * gb);

        Assert.Equal(12 * gb, status.UsedBytes);
        Assert.Equal(0.75, status.UsedFraction);
        Assert.StartsWith("12 GB in use of 16 GB", status.Describe());
        Assert.EndsWith("4 GB available", status.Describe());
        Assert.Equal("Memory details are unavailable right now.", default(MemoryStatus).Describe());
    }

    [Fact]
    public void ATrimResult_NeverReportsNegativeFreedMemory()
    {
        var worse = new MemoryTrimResult(10, 2, 5_000, 3_000);
        Assert.Equal(0, worse.FreedBytes);

        Assert.Equal("Nothing could be trimmed.", new MemoryTrimResult(0, 5, 1, 1).Describe());
        Assert.Contains("trimming 10 apps' memory", worse.Describe());
    }

    // ---- other tools -------------------------------------------------------------------------------

    [Fact]
    public void OtherTools_OpenWhenInstalled_AndOtherwiseOfferTheirDownloadPage()
    {
        var present = new HashSet<string> { @"C:\Program Files\BleachBit\bleachbit.exe" };
        var tools = OptimizeTools.Detect(present.Contains, f => f == Environment.SpecialFolder.ProgramFiles ? @"C:\Program Files" : @"C:\Program Files (x86)");

        var bleach = tools.Single(t => t.Name == "BleachBit");
        Assert.True(bleach.IsInstalled);
        Assert.Equal(@"C:\Program Files\BleachBit\bleachbit.exe", bleach.Target);
        Assert.Equal("Open BleachBit", bleach.ActionText);

        var mem = tools.Single(t => t.Name == "Mem Reduct");
        Assert.False(mem.IsInstalled);
        Assert.Equal("https://github.com/henrypp/memreduct", mem.Target);
        Assert.Equal("Get Mem Reduct", mem.ActionText);

        Assert.Equal("ms-settings:storagesense", tools.Single(t => t.Name == "Windows Storage settings").Target);
        Assert.Equal("cleanmgr.exe", tools.Single(t => t.Name == "Disk Cleanup").Target);
    }

    [Fact]
    public void OtherTools_AreAlsoFoundInTheX86ProgramFiles()
    {
        var present = new HashSet<string> { @"C:\Program Files (x86)\BleachBit\bleachbit.exe" };
        var tools = OptimizeTools.Detect(present.Contains, f => f == Environment.SpecialFolder.ProgramFiles ? @"C:\Program Files" : @"C:\Program Files (x86)");

        Assert.True(tools.Single(t => t.Name == "BleachBit").IsInstalled);
    }
}
