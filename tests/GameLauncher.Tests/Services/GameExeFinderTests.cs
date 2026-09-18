using System.IO;
using GameLauncher.Services;

namespace GameLauncher.Tests.Services;

/// <summary>
/// Exercises GameExeFinder.FindLargestExe against real temp directory trees - it does real filesystem
/// walking with no injectable seam, the same testing approach ManualFolderScanner's own real-world
/// design already relies on.
/// </summary>
public class GameExeFinderTests : IDisposable
{
    private readonly string _root;

    public GameExeFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests_ExeFinder_" + Guid.NewGuid());
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

    [Fact]
    public void RealAWayOutLayout_ExecutableThreeLevelsDeep_IsFound()
    {
        // The exact confirmed real-world layout: <root>\Haze1\Binaries\Win64\AWayOut.exe - three
        // subfolder levels below the install root, one deeper than the old default maxDepth of 2 ever
        // reached (root itself is depth 0, so Haze1=1, Binaries=2, Win64=3).
        var expected = WriteExe(@"Haze1\Binaries\Win64\AWayOut.exe", 50_000_000);

        var found = GameExeFinder.FindLargestExe(_root);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void FriendsPassBuildPresent_AndLarger_RealExeIsStillSelected_NotTrustingFileSizeAlone()
    {
        // A Way Out ships EA's "Friend's Pass" trial build (AWayOut_friend.exe) right next to the real,
        // owned game's exe - deliberately made LARGER here than the real exe, so a pure "pick the
        // biggest file" heuristic (the previous, and still the fallback, selection rule) would pick the
        // wrong one if exclusion didn't filter it out first. Passed the same way EaScanner itself
        // passes it - via extraExcludePatterns, not a global default (see the next test).
        var realExe = WriteExe(@"Haze1\Binaries\Win64\AWayOut.exe", 50_000_000);
        WriteExe(@"Haze1\Binaries\Win64\AWayOut_friend.exe", 90_000_000);

        var found = GameExeFinder.FindLargestExe(_root, extraExcludePatterns: [GameExeFinder.EaFriendsPassExcludePattern]);

        Assert.Equal(realExe, found);
    }

    [Fact]
    public void OrdinaryGameNamesContainingFriend_RemainDetectable_AtTheDefaultScannerLevel()
    {
        // The regression this exists for: "friend" used to be a GLOBAL default exclude pattern applied
        // to every scanner (Xbox, Ubisoft, Manual, PublisherUninstallScanner), which would have silently
        // rejected a perfectly legitimate game exe like this one. Called with no extraExcludePatterns -
        // the EA-only "_friend" pattern (see EaFriendsPassExcludePattern) is opt-in, not global.
        var expected = WriteExe("FriendlyGame.exe", 50_000_000);

        var found = GameExeFinder.FindLargestExe(_root);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void OrdinaryGameNamesContainingFriend_RemainDetectable_EvenWithinEaScannersOwnScope()
    {
        // "_friend" (with the underscore) is narrow enough that even EA's own extra pattern doesn't
        // reject an unrelated game whose name happens to contain "friend" without that exact suffix
        // shape - "MyFriendsList.exe" has no "_friend" substring (no underscore immediately before it).
        var expected = WriteExe("MyFriendsList.exe", 50_000_000);

        var found = GameExeFinder.FindLargestExe(_root, extraExcludePatterns: [GameExeFinder.EaFriendsPassExcludePattern]);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void ExecutableBeyondMaxDepth_IsNotFound_SearchStaysBounded()
    {
        // Proves the search is still genuinely BOUNDED, not just "deep enough for this one case" -
        // maxDepth: 2 here should not reach a level-3 executable.
        WriteExe(@"a\b\c\toodeep.exe", 50_000_000);

        var found = GameExeFinder.FindLargestExe(_root, maxDepth: 2);

        Assert.Null(found);
    }

    [Fact]
    public void DefaultDepth_FindsExecutableAtDepthFive_ButNotDepthSix()
    {
        var atFive = WriteExe(@"a\b\c\d\e\atfive.exe", 50_000_000);
        var found = GameExeFinder.FindLargestExe(_root);
        Assert.Equal(atFive, found);

        // A separate tree so the depth-6 item can't win "largest" purely by being bigger than atfive.
        var deeperRoot = Path.Combine(_root, "deeper-case");
        Directory.CreateDirectory(deeperRoot);
        var tooDeepDir = Path.Combine(deeperRoot, "a", "b", "c", "d", "e", "f");
        Directory.CreateDirectory(tooDeepDir);
        File.WriteAllBytes(Path.Combine(tooDeepDir, "atsix.exe"), new byte[90_000_000]);

        var foundInDeeperRoot = GameExeFinder.FindLargestExe(deeperRoot);
        Assert.Null(foundInDeeperRoot); // depth 6 is beyond the default maxDepth of 5
    }

    [Fact]
    public void TrialAndAnticheatBuilds_AreStillExcluded_RegressionForTheOriginalFc26Fix()
    {
        var realExe = WriteExe("FC26.exe", 50_000_000);
        WriteExe("FC26_Trial.exe", 90_000_000);
        WriteExe("EAAntiCheat.GameServiceLauncher.exe", 80_000_000);

        var found = GameExeFinder.FindLargestExe(_root);

        Assert.Equal(realExe, found);
    }
}
