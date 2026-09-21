using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Tests.Identity;

namespace GameLauncher.Tests.Services;

/// <summary>The scan's progress bar: the percentage is real (launcher pass first, then games identified), the scanner reports each source in
/// order, and the view model shows a step only while its own scan is the one running.</summary>
public class ScanProgressTests
{
    private sealed class Recorder : IProgress<ScanProgress>
    {
        public readonly List<ScanProgress> Steps = new();
        public void Report(ScanProgress value) => Steps.Add(value);
    }

    // ---- the percentage -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScanPhase.LookingForGames, 0, 10, 0.0)]
    [InlineData(ScanPhase.LookingForGames, 5, 10, 7.5)]
    [InlineData(ScanPhase.LookingForGames, 10, 10, 15.0)]         // the launcher pass is the first 15%
    [InlineData(ScanPhase.IdentifyingGames, 0, 40, 15.0)]
    [InlineData(ScanPhase.IdentifyingGames, 20, 40, 57.5)]
    [InlineData(ScanPhase.IdentifyingGames, 40, 40, 100.0)]
    [InlineData(ScanPhase.IdentifyingGames, 0, 0, 15.0)]          // no games at all: no divide-by-zero, and never NaN
    [InlineData(ScanPhase.IdentifyingGames, 99, 40, 100.0)]       // too many done is clamped, never over 100
    [InlineData(ScanPhase.IdentifyingGames, -3, 40, 15.0)]
    public void ThePercentage_IsRealAndClamped(ScanPhase phase, int done, int total, double expected) =>
        Assert.Equal(expected, new ScanProgress(phase, done, total).Percent, precision: 6);

    [Fact]
    public void ThePercentage_NeverGoesBackwards_AcrossTheWholeScan()
    {
        var steps = Enumerable.Range(0, 11).Select(i => new ScanProgress(ScanPhase.LookingForGames, i, 10))
            .Concat(Enumerable.Range(0, 41).Select(i => new ScanProgress(ScanPhase.IdentifyingGames, i, 40)));

        var last = -1.0;
        foreach (var step in steps)
        {
            Assert.True(step.Percent >= last, $"{step} went backwards from {last}");
            last = step.Percent;
        }

        Assert.Equal(100.0, last, precision: 6);
    }

    [Theory]
    [InlineData(ScanPhase.LookingForGames, 0, 10, null, "Looking for games...")]
    [InlineData(ScanPhase.LookingForGames, 3, 10, "Epic", "Looking for games... Epic")]
    [InlineData(ScanPhase.IdentifyingGames, 12, 40, "Apex Legends", "Identifying games... 12 of 40 - Apex Legends")]
    [InlineData(ScanPhase.IdentifyingGames, 40, 40, null, "Identifying games... 40 of 40")]
    [InlineData(ScanPhase.IdentifyingGames, 0, 0, null, "Identifying games...")]
    public void TheText_SaysWhatIsHappening(ScanPhase phase, int done, int total, string? item, string expected) =>
        Assert.Equal(expected, new ScanProgress(phase, done, total, item).Text);

    // ---- the scanner --------------------------------------------------------------------------------------------------------

    private static (string Name, Func<List<GameEntry>> Scan) Source(string name, params string[] gameNames) =>
        (name, () => gameNames.Select(n => Games.Manual("m-" + n, n)).ToList());

    [Fact]
    public void EverySource_IsReportedBeforeItRuns_InOrder_ThenTheLauncherPassIsComplete()
    {
        var recorder = new Recorder();

        var found = GameScannerService.RunSources([Source("Steam", "A", "B"), Source("Epic"), Source("GOG", "C")], recorder, CancellationToken.None);

        Assert.Equal(new[] { "A", "B", "C" }, found.Select(g => g.Name));
        Assert.Equal(new (ScanPhase, int, int, string?)[]
        {
            (ScanPhase.LookingForGames, 0, 3, "Steam"), (ScanPhase.LookingForGames, 1, 3, "Epic"), (ScanPhase.LookingForGames, 2, 3, "GOG"),
            (ScanPhase.LookingForGames, 3, 3, null),
        }, recorder.Steps.Select(s => (s.Phase, s.Done, s.Total, s.Item)));
    }

    [Fact]
    public void AFailingSource_IsSkipped_StillReported_AndTheOthersStillRun()
    {
        var recorder = new Recorder();
        var failing = ("Xbox", (Func<List<GameEntry>>)(() => throw new IOException("locked")));

        var found = GameScannerService.RunSources([Source("Steam", "A"), failing, Source("GOG", "C")], recorder, CancellationToken.None);

        Assert.Equal(new[] { "A", "C" }, found.Select(g => g.Name));
        Assert.Equal(new[] { "Steam", "Xbox", "GOG" }, recorder.Steps.Where(s => s.Item is not null).Select(s => s.Item));
    }

    [Fact]
    public void ASupersededScan_StopsAfterTheSourceThatIsRunning()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var cancelling = ("Epic", (Func<List<GameEntry>>)(() => { cts.Cancel(); return []; }));

        Assert.Throws<OperationCanceledException>(() =>
            GameScannerService.RunSources([Source("Steam", "A"), cancelling, Source("GOG", "C")], recorder, cts.Token));

        Assert.DoesNotContain(recorder.Steps, s => s.Item == "GOG");                                  // the next source never started
    }

    [Fact]
    public void WithNobodyListening_TheScanStillRuns() =>
        Assert.Single(GameScannerService.RunSources([Source("Steam", "A")], progress: null, CancellationToken.None));

    // ---- the view model -----------------------------------------------------------------------------------------------------

    private static ScanProgress Step(int done) => new(ScanPhase.IdentifyingGames, done, 40, "Apex Legends");

    [Fact]
    public void WhileItsOwnScanRuns_TheViewModelShowsEachStep()
    {
        using var h = new IdentityHarness();
        var owner = h.Vm.SetRefreshOwnershipForTest();
        h.Vm.IsLoading = true;
        Assert.True(h.Vm.IsScanIndeterminate);                                                        // before the first report the bar animates

        h.Vm.ApplyScanProgress(Step(12), owner);

        Assert.False(h.Vm.IsScanIndeterminate);
        Assert.Equal(Step(12).Percent, h.Vm.ScanProgressPercent, precision: 6);
        Assert.Equal("Identifying games... 12 of 40 - Apex Legends", h.Vm.ScanProgressText);
        Assert.Equal(h.Vm.ScanProgressText, h.Vm.StatusText);                                         // and the status bar says the same
    }

    [Fact]
    public void AReportFromAScanThatWasSupersededOrAlreadyFinished_IsIgnored()
    {
        using var h = new IdentityHarness();
        var current = h.Vm.SetRefreshOwnershipForTest();
        using var old = new CancellationTokenSource();
        h.Vm.IsLoading = true;

        h.Vm.ApplyScanProgress(Step(12), old);                                                        // an older scan still reporting
        Assert.Equal(0, h.Vm.ScanProgressPercent);
        Assert.True(h.Vm.IsScanIndeterminate);

        h.Vm.IsLoading = false;
        h.Vm.ApplyScanProgress(Step(12), current);                                                    // a report that arrives after the scan finished
        Assert.Equal(0, h.Vm.ScanProgressPercent);
        Assert.Equal("", h.Vm.ScanProgressText);
    }

    [Fact]
    public void TheBar_NeverGoesBackwards_EvenIfAnEarlierStepArrivesLate()
    {
        using var h = new IdentityHarness();
        var owner = h.Vm.SetRefreshOwnershipForTest();
        h.Vm.IsLoading = true;

        h.Vm.ApplyScanProgress(Step(30), owner);
        h.Vm.ApplyScanProgress(Step(10), owner);

        Assert.Equal(Step(30).Percent, h.Vm.ScanProgressPercent, precision: 6);
    }
}
