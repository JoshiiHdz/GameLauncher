using System.Diagnostics;
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
}
