using GameLauncher.Models;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Identity;

/// <summary>Cancellation must reach the FINAL, synchronous commit, not only the work before it. Each case cancels at the exact moment
/// after the last await has finished and before the UI-thread mutation/save would run, and asserts that nothing was written and
/// nothing on screen changed - and, as its control, that the very same scenario without the cancellation does commit (so the
/// scenario is not passing because it never got that far).
///
/// A staged asset file may stay on disk under the deferred-cleanup policy; that is never a reason to save a cancelled selection.</summary>
public class IdentityCancellationBeforeCommitTests : IDisposable
{
    private readonly IdentityHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");

    private static ArtworkSelection Provenance() => new()
    {
        Provider = ArtworkProvider.SteamGridDb, ProviderGameId = "B", ProviderArtworkRef = "b1", ProviderTitle = "Bar",
    };

    private static ArtworkImageValidator.ValidatedImage ValidImage() =>
        ArtworkImageValidator.ValidateBytes(TestImages.Png(60, 120), "test://cover")
        ?? throw new InvalidOperationException("the test image should validate");

    // ---- Choose Cover: between the asset write and CommitArtworkChange --------------------------------------------------------

    [Fact]
    public async Task ApplyCatalogCover_CancelledAfterTheAssetIsWritten_CommitsNothing()
    {
        var game = await _h.Add(Foo());
        using var cts = new CancellationTokenSource();
        var assetWritten = false;
        _h.Vm.AfterAssetWrittenForTest = _ => { assetWritten = true; cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _h.Vm.ApplyCatalogCoverAsync(game.Id, ValidImage(), Provenance(), expectedArtworkRevision: 0, cts.Token));

        Assert.True(assetWritten);                                        // it really got as far as the write
        Assert.Null(_h.Over(game.Id)?.Artwork);                           // no selection
        Assert.Equal(0, _h.Over(game.Id)?.ArtworkRevision ?? 0);          // no revision advanced
        Assert.Null(IdentityHarness.Height(game));                        // nothing displayed
        Assert.Null(new GameLauncher.Services.SettingsService(_h.DataDir).Load().Overrides.GetValueOrDefault(game.Id)?.Artwork); // nothing saved
    }

    [Fact]
    public async Task ApplyCatalogCover_NotCancelled_CommitsAtTheSamePoint_Control()
    {
        var game = await _h.Add(Foo());
        var assetWritten = false;
        _h.Vm.AfterAssetWrittenForTest = _ => assetWritten = true;

        var outcome = await _h.Vm.ApplyCatalogCoverAsync(game.Id, ValidImage(), Provenance(), expectedArtworkRevision: 0, CancellationToken.None);

        Assert.True(assetWritten);
        Assert.Equal(ArtworkChangeOutcome.Success, outcome);
        Assert.True(_h.Over(game.Id)!.Artwork!.IsUserSelected);
        Assert.NotNull(IdentityHarness.Height(game));
    }

    // ---- Confirm: after launcher-id verification, before the identity commit --------------------------------------------------

    [Fact]
    public async Task Confirm_CancelledAfterLauncherVerificationFinishes_CommitsNothing_AndStartsNoFollowUp()
    {
        var game = await _h.Add(Games.Steam("1091500", "Cyberpunk 2077"));
        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        var generationBefore = _h.Vm.IdentityGenerationForTest(game.Id);
        using var cts = new CancellationTokenSource();
        _h.Igdb.Consistency = (_, _) => { cts.Cancel(); return LauncherConsistency.Consistent; }; // proof arrives; the window closes as it does

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Vm.ConfirmIdentityAsync(
            game.Id, new CatalogCandidate(Cat.Igdb, "77", "Cyberpunk 2077", null, null), state.DecisionRevision, state.IdentityRevision, cts.Token));

        Assert.Null(_h.Record(game.Id)?.Confirmed);                       // no decision recorded
        Assert.Equal(0, _h.Over(game.Id)?.DecisionRevision ?? 0);
        Assert.Equal(0, _h.Over(game.Id)?.IdentityRevision ?? 0);
        Assert.Equal(generationBefore, _h.Vm.IdentityGenerationForTest(game.Id));
        Assert.Equal(0, _h.Igdb.CoverFetches + _h.Sgdb.CoverFetches);     // and no follow-up unit went looking for art
        Assert.Null(new GameLauncher.Services.SettingsService(_h.DataDir).Load().Overrides.GetValueOrDefault(game.Id)?.Identity?.Confirmed);
    }

    [Fact]
    public async Task Confirm_NotCancelled_RecordsTheProvenLauncherId_Control()
    {
        var game = await _h.Add(Games.Steam("1091500", "Cyberpunk 2077"));
        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        _h.Igdb.Consistency = (_, _) => LauncherConsistency.Consistent;

        var outcome = await _h.Vm.ConfirmIdentityAsync(
            game.Id, new CatalogCandidate(Cat.Igdb, "77", "Cyberpunk 2077", null, null), state.DecisionRevision, state.IdentityRevision);

        Assert.Equal(IdentityChangeOutcome.Success, outcome);
        Assert.Equal(new IdentityKey(Cat.Igdb, "77"), _h.Record(game.Id)!.Confirmed!.Key);
        Assert.Contains(_h.Record(game.Id)!.Confirmed!.VerifiedLauncherIds!, l => l.Id == "1091500");
    }

    [Fact]
    public async Task Clear_CancelledBeforeItsCommit_ChangesNothing()
    {
        var game = await _h.Add(Foo());
        _h.Vm.EnsureOverrideForTest(game.Id).Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "B", "Bar") };
        var state = _h.Vm.GetIdentityDialogState(game.Id)!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _h.Vm.ClearIdentityAsync(game.Id, state.DecisionRevision, state.IdentityRevision, cts.Token));

        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), _h.Record(game.Id)!.Confirmed!.Key);
        Assert.Equal(state.DecisionRevision, _h.Over(game.Id)!.DecisionRevision);
    }

    // ---- The follow-up automatic unit: after its worker returns, before its UI-thread commit -----------------------------------

    [Fact]
    public async Task AutomaticUnit_CancelledAfterItsWorkerFinishes_CommitsNothing()
    {
        var game = await _h.Add(Foo());
        using var cts = new CancellationTokenSource();
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        // The cover lookup is the last thing the worker does: the window closes there, and the worker still returns a full result.
        _h.Igdb.OnCover = _ => cts.Cancel();
        var generationBefore = _h.Vm.IdentityGenerationForTest(game.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Vm.RunAutomaticUnitAsync(game.Id, "Reset", force: true, cts.Token));

        Assert.Equal(1, _h.Igdb.CoverFetches);                            // the worker really ran to the end and produced a cover...
        Assert.Null(_h.Over(game.Id)?.Identity);                          // ...none of which was committed
        Assert.Null(_h.Over(game.Id)?.Artwork);
        Assert.Null(IdentityHarness.Height(game));
        Assert.Equal(generationBefore, _h.Vm.IdentityGenerationForTest(game.Id));
        Assert.Null(new GameLauncher.Services.SettingsService(_h.DataDir).Load().Overrides.GetValueOrDefault(game.Id)?.Identity);
    }

    [Fact]
    public async Task AutomaticUnit_NotCancelled_CommitsTheSameResult_Control()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");

        await _h.Vm.RunAutomaticUnitAsync(game.Id, "Reset", force: true, CancellationToken.None);

        Assert.Contains(_h.Record(game.Id)!.Resolved, r => r.Namespace == Cat.Igdb && r.Id == "A");
        Assert.NotNull(IdentityHarness.Height(game));
    }
}
