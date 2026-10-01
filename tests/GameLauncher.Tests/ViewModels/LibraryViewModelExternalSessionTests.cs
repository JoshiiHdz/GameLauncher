using System.IO;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace GameLauncher.Tests.ViewModels;

public sealed class LibraryViewModelExternalSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-External-" + Guid.NewGuid());
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private LibraryViewModel Create() => new(new SettingsService(_dir), new PendingUpdateNotesService(_dir))
    {
        ExternalSessionClock = _clock, SessionClock = () => _clock.GetUtcNow().UtcDateTime,
    };
    private static GameEntry Game(string id = "a") => new()
    {
        Id = id, Name = id, Source = GameSource.Manual, InstallDir = @"C:\Games\" + id,
        ExecutablePath = @"C:\Games\" + id + "\\Game.exe",
    };
    private static ExternalGameCandidate Seen(GameEntry g) => new(g.Id, g.ExecutablePath);
    private void ObserveFor(LibraryViewModel vm, int seconds, params GameEntry[] games)
    {
        for (var i = 0; i < seconds; i += 5)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            vm.ApplyExternalObservations(games.Select(Seen).ToArray());
        }
    }

    [Fact]
    public void ExistingProcess_CountsOnlyAfterObservation_CheckpointsAndFlushesOnce()
    {
        var vm = Create(); var game = Game();
        vm.SimulateRefreshResult([game]);
        _clock.Advance(TimeSpan.FromHours(2)); // Running before the launcher noticed it is not imported.
        vm.ApplyExternalObservations([Seen(game)]);
        Assert.True(game.IsRunning);
        Assert.Null(vm.RunningGameId); // Does not take ownership of the launch/hide/restore workflow.
        ObserveFor(vm, 65, game);
        Assert.Equal(60, new SettingsService(_dir).Load().Overrides[game.Id].TotalPlaySeconds);
        vm.StopPassiveTracking();
        Assert.Equal(65, game.TotalPlaySeconds);
        Assert.False(game.IsRunning);
        vm.StopPassiveTracking();
        Assert.Equal(65, game.TotalPlaySeconds);
        Assert.Equal(0, vm.TrackedSessionCount);
    }

    [Fact]
    public void ProcessExit_StopsAtLastPositiveObservation_NotExitPollOrLater()
    {
        var vm = Create(); var game = Game(); vm.SimulateRefreshResult([game]);
        vm.ApplyExternalObservations([Seen(game)]); ObserveFor(vm, 15, game);
        _clock.Advance(TimeSpan.FromSeconds(5)); vm.ApplyExternalObservations([]);
        Assert.Equal(15, game.TotalPlaySeconds); Assert.False(game.IsRunning);
        _clock.Advance(TimeSpan.FromHours(1)); vm.StopPassiveTracking();
        Assert.Equal(15, game.TotalPlaySeconds);
    }

    [Fact]
    public void LaunchedSessionAndPassiveDetection_DoNotDoubleCount_InEitherOrder()
    {
        var vm = Create(); var game = Game(); vm.SimulateRefreshResult([game]);
        vm.ApplyExternalObservations([Seen(game)]); ObserveFor(vm, 15, game);
        var token = vm.MarkGameRunning(game);
        Assert.Equal(15, game.TotalPlaySeconds); Assert.Equal(1, vm.TrackedSessionCount);
        ObserveFor(vm, 20, game); Assert.Equal(1, vm.TrackedSessionCount);
        vm.MarkGameNotRunning(game, token);
        Assert.Equal(35, game.TotalPlaySeconds);
        vm.ApplyExternalObservations([Seen(game)]); ObserveFor(vm, 10, game);
        vm.StopPassiveTracking(); Assert.Equal(45, game.TotalPlaySeconds);
    }

    [Fact]
    public void MultipleGames_RescanAndFiltering_KeepTheirOwnSessions()
    {
        var vm = Create(); var a = Game(); var b = Game("b"); b.Hidden = true;
        vm.SimulateRefreshResult([a, b]); vm.ApplyExternalObservations([Seen(a), Seen(b)]);
        ObserveFor(vm, 15, a, b);
        var replacement = Game(); vm.SimulateRefreshResult([replacement, b]);
        Assert.True(replacement.IsRunning); Assert.True(b.IsRunning);
        vm.SearchText = "unrelated";
        vm.ApplyExternalObservations([Seen(b)]);
        Assert.False(replacement.IsRunning); Assert.True(b.IsRunning);
        Assert.Equal(15, replacement.TotalPlaySeconds);
        ObserveFor(vm, 10, b); vm.StopPassiveTracking();
        Assert.Equal(25, b.TotalPlaySeconds);
    }

    [Fact]
    public void SleepGap_DisableAndStalePath_DoNotCreateUnobservedTime()
    {
        var vm = Create(); var game = Game(); vm.SimulateRefreshResult([game]);
        vm.ApplyExternalObservations([Seen(game)]); ObserveFor(vm, 10, game);
        _clock.Advance(TimeSpan.FromHours(3)); vm.ApplyExternalObservations([Seen(game)]);
        vm.TrackExternalGames = false;
        Assert.Equal(10, game.TotalPlaySeconds); Assert.False(game.IsRunning);
        ObserveFor(vm, 30, game); Assert.Equal(0, vm.TrackedSessionCount);
        Assert.False(new SettingsService(_dir).Load().TrackExternalGames);
        vm.TrackExternalGames = true;
        vm.ApplyExternalObservations([new(game.Id, @"C:\Different\Game.exe")]);
        Assert.Equal(0, vm.TrackedSessionCount);
    }

    [Fact]
    public async Task IdMigration_PreservesPassiveSession_AndCreditsOnlyTheSurvivingGame()
    {
        var vm = Create(); var old = Game(); vm.SimulateRefreshResult([old]);
        vm.ApplyExternalObservations([Seen(old)]); ObserveFor(vm, 15, old);
        var winner = new GameEntry { Id = "new-id", Name = old.Name, Source = old.Source,
            InstallDir = old.InstallDir, ExecutablePath = old.ExecutablePath };
        await vm.ApplyScanResultAsync(new ScanResult([winner], [], [], [], [],
            new Dictionary<string, string> { [old.Id] = winner.Id }));
        Assert.True(winner.IsRunning);
        ObserveFor(vm, 10, winner); vm.StopPassiveTracking();
        Assert.Equal(25, winner.TotalPlaySeconds);
        var saved = new SettingsService(_dir).Load();
        Assert.Equal(25, saved.Overrides[winner.Id].TotalPlaySeconds);
        Assert.False(saved.Overrides.ContainsKey(old.Id));
        var restarted = Create(); var entry = new GameEntry { Id = winner.Id, Name = winner.Name,
            Source = winner.Source, InstallDir = winner.InstallDir, ExecutablePath = winner.ExecutablePath };
        await restarted.ApplyScanResultAsync(new ScanResult([entry], [], [], [], []));
        Assert.Equal(25, entry.TotalPlaySeconds);
        Assert.False(entry.IsRunning);
    }

    [Fact]
    public void LateManagedCleanupCannotEraseANewerPassiveRunningBadge()
    {
        var vm = Create(); var game = Game(); vm.SimulateRefreshResult([game]);
        var managed = vm.MarkGameRunning(game); _clock.Advance(TimeSpan.FromSeconds(15));
        vm.MarkGameNotRunning(game, managed);
        vm.ApplyExternalObservations([Seen(game)]);
        vm.MarkGameNotRunning(game, managed);
        Assert.True(game.IsRunning); Assert.Equal(15, game.TotalPlaySeconds);
        vm.StopPassiveTracking();
    }

    [Fact]
    public async Task CancelledMonitorEndsWithoutStartingAnySessions()
    {
        var vm = Create();
        await vm.MonitorExternalGamesAsync(new CancellationToken(true));
        Assert.Equal(0, vm.TrackedSessionCount);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
