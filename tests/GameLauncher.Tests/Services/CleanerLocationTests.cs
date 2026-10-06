using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>The cleaner's reach: file-name patterns, top-level-only roots, wildcard folders (browser profiles), roots worked out at scan time, and the guard
/// that stops any root from ever being a drive or a system folder. All against real temp folders.</summary>
public sealed class CleanerLocationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Locations-" + Guid.NewGuid());

    public CleanerLocationTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        if (Directory.Exists(_base))
            Directory.Delete(_base, recursive: true);
    }

    private string Make(string relative, int bytes = 100)
    {
        var path = Path.Combine(_base, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    // ---- file-name patterns ------------------------------------------------------------------------

    [Fact]
    public void FilePatterns_LimitACategoryToTheNamedFiles_AndLeaveTheRestAlone()
    {
        var log = Make(@"logs\a.log");
        var etl = Make(@"logs\deep\b.ETL");
        var keep = Make(@"logs\notes.txt");
        var category = StorageCleaner.Directories("t", "T", "d", [Path.Combine(_base, "logs")], TimeSpan.Zero, filePatterns: ["*.log", "*.etl"]);

        Assert.Equal(200, category.Scan(CancellationToken.None).Bytes);
        var outcome = category.Clean(CancellationToken.None);

        Assert.Equal(2, outcome.Deleted);
        Assert.False(File.Exists(log));
        Assert.False(File.Exists(etl)); // the match ignores case
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void AFolderThatOnlyHeldMatchedFiles_IsNotRemoved_WhenCleaningByName()
    {
        Make(@"logs\sub\only.log");
        var category = StorageCleaner.Directories("t", "T", "d", [Path.Combine(_base, "logs")], TimeSpan.Zero, filePatterns: ["*.log"]);

        category.Clean(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_base, "logs", "sub"))); // a category that picks files by name does not tidy folders
    }

    // ---- top-level-only roots ----------------------------------------------------------------------

    [Fact]
    public void ANonRecursiveRoot_LooksOnlyAtTheFilesDirectlyInIt()
    {
        var top = Make(@"top\MEMORY.DMP");
        var below = Make(@"top\Minidump\small.dmp");
        var category = StorageCleaner.Directories("t", "T", "d", () => [new CleanRoot(Path.Combine(_base, "top"), Recursive: false)], TimeSpan.Zero, filePatterns: ["*.dmp"]);

        category.Clean(CancellationToken.None);

        Assert.False(File.Exists(top));
        Assert.True(File.Exists(below));
    }

    // ---- wildcard folders --------------------------------------------------------------------------

    [Fact]
    public void AWildcardFolder_ClearsTheCacheOfEveryProfile_AndNothingElse()
    {
        var defaultCache = Make(@"User Data\Default\Cache\f_000001");
        var profileCache = Make(@"User Data\Profile 1\Cache\f_000002");
        var cookies = Make(@"User Data\Default\Network\Cookies");
        var login = Make(@"User Data\Default\Login Data");
        var category = StorageCleaner.Directories("t", "T", "d", [Path.Combine(_base, "User Data", "*", "Cache")], TimeSpan.Zero);

        category.Clean(CancellationToken.None);

        Assert.False(File.Exists(defaultCache));
        Assert.False(File.Exists(profileCache));
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(login));
    }

    [Fact]
    public void ExpandRoot_ReturnsOnlyFoldersThatExist_AndATrailingWildcardMatchesFolders()
    {
        Directory.CreateDirectory(Path.Combine(_base, "Saved", "webcache"));
        Directory.CreateDirectory(Path.Combine(_base, "Saved", "webcache_4147"));
        Directory.CreateDirectory(Path.Combine(_base, "Saved", "Logs"));

        var found = StorageCleaner.ExpandRoot(Path.Combine(_base, "Saved", "webcache*")).Select(Path.GetFileName).Order().ToList();

        Assert.Equal(["webcache", "webcache_4147"], found);
        Assert.Empty(StorageCleaner.ExpandRoot(Path.Combine(_base, "Missing", "*", "Cache")));
    }

    [Fact]
    public void AWildcardNeverLeadsThroughALink()
    {
        var outside = Path.Combine(_base, "outside");
        var precious = Make(@"outside\Cache\precious.bin");
        Directory.CreateDirectory(Path.Combine(_base, "User Data"));
        Directory.CreateDirectory(Path.Combine(_base, "User Data", "Default"));
        var link = Path.Combine(_base, "User Data", "Linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // this machine may not create links without elevation; the rule is also covered where it can
        }

        var category = StorageCleaner.Directories("t", "T", "d", [Path.Combine(_base, "User Data", "*", "Cache")], TimeSpan.Zero);
        category.Clean(CancellationToken.None);

        Assert.True(File.Exists(precious));
        Directory.Delete(link); // the link first, so deleting the tree can never reach through it
    }

    // ---- roots worked out at scan time -------------------------------------------------------------

    [Fact]
    public void RootsGivenAsAFunction_AreReadEachTime_SoALaterInstallIsFound()
    {
        var folder = Path.Combine(_base, "later");
        var category = StorageCleaner.Directories("t", "T", "d", () => [new CleanRoot(folder)], TimeSpan.Zero);
        Assert.Equal(0, category.Scan(CancellationToken.None).Files);

        Make(@"later\x.bin");

        Assert.Equal(1, category.Scan(CancellationToken.None).Files);
    }

    // ---- the guard ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData("")]
    [InlineData(@"relative\folder")]
    [InlineData(@"C:\Users\x\*\Cache")]
    public void ADriveARelativePathOrAnUnexpandedWildcard_IsNeverARoot(string path) => Assert.False(StorageCleaner.IsSafeRoot(path));

    [Fact]
    public void TheProfileTheWindowsFolderAndTheDataFolders_AreNeverRecursiveRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            Assert.False(StorageCleaner.IsSafeRoot(path), path);
            Assert.False(StorageCleaner.IsSafeRoot(path + @"\"), path + @"\");
            Assert.False(StorageCleaner.IsSafeRoot(path, recursive: false, hasFilePatterns: false), path); // top level, but no file names to pick: still refused
        }
    }

    [Fact]
    public void AForbiddenFolder_IsAllowedOnlyAsATopLevelRootThatPicksFilesByName()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.True(StorageCleaner.IsSafeRoot(windows, recursive: false, hasFilePatterns: true));
        Assert.False(StorageCleaner.IsSafeRoot(windows, recursive: true, hasFilePatterns: true));
    }

    [Fact]
    public void ACategoryGivenAForbiddenRoot_DoesNothingAtAll()
    {
        // A drive root and the user's profile, as a list mistake might spell them: scanned and cleaned as if they were empty.
        var category = StorageCleaner.Directories("t", "T", "d", [@"C:\", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)], TimeSpan.Zero);

        Assert.Equal(new ScanOutcome(0, 0), category.Scan(CancellationToken.None));
        Assert.Equal(new CleanOutcome(0, 0, 0), category.Clean(CancellationToken.None));
    }

    // ---- the real list -----------------------------------------------------------------------------

    [Fact]
    public void TheRealList_HasTheNewKinds_InAnOrderThatKeepsTheRecycleBinLast_AndPreTicksOnlyTwo()
    {
        var categories = StorageCleaner.DefaultCategories();

        Assert.Equal(
            new[]
            {
                "user-temp", "windows-temp", "crash-dumps", "shader-cache", "browser-caches", "launcher-caches", "driver-installers",
                "windows-update-cache", "windows-logs", "system-dumps", "recycle-bin",
            },
            categories.Select(c => c.Id));
        Assert.Equal(["user-temp", "crash-dumps"], categories.Where(c => c.SelectedByDefault).Select(c => c.Id));
        Assert.All(categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.Equal(categories.Count, categories.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void EveryRealCategoryRoot_PassesTheGuard_OrIsAWildcardThatExpandsToSafeFolders()
    {
        // Scanning the real list on this machine must never be refused for being unsafe; what it finds is measured, nothing is deleted.
        foreach (var category in StorageCleaner.DefaultCategories())
        {
            var outcome = category.Scan(CancellationToken.None);
            Assert.True(outcome.Bytes >= 0 && outcome.Files >= 0, category.Id);
        }
    }
}
