using GameLauncher.Services;
using GameLauncher.Services.SessionTracking;

namespace GameLauncher.Tests.Services.SessionTracking;

public sealed class SnapshotProcessProviderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void LibrarySizeDoesNotMultiplySnapshots_OnlyMatchesGetPathQueries(int count)
    {
        var calls = 0;
        var game = new CountingProcess("Game1", @"C:\Games\1\Game1.exe");
        var unrelated = new CountingProcess("Browser", @"C:\Browser\Browser.exe");
        var provider = new SnapshotProcessProvider(() => { calls++; return [game, unrelated]; });
        var candidates = Enumerable.Range(1, count)
            .Select(i => new ExternalGameCandidate("game" + i, $@"C:\Games\{i}\Game{i}.exe")).ToArray();
        Assert.Single(new ExternalGameDetector(provider).Detect(candidates, default));
        Assert.Equal(1, calls);
        Assert.Equal(1, game.PathReads);
        Assert.Equal(0, unrelated.PathReads);
        Assert.Equal(1, game.Disposals);
        Assert.Equal(1, unrelated.Disposals);
    }

    [Fact]
    public void EmptyLibraryAndPreCancellation_PerformNoProcessEnumeration()
    {
        var calls = 0;
        var provider = new SnapshotProcessProvider(() => { calls++; return []; });
        Assert.Empty(provider.FindProcessesByName([]));
        Assert.Empty(new ExternalGameDetector(provider).Detect([], default));
        Assert.Throws<OperationCanceledException>(() => new ExternalGameDetector(provider)
            .Detect([new("game", @"C:\Game.exe")], new CancellationToken(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NameMatchingRemainsCaseInsensitive_AndUnmatchedHandlesAreReleased()
    {
        var game = new CountingProcess("GAME", @"C:\Game.exe");
        var other = new CountingProcess("Other", @"C:\Other.exe");
        var matches = new SnapshotProcessProvider(() => [game, other]).FindProcessesByName(["game", "GAME"]);
        Assert.Same(game, Assert.Single(matches));
        Assert.Equal(0, game.Disposals);
        Assert.Equal(1, other.Disposals);
        game.Dispose();
    }

    private sealed class CountingProcess(string name, string path) : IGameProcess
    {
        public int Id => 1;
        public string ProcessName => name;
        public int PathReads, Disposals;
        public string? GetPath() { PathReads++; return path; }
        public DateTimeOffset? GetStartTimeUtc() => null;
        public void PrepareForExitWait() => throw new NotSupportedException();
        public Task WaitForExitAsync(CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
}
