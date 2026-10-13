using System.Diagnostics;
using System.IO;
using GameLauncher.Services;
using GameLauncher.Tests.Services.SessionTracking;

namespace GameLauncher.Tests.Services;

public sealed class ExternalGameDetectorTests
{
    [Fact]
    public void RequiresExactPath_NotJustSameName_AndDisposesEveryHandle()
    {
        var provider = new FakeProcessProvider();
        var correct = provider.AddRunning(1, "Game", @"C:\Games\One\Game.exe");
        var wrong = provider.AddRunning(2, "Game", @"C:\Other\Game.exe");
        var denied = provider.AddRunning(3, "Game", null);
        var helper = provider.AddRunning(4, "Helper", @"C:\Games\One\Helper.exe");
        var result = new ExternalGameDetector(provider).Detect([
            new("one", @"c:\games\one\GAME.exe"), new("two", @"C:\Games\Two\Game.exe")], default);
        Assert.Equal("one", Assert.Single(result).GameId);
        Assert.True(correct.Disposed);
        Assert.True(wrong.Disposed);
        Assert.True(denied.Disposed);
        Assert.False(helper.Disposed); // Wasn't enumerated at all.
    }

    [Fact]
    public void DuplicateProcessesCountOnce_AmbiguousInstallAndUriOnlyAreSkipped()
    {
        var provider = new FakeProcessProvider();
        provider.AddRunning(1, "Game", @"C:\Game\Game.exe");
        provider.AddRunning(2, "Game", @"C:\Game\Game.exe");
        var detector = new ExternalGameDetector(provider);
        Assert.Single(detector.Detect([new("a", @"C:\Game\Game.exe")], default));
        Assert.Empty(detector.Detect([new("a", @"C:\Game\Game.exe"), new("b", @"C:\Game\Game.exe"),
            new("uri", ""), new("relative", "Game.exe")], default));
    }

    [Fact]
    public void ThePrebuiltIndex_GivesTheSameAnswerAsDetectingFromTheListEveryTime()
    {
        var provider = new FakeProcessProvider();
        provider.AddRunning(1, "Game", @"C:\Games\One\Game.exe");
        var detector = new ExternalGameDetector(provider);
        ExternalGameCandidate[] games = [new("one", @"C:\Games\One\Game.exe"), new("two", @"C:\Games\Two\Other.exe"),
            new("dup-a", @"C:\Same\Same.exe"), new("dup-b", @"C:\Same\Same.exe"), new("uri", "")];

        var index = detector.BuildIndex(games);

        Assert.Equal(2, index.PathCount);                       // the shared path and the empty one are not indexed
        Assert.Equal(["Game", "Other"], index.Names.OrderBy(n => n).ToArray());
        Assert.Equal(detector.Detect(games, default).Select(g => g.GameId), detector.Detect(index, default).Select(g => g.GameId));
        Assert.Equal("one", Assert.Single(detector.Detect(index, default)).GameId);
        Assert.Equal("one", Assert.Single(detector.Detect(index, default)).GameId);   // and it is reusable poll after poll
    }

    [Fact]
    public void AnEmptyIndex_NeverEnumeratesProcesses()
    {
        var provider = new FakeProcessProvider();
        var detector = new ExternalGameDetector(provider);

        Assert.Empty(detector.Detect(detector.BuildIndex([new("uri", "")]), default));
    }

    [Fact]
    public void CancellationDoesNotBecomeAnEmptyResult()
    {
        Assert.Throws<OperationCanceledException>(() => new ExternalGameDetector(new FakeProcessProvider())
            .Detect([], new CancellationToken(true)));
    }

    [Fact]
    public void RealProcessAdapter_CanObserveCurrentProcessByItsActualImagePath()
    {
        using var current = Process.GetCurrentProcess();
        var path = current.MainModule!.FileName;
        Assert.Equal("test-host", Assert.Single(new ExternalGameDetector()
            .Detect([new("test-host", path)], default)).GameId);
    }

    // ---- Games with no exe on record (Xbox, EA, anything started by address) and protected processes ---------------------------------

    private sealed class PerFolderNames(Dictionary<string, string[]> byFolder) : GameLauncher.Services.SessionTracking.IExecutableNameDiscovery
    {
        public HashSet<string> GetCandidateNames(string installDir) =>
            new(byFolder.TryGetValue(installDir.TrimEnd(System.IO.Path.DirectorySeparatorChar), out var names) ? names : [], StringComparer.OrdinalIgnoreCase);
    }

    private static string TempFolder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "axis-ext-" + Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AGameWithNoExeOnRecord_IsFoundByRunningFromInsideItsInstallFolder_AndNotFromElsewhere()
    {
        var folder = TempFolder("Fortnite");
        var provider = new FakeProcessProvider();
        var inside = provider.AddRunning(1, "FortniteClient-Win64-Shipping", Path.Combine(folder, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"));
        var elsewhere = provider.AddRunning(2, "FortniteClient-Win64-Shipping", @"C:\Somewhere\Else\FortniteClient-Win64-Shipping.exe");
        var names = new PerFolderNames(new() { [folder] = ["FortniteClient-Win64-Shipping", "CrashReportClient"] });
        var detector = new ExternalGameDetector(provider, names);

        var found = detector.Detect([new("fortnite", "", folder)], default);          // started by address: no exe on record

        Assert.Equal("fortnite", Assert.Single(found).GameId);
        Assert.True(inside.Disposed && elsewhere.Disposed);
    }

    [Fact]
    public void AProtectedProcess_IsRecognisedByNameOnlyWhenTheNameIsOneGamesMainProgram()
    {
        var one = TempFolder("One");
        var two = TempFolder("Two");
        var provider = new FakeProcessProvider();
        provider.AddRunning(1, "GameOne-Win64-Shipping", null);        // path refused (anti-cheat): a main program of one game only
        provider.AddRunning(2, "EasyAntiCheat_EOS", null);             // a helper with no claim to be the game
        provider.AddRunning(3, "SharedTool", null);                    // in both games' folders
        var names = new PerFolderNames(new()
        {
            [one] = ["GameOne-Win64-Shipping", "EasyAntiCheat_EOS", "SharedTool", "Other"],
            [two] = ["GameTwo-Win64-Shipping", "SharedTool", "Another"],
        });
        var detector = new ExternalGameDetector(provider, names);

        var found = detector.Detect([new("one", "", one), new("two", "", two)], default);

        Assert.Equal("one", Assert.Single(found).GameId);               // not the helper, not the shared name, and not game two
    }

    [Fact]
    public void ADetectorWithoutFolderNames_BehavesAsBefore_NothingIsGuessedFromAFolder()
    {
        var folder = TempFolder("Game");
        var provider = new FakeProcessProvider();
        provider.AddRunning(1, "Game", Path.Combine(folder, "Game.exe"));

        Assert.Empty(new ExternalGameDetector(provider).Detect([new("g", "", folder)], default));
    }
}
