using System.Diagnostics;
using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>A [Fact] that only runs where this process can actually create an NTFS junction (mklink /J -
/// unlike a symlink, this needs no admin/Developer-Mode privilege on a normal Windows install, but a
/// locked-down CI/sandbox account could still lack it). Skipped, not failed, where it can't - the loop
/// test needs a REAL reparse point; there is no meaningful way to fake FileAttributes.ReparsePoint for a
/// directory Directory.EnumerateDirectories itself is about to walk into.</summary>
public sealed class FactRequiresJunctionSupportAttribute : FactAttribute
{
    public FactRequiresJunctionSupportAttribute()
    {
        if (!JunctionSupport.IsSupported.Value)
            Skip = "This account/sandbox can't create NTFS junctions (mklink /J failed) - the loop-safety test needs a real reparse point.";
    }
}

internal static class JunctionSupport
{
    public static readonly Lazy<bool> IsSupported = new(Probe);

    private static bool Probe()
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), "GameLauncherTests_JunctionProbe_" + Guid.NewGuid());
        var target = Path.Combine(probeRoot, "target");
        var link = Path.Combine(probeRoot, "link");
        try
        {
            Directory.CreateDirectory(target);
            return XboxExeSearchTests.TryCreateJunction(link, target);
        }
        finally
        {
            // Directory.Delete(path, recursive: true) throws UnauthorizedAccessException when the tree
            // contains a junction - it walks INTO the reparse point rather than just unlinking it. The
            // junction itself must be removed first, non-recursively, before the recursive delete below
            // can succeed at all.
            try { Directory.Delete(link, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(probeRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A [Fact] that only runs where this process can actually deny its own access to a directory
/// via icacls (a locked-down CI/sandbox account could lack the rights to change ACLs at all, even on its
/// own temp files). Skipped, not failed, where it can't.</summary>
public sealed class FactRequiresIcaclsDenySupportAttribute : FactAttribute
{
    public FactRequiresIcaclsDenySupportAttribute()
    {
        if (!IcaclsDenySupport.IsSupported.Value)
            Skip = "This account/sandbox can't change its own ACLs via icacls - the inaccessible-directory test needs a real access-denied directory.";
    }
}

internal static class IcaclsDenySupport
{
    public static readonly Lazy<bool> IsSupported = new(Probe);

    private static bool Probe()
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), "GameLauncherTests_IcaclsProbe_" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(probeRoot);
            if (!XboxExeSearchTests.TryDenyListDirectory(probeRoot))
                return false;
            XboxExeSearchTests.TryRestoreListDirectory(probeRoot);
            return true;
        }
        finally
        {
            try { Directory.Delete(probeRoot, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// XboxExeSearch.FindLargestExe - the layout-independent (no maxDepth), budget-and-cancellation-bound,
/// loop-safe fallback search used only when a game's own manifest metadata named no executable. Exercises
/// exactly the scenarios called out for this change: executables deeper than five levels, larger helper
/// executables that must NOT win over exclusion rules, inaccessible directories, junction loops, and
/// cancellation.
/// </summary>
public class XboxExeSearchTests : IDisposable
{
    private readonly string _root;

    public XboxExeSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests_XboxExeSearch_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string WriteExe(string relativePath, int sizeBytes)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[sizeBytes]);
        return fullPath;
    }

    // ---- Depth independence ------------------------------------------------------------------------

    [Fact]
    public void AnExecutableTenLevelsDeep_IsFound_NoMaxDepthAtAll()
    {
        // Explicitly deeper than the old fixed maxDepth of 5 that every OTHER scanner still uses, let
        // alone Xbox's old maxDepth: 1 - proving this search really is depth-independent, not just
        // "deeper than before".
        var expected = WriteExe(@"a\b\c\d\e\f\g\h\i\j\Game.exe", 50_000_000);

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None);

        Assert.Equal(XboxExeSearchOutcome.Found, result.Outcome);
        Assert.Equal(expected, result.ExePath);
    }

    [Fact]
    public void ALargerHelperExecutable_DoesNotWin_OverAnExcludedName()
    {
        // "Larger helper executables" - a big installer/redist helper sitting next to the small real
        // game exe must not be picked just because it's bigger; GameExeFinder's shared exclude list
        // (reused here via IsExcludedExeName, not a second copy of it) is what actually prevents that.
        var realExe = WriteExe(@"Content\Game.exe", 20_000_000);
        WriteExe(@"Content\vcredist_x64.exe", 90_000_000);

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None);

        Assert.Equal(XboxExeSearchOutcome.Found, result.Outcome);
        Assert.Equal(realExe, result.ExePath);
    }

    // ---- Budget: NotFound vs Incomplete are different outcomes -------------------------------------

    [Fact]
    public void NothingAnywhereInTheWholeReachableTree_IsNotFound_AGenuineNegative()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Content"));

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None);

        Assert.Equal(XboxExeSearchOutcome.NotFound, result.Outcome);
        Assert.Null(result.ExePath);
    }

    [Fact]
    public void RunningOutOfDirectoryBudget_BeforeFindingAnything_IsIncomplete_NotNotFound()
    {
        // The exe sits in a directory this budget can never reach - the result must say "incomplete",
        // never "not found": absence-because-we-stopped-looking is not the same fact as absence-because-
        // we-looked-everywhere, and a caller that conflated them would silently drop a real game.
        WriteExe(@"a\b\c\d\e\Game.exe", 50_000_000);

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None, maxDirectoriesToVisit: 2);

        Assert.Equal(XboxExeSearchOutcome.Incomplete, result.Outcome);
        Assert.Null(result.ExePath);
    }

    [Fact]
    public void RunningOutOfDirectoryBudget_AfterAlreadyFindingSomething_IsStillIncomplete_NotConfidentlyFound()
    {
        // Finding SOMETHING before the budget ran out is not the same fact as "searched the whole tree
        // and this is the best candidate" - a larger, unvisited exe could still exist in the unexamined
        // branch (OtherStuff.exe here, deliberately bigger). Outcome must stay Incomplete so a caller
        // never treats a merely-provisional find as a settled, confident result; ProvisionalExePath still
        // carries what was found, so the caller isn't left with nothing either.
        var expected = WriteExe("Game.exe", 50_000_000);
        WriteExe(@"a\b\c\d\e\OtherStuff.exe", 90_000_000); // larger, but unreachable at this budget

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None, maxDirectoriesToVisit: 2);

        Assert.Equal(XboxExeSearchOutcome.Incomplete, result.Outcome);
        Assert.Null(result.ExePath);
        Assert.Equal(expected, result.ProvisionalExePath);
    }

    [Fact]
    public void RunningOutOfTimeBudget_IsIncomplete_EvenWithAnUnlimitedDirectoryBudget()
    {
        WriteExe(@"a\b\Game.exe", 50_000_000);

        var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None,
            maxDirectoriesToVisit: 1_000_000, timeBudget: TimeSpan.Zero);

        Assert.Equal(XboxExeSearchOutcome.Incomplete, result.Outcome);
    }

    // ---- Inaccessible directories: skipped, not fatal -----------------------------------------------

    [FactRequiresIcaclsDenySupport]
    public void AnInaccessibleSubdirectory_IsSkipped_SiblingsAreStillSearched()
    {
        var lockedDir = Path.Combine(_root, "Locked");
        Directory.CreateDirectory(lockedDir);
        var expected = WriteExe(@"Sibling\Game.exe", 50_000_000);

        Assert.True(TryDenyListDirectory(lockedDir), "Failed to lock down the directory the test itself depends on.");

        try
        {
            var result = XboxExeSearch.FindLargestExe(_root, CancellationToken.None);

            Assert.Equal(XboxExeSearchOutcome.Found, result.Outcome);
            Assert.Equal(expected, result.ExePath);
        }
        finally
        {
            // Restore access before the fixture's own directory-delete runs, or cleanup itself fails.
            TryRestoreListDirectory(lockedDir);
        }
    }

    // ---- Loops: a real NTFS junction must never hang or crash the search ---------------------------

    [FactRequiresJunctionSupport]
    public void AJunctionPointingBackAtAnAncestor_NeverCausesAnInfiniteLoop()
    {
        // The exe deliberately does NOT sit in _root itself: if it did, it would be found while
        // processing _root - the very first directory dequeued - before the walk ever reaches the
        // junction at all, which would make this test pass regardless of whether the loop guard works.
        // Forcing it to be found only via a SECOND, sibling branch means the walk has to actually get
        // past "Loop" (and therefore past the junction inside it) to succeed.
        var expected = WriteExe(@"RealGame\Game.exe", 50_000_000);
        var loopDir = Path.Combine(_root, "Loop");
        Directory.CreateDirectory(loopDir);
        var linkPath = Path.Combine(loopDir, "BackToRoot");

        Assert.True(TryCreateJunction(linkPath, _root), "Failed to create the junction the test itself depends on.");

        var visited = 0;
        XboxExeSearch.OnDirectoryVisitedForTest = _ => visited++;

        try
        {
            // The real safety net: if XboxExeSearch's loop guard were broken, this call would hang
            // forever and the whole test process would eventually be killed by the test runner's own
            // timeout - a hang, not a clean assertion failure. Running it on a background task with an
            // explicit wait turns that into an actual, reportable failure instead.
            var task = Task.Run(() => XboxExeSearch.FindLargestExe(_root, CancellationToken.None));
            var completed = task.Wait(TimeSpan.FromSeconds(10));

            Assert.True(completed, "XboxExeSearch did not terminate within 10s - the junction loop guard is broken.");
            Assert.Equal(XboxExeSearchOutcome.Found, task.Result.Outcome);
            Assert.Equal(expected, task.Result.ExePath);

            // The tree here (_root, RealGame, Loop, and the junction itself) has only 4 directories
            // total. A guard that actually skips reparse points visits exactly that many; a guard that's
            // missing would instead chase the junction for dozens of levels before Windows' own MAX_PATH
            // limit eventually, incidentally, stops it - "eventually terminates" is not the same
            // guarantee as "never descends into the loop at all", and this is the assertion that tells
            // the two apart.
            Assert.True(visited < 10, $"Visited {visited} directories for a 4-directory tree - the walk followed the junction instead of skipping it.");
        }
        finally
        {
            XboxExeSearch.OnDirectoryVisitedForTest = null;
            // _root now contains a junction pointing back at ITSELF - Dispose()'s own recursive delete
            // of _root would hit the exact UnauthorizedAccessException JunctionSupport.Probe's remarks
            // explain, so the junction has to be unlinked (non-recursively - never touching its target,
            // which here IS _root) before that runs.
            try { Directory.Delete(linkPath, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- Cancellation ---------------------------------------------------------------------------------

    [Fact]
    public void APreCancelledToken_StopsImmediately_ThrowsOperationCanceled()
    {
        WriteExe("Game.exe", 50_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => XboxExeSearch.FindLargestExe(_root, cts.Token));
    }

    [Fact]
    public void CancellingMidWalk_StopsTheSearch_RatherThanRunningToCompletion()
    {
        for (var i = 0; i < 50; i++)
            WriteExe($@"dir{i}\Game{i}.exe", 1_000);

        // Deterministic, not timing-based: cancel from inside the test-only per-directory hook, exactly
        // on the 3rd directory visited - proves the walk genuinely stops partway through, not just that
        // a pre-cancelled token is honored before starting (that's APreCancelledToken_..., above).
        using var cts = new CancellationTokenSource();
        var visited = 0;
        XboxExeSearch.OnDirectoryVisitedForTest = _ =>
        {
            if (++visited == 3)
                cts.Cancel();
        };

        try
        {
            var ex = Record.Exception(() => XboxExeSearch.FindLargestExe(_root, cts.Token, maxDirectoriesToVisit: 1_000_000));

            Assert.IsType<OperationCanceledException>(ex);
            Assert.True(visited < 50, "The walk visited every directory before cancellation took effect - it isn't actually checking the token mid-walk.");
        }
        finally
        {
            XboxExeSearch.OnDirectoryVisitedForTest = null;
        }
    }

    [Fact]
    public void CancellingWhileEnumeratingOneLargeDirectory_StopsBeforeItFinishes_NotJustBetweenDirectories()
    {
        // A single directory holding more entries than one periodic check interval - proving cancellation
        // is actually observed WHILE walking through this ONE directory's own file list, not merely at
        // the top of the outer per-directory loop (which a directory this large would never even reach a
        // second iteration of, since there IS no second directory here - only proof of an in-enumeration
        // check can explain an early exit).
        const int fileCount = 600; // comfortably more than one EnumerationCheckInterval (256)
        for (var i = 0; i < fileCount; i++)
            WriteExe($"Game{i}.exe", 1_000);

        using var cts = new CancellationTokenSource();
        // Cancels the instant the FIRST periodic in-enumeration check fires - deterministic, no timing
        // race, and impossible to satisfy by accident if that check didn't exist at all (there'd be
        // nothing to invoke this hook before the whole 600-file loop finished on its own).
        XboxExeSearch.OnEnumerationCheckForTest = () => cts.Cancel();

        try
        {
            var ex = Record.Exception(() => XboxExeSearch.FindLargestExe(_root, cts.Token, maxDirectoriesToVisit: 1_000_000));

            Assert.IsType<OperationCanceledException>(ex);
        }
        finally
        {
            XboxExeSearch.OnEnumerationCheckForTest = null;
        }
    }

    internal static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(5000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool TryDenyListDirectory(string dir) => RunIcacls($"\"{dir}\" /deny \"{Environment.UserName}:(RD)\"");

    internal static void TryRestoreListDirectory(string dir) => RunIcacls($"\"{dir}\" /remove:d \"{Environment.UserName}\"");

    private static bool RunIcacls(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
