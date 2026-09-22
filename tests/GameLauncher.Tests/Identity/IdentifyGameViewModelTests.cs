using System.IO;
using System.Net.Http;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>The Identify Game / Choose Cover dialog's LOGIC, without a window: what it searches, how it words each state and each
/// outcome, that a superseded search is discarded, that a stale dialog applies nothing and says why, and that picking an IMAGE
/// never touches identity. Covers are told apart by decoded height (A = 480, B = 640).</summary>
public class IdentifyGameViewModelTests : IDisposable
{
    private readonly IdentityHarness _h = new();
    private const int HeightA = 480, HeightB = 640;

    public void Dispose() => _h.Dispose();

    private static GameEntry Foo() => Games.Manual("manual-foo", "Foo");
    private static CatalogCandidate Cand(IdentifierNamespace ns, string id, string title = "Foo", string? detail = null, string? thumb = null) =>
        new(ns, id, title, detail, thumb);
    private static CatalogCoverResult Cover(int h) => new(CoverLookupStatus.Resolved, TestBitmaps.Distinct(h), false);

    private IdentifyGameViewModel Open(GameEntry game, params ICatalogProvider[] providers) =>
        new(_h.Vm, providers.Length > 0 ? providers : [_h.Igdb, _h.Sgdb], game.Id, game.Name);

    // ---- Opening ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_SearchesTheDetectedTitle_NeverACustomName()
    {
        var game = await _h.Add(Foo());
        game.Name = "My Custom Name";

        var dialog = Open(game);

        Assert.Equal("Foo", dialog.SearchText);
        Assert.Equal("Foo", dialog.DetectedTitle);
    }

    [Fact]
    public async Task Opening_UsesTheCuratedHint_ForAnAbbreviatedTitle()
    {
        var game = await _h.Add(Games.Ea());

        Assert.Equal("Apex Legends", Open(game).SearchText);
    }

    [Theory]
    [InlineData("NoMatch", "No confident match")]
    [InlineData("Ambiguous", "Several games share this name")]
    [InlineData("Contradicted", "disagreed with the launcher")]
    [InlineData("Unavailable", "could not be reached")]
    [InlineData("NotConfigured", "No catalog is configured")]
    public async Task EveryUnresolvedReason_IsWordedSoTheUserKnowsWhyAndWhatToDo(string reason, string expected)
    {
        var game = await _h.Add(Foo());
        _h.Vm.EnsureOverrideForTest(game.Id).Identity = new GameIdentityRecord
        {
            LastAttempt = new ResolutionAttempt { Outcome = LookupOutcome.Create(reason) },
        };

        Assert.Contains(expected, Open(game).StateText);
    }

    [Fact]
    public async Task TheStates_AreDescribedByWhoDecided()
    {
        var game = await _h.Add(Foo());
        var over = _h.Vm.EnsureOverrideForTest(game.Id);
        var query = IdentityQuery.From(game);

        over.Identity = new GameIdentityRecord();
        over.Identity.Resolved.Add(Cat.Resolved(query, Cat.Igdb, "1", "Foo Auto"));
        Assert.Contains("Identified automatically as “Foo Auto” (IGDB)", Open(game).StateText);

        over.Identity = new GameIdentityRecord { Confirmed = Cat.Confirmed(Cat.Sgdb, "9", "Foo Chosen") };
        Assert.Contains("You confirmed this game as “Foo Chosen” (SteamGridDB)", Open(game).StateText);
    }

    // ---- Search -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_ListsCandidatesFromEveryProvider_InProviderOrder()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Candidates = t => [Cand(Cat.Igdb, "1", "Foo", "2019"), Cand(Cat.Igdb, "2", "Foo 2", "2021")];
        _h.Sgdb.Candidates = t => [Cand(Cat.Sgdb, "9", "Foo")];
        var dialog = Open(game);

        await dialog.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "IGDB · 2019", "IGDB · 2021", "SteamGridDB" }, dialog.Candidates.Select(c => c.Subtitle));
        Assert.Equal("3 result(s).", dialog.StatusMessage);
        Assert.False(dialog.IsSearching);
    }

    /// <summary>Waits for the background thumbnail loader SearchAsync fires off (LoadThumbnailsAsync) without
    /// awaiting it directly - it's deliberately fire-and-forget so a superseded search's downloads don't block
    /// the next one, so a test has to poll for it to finish the same way the window itself just waits and redraws.</summary>
    private static async Task WaitForThumbnails(IdentifyGameViewModel dialog, int count)
    {
        for (var i = 0; i < 100 && dialog.Candidates.Take(count).Any(c => c.Thumbnail is null); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Search_ACandidateWithItsOwnThumbnailUrl_IsDownloadedDirectly_NoExtraCoverLookup()
    {
        // IGDB's search response already carries a cover (see ParseCandidates) - asserting ListCovers is never
        // called is what actually proves the fallback added for SteamGridDB (below) doesn't cost every provider
        // an extra network round trip it never needed.
        var game = await _h.Add(Foo());
        _h.Igdb.Candidates = _ => [Cand(Cat.Igdb, "1", "Foo", thumb: "https://images.igdb.com/cover.jpg")];
        _h.Igdb.Covers = _ => throw new InvalidOperationException("ListCovers should never be called when the candidate already has a ThumbnailUrl.");
        _h.Igdb.Download = url => url == "https://images.igdb.com/cover.jpg" ? TestImages.Png(64, 96) : null;
        var dialog = Open(game, _h.Igdb);

        await dialog.SearchCommand.ExecuteAsync(null);
        await WaitForThumbnails(dialog, 1);

        Assert.NotNull(dialog.Candidates.Single().Thumbnail);
    }

    [Fact]
    public async Task Search_ACandidateWithNoThumbnailUrlOfItsOwn_FallsBackToTheProvidersCoverListing()
    {
        // SteamGridDB's autocomplete endpoint returns no image at all (id/name/tags only) - without this
        // fallback, its candidates showed the name and the placeholder icon forever, never a real cover.
        var game = await _h.Add(Foo());
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "9", "Foo", thumb: null)];
        _h.Sgdb.Covers = id => id == "9" ? [new CoverChoice("r1", "https://cdn2.steamgriddb.com/full.png", "https://cdn2.steamgriddb.com/thumb.png")] : [];
        _h.Sgdb.Download = url => url == "https://cdn2.steamgriddb.com/thumb.png" ? TestImages.Png(64, 96) : null;
        var dialog = Open(game, _h.Sgdb);

        await dialog.SearchCommand.ExecuteAsync(null);
        await WaitForThumbnails(dialog, 1);

        Assert.NotNull(dialog.Candidates.Single().Thumbnail);
    }

    [Fact]
    public async Task Search_ACandidateWhoseProviderHasNoCoverEither_StaysWithoutAThumbnail_NotAnException()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "9", "Foo", thumb: null)];
        _h.Sgdb.Covers = _ => Array.Empty<CoverChoice>(); // nothing to fall back to
        var dialog = Open(game, _h.Sgdb);

        await dialog.SearchCommand.ExecuteAsync(null);
        await Task.Delay(50); // give the (necessarily unsatisfiable) background load a chance to finish

        Assert.Null(dialog.Candidates.Single().Thumbnail);
        Assert.Single(dialog.Candidates); // the search result itself is unaffected - only its thumbnail is missing
    }

    [Fact]
    public async Task AFailingProvider_NeverHidesTheOthersResults_AndIsNamed()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Candidates = _ => throw new HttpRequestException("boom");
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "9", "Foo")];
        var dialog = Open(game);

        await dialog.SearchCommand.ExecuteAsync(null);

        Assert.Single(dialog.Candidates);
        Assert.Contains("IGDB could not be searched", dialog.StatusMessage);
    }

    [Fact]
    public async Task ASupersededSearch_IsDiscarded_ItsResultsAreNeverShown()
    {
        var game = await _h.Add(Foo());
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Igdb.Candidates = text =>
        {
            if (text == "old")
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return [Cand(Cat.Igdb, text, text)];
        };
        _h.Sgdb.Candidates = _ => Array.Empty<CatalogCandidate>();
        var dialog = Open(game);

        dialog.SearchText = "old";
        var first = dialog.SearchCommand.ExecuteAsync(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        dialog.SearchText = "new";
        await dialog.SearchCommand.ExecuteAsync(null);
        release.Set();
        await first;

        Assert.All(dialog.Candidates, c => Assert.Equal("new", c.Title)); // the older query's results arrived late and were dropped
    }

    [Fact]
    public async Task ABlankSearch_AndNoConfiguredCatalog_SayWhatIsWrong()
    {
        var game = await _h.Add(Foo());
        var dialog = Open(game);
        dialog.SearchText = "   ";
        await dialog.SearchCommand.ExecuteAsync(null);
        Assert.Contains("Type a title", dialog.StatusMessage);

        var none = new IdentifyGameViewModel(_h.Vm, Array.Empty<ICatalogProvider>(), game.Id, game.Name);
        await none.SearchCommand.ExecuteAsync(null);
        Assert.Contains("No catalog is configured", none.StatusMessage);
    }

    // ---- Confirm / Reject / Clear -------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_AppliesTheChoice_AndSaysSo_AndTheStateTextFollows()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Cover = _ => Cover(90);
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game);
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("Identity set to “Bar” (SteamGridDB)", dialog.StatusMessage);
        Assert.Contains("You confirmed this game as “Bar”", dialog.StateText);
        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), _h.Record(game.Id)!.Confirmed!.Key);
        Assert.True(dialog.CanClear);
    }

    [Fact]
    public async Task Confirm_WhileACoverIsPinned_TellsTheUserItWasKept()
    {
        var game = await _h.Add(Foo());
        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { await _h.Vm.ApplyLocalCoverImageAsync(game.Id, png); }
        finally { File.Delete(png); }
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game);
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("Your custom cover was kept", dialog.StatusMessage);
        Assert.True(dialog.HasPinnedCover);
    }

    [Fact]
    public async Task Confirm_WithNothingSelected_AsksForASelection_AndChangesNothing()
    {
        var game = await _h.Add(Foo());
        var dialog = Open(game);

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("Select a game", dialog.StatusMessage);
        Assert.Null(_h.Record(game.Id)?.Confirmed);
    }

    [Fact]
    public async Task AStaleDialog_AppliesNothing_AndTellsTheUserWhatTheGameWasJustIdentifiedAs()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game); // opened against the UNRESOLVED state
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();

        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");   // meanwhile a scan identifies it
        await _h.Scan(game);

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("This game changed while this window was open", dialog.StatusMessage);
        Assert.Contains("Identified automatically as “Foo” (IGDB)", dialog.StatusMessage);
        Assert.Contains("Nothing was applied", dialog.StatusMessage);
        Assert.Null(_h.Record(game.Id)!.Confirmed);
        Assert.Contains("Identified automatically", dialog.StateText);      // and the dialog now shows the LIVE state
    }

    [Fact]
    public async Task Reject_UsesTheSelectedCandidate_OrElseTheAutomaticMatch()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Title = _ => CatalogSearchResult.Found("A", "Foo");
        await _h.Scan(game);
        var dialog = Open(game);

        await dialog.RejectCommand.ExecuteAsync(null); // nothing selected: "not this game" means the automatic match

        Assert.Contains("will not be picked automatically", dialog.StatusMessage);
        Assert.Contains(_h.Record(game.Id)!.Rejected, r => r.Key == new IdentityKey(Cat.Igdb, "A"));
        Assert.Empty(_h.Record(game.Id)!.Resolved);
    }

    [Fact]
    public async Task Reject_WithNothingToReject_SaysSo()
    {
        var game = await _h.Add(Foo());
        var dialog = Open(game);

        await dialog.RejectCommand.ExecuteAsync(null);

        Assert.Contains("Select the game that is NOT this one", dialog.StatusMessage);
    }

    [Fact]
    public async Task Clear_IsOnlyAvailableWhenThereIsSomethingToClear_AndReturnsToAutomatic()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Cover = _ => Cover(90);
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game);
        Assert.False(dialog.CanClear);
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.True(dialog.CanClear);

        await dialog.ClearCommand.ExecuteAsync(null);

        Assert.Contains("Back to automatic identification", dialog.StatusMessage);
        Assert.Null(_h.Record(game.Id)!.Confirmed);
    }

    // ---- Choose cover -----------------------------------------------------------------------------------------------

    private async Task<(GameEntry Game, IdentifyGameViewModel Dialog)> ConfirmedToB()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Cover = _ => Cover(90);
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game);
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();
        await dialog.ConfirmCommand.ExecuteAsync(null);
        return (game, Open(game)); // a fresh dialog, opened against the confirmed identity
    }

    [Fact]
    public async Task LoadCovers_ListsOnlyTheCoversOfEligibleIdentities()
    {
        var (game, _) = await ConfirmedToB();
        // an automatic IGDB game A that DISAGREES with the confirmed B is ineligible: its covers must never be offered
        _h.Vm.EnsureOverrideForTest(game.Id).Identity!.Resolved.Add(Cat.Resolved(IdentityQuery.From(game), Cat.Igdb, "A", "Foo"));
        _h.Igdb.Covers = _ => [new CoverChoice("a1", "https://images.igdb.com/a1.jpg", null)];
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null), new CoverChoice("b2", "https://cdn2.steamgriddb.com/b2.png", null)];
        var dialog = Open(game);

        await dialog.LoadCoversCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "b1", "b2" }, dialog.Covers.Select(c => c.Choice.Ref));
        Assert.All(dialog.Covers, c => Assert.Equal("B", c.Entry.Id));
    }

    [Fact]
    public async Task LoadCovers_WithNoIdentity_SaysToIdentifyFirst()
    {
        var game = await _h.Add(Foo());
        var dialog = Open(game);

        await dialog.LoadCoversCommand.ExecuteAsync(null);

        Assert.Contains("Identify this game first", dialog.StatusMessage);
        Assert.Empty(dialog.Covers);
    }

    [Fact]
    public async Task ApplyCover_PinsTheImageWithProvenance_ButNeverTouchesIdentity()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        _h.Sgdb.Download = _ => TestImages.Png(60, 120);
        var dialog = Open(game);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();
        var over = _h.Over(game.Id)!;
        var identityBefore = (over.Identity!.Confirmed!.Key, over.IdentityRevision, over.DecisionRevision);
        var artworkRevision = over.ArtworkRevision;

        await dialog.ApplyCoverCommand.ExecuteAsync(null);

        Assert.Contains("Custom cover applied", dialog.StatusMessage);
        Assert.True(over.Artwork!.IsUserSelected);
        Assert.Equal((ArtworkProvider.SteamGridDb, "B", "b1"), (over.Artwork.Provider, over.Artwork.ProviderGameId, over.Artwork.ProviderArtworkRef));
        Assert.Null(over.Artwork.DerivedFrom);                                             // provenance of the IMAGE, never identity
        Assert.Equal(artworkRevision + 1, over.ArtworkRevision);
        Assert.Equal(HeightB, IdentityHarness.Height(game));
        Assert.Equal(identityBefore, (over.Identity.Confirmed.Key, over.IdentityRevision, over.DecisionRevision)); // identity untouched (I2)
    }

    [Fact]
    public async Task ApplyCover_OfAWebpTheAssetStoreCannotStage_SaysSo_AndChangesNothing()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.webp", null)];
        _h.Sgdb.Download = _ => WebpFixtures.Load(WebpFixtures.Lossy);
        var dialog = Open(game);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();
        var revision = _h.Over(game.Id)!.ArtworkRevision;

        await dialog.ApplyCoverCommand.ExecuteAsync(null);

        Assert.Contains("couldn't be used", dialog.StatusMessage);
        Assert.Equal(revision, _h.Over(game.Id)!.ArtworkRevision);
    }

    [Fact]
    public async Task ApplyCover_OfAnImageThatCannotBeDownloaded_SaysSo()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        _h.Sgdb.Download = _ => null;
        var dialog = Open(game);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();

        await dialog.ApplyCoverCommand.ExecuteAsync(null);

        Assert.Contains("couldn't be used", dialog.StatusMessage);
    }

    [Fact]
    public async Task ApplyCover_AfterANewerCoverLanded_IsStale_AndReplacesNothing_D3()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        _h.Sgdb.Download = _ => TestImages.Png(60, 120);
        var dialog = Open(game); // captures the artwork revision now
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();

        var png = Path.Combine(Path.GetTempPath(), $"GameLauncherTests-Pin-{Guid.NewGuid()}.png");
        File.WriteAllBytes(png, TestImages.Png(60, 150));
        try { Assert.Equal(ArtworkChangeOutcome.Success, await _h.Vm.ApplyLocalCoverImageAsync(game.Id, png)); }
        finally { File.Delete(png); }
        var pinnedAsset = _h.Over(game.Id)!.Artwork!.AssetId;

        await dialog.ApplyCoverCommand.ExecuteAsync(null);

        Assert.Contains("cover changed while you were choosing", dialog.StatusMessage);
        Assert.Equal(pinnedAsset, _h.Over(game.Id)!.Artwork!.AssetId);       // the newer cover was not silently overwritten
        Assert.Equal(ArtworkProvider.UserLocalFile, _h.Over(game.Id)!.Artwork!.Provider);
    }

    [Fact]
    public async Task ACoverListingFailure_IsReported_NotThrown()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => throw new HttpRequestException("down");
        var dialog = Open(game);

        await dialog.LoadCoversCommand.ExecuteAsync(null);

        Assert.Contains("SteamGridDB could not be reached", dialog.StatusMessage);
        Assert.Empty(dialog.Covers);
    }

    [Fact]
    public async Task Closing_CancelsWorkInFlight_AndAFinishedSearchIsThenDiscarded()
    {
        var game = await _h.Add(Foo());
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Igdb.Candidates = text => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return [Cand(Cat.Igdb, "1", "late")]; };
        var dialog = Open(game);
        var search = dialog.SearchCommand.ExecuteAsync(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        dialog.Cancel();
        release.Set();
        await search;

        Assert.Empty(dialog.Candidates);
    }

    [Fact]
    public void ADialogForAGameThatDoesNotExist_IsRefusedAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new IdentifyGameViewModel(_h.Vm, [_h.Igdb], "no-such-game", "x"));
    }

    // ---- The dialog's lifetime: closing at any moment is safe ---------------------------------------------------------------------

    /// <summary>Queues work instead of running it, so a test can close the window at the exact moment work is queued but has NOT
    /// started - the case a task cancelled by its token before it runs turns into an exception nobody handles.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly List<Task> _queued = new();

        public int Queued => _queued.Count;

        protected override void QueueTask(Task task)
        {
            lock (_queued) _queued.Add(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() { lock (_queued) return _queued.ToList(); }

        public void RunAll()
        {
            List<Task> batch;
            lock (_queued) { batch = _queued.ToList(); _queued.Clear(); }
            foreach (var task in batch)
                TryExecuteTask(task);
        }
    }

    [Fact]
    public async Task ClosingBeforeASearchWorkerHasStarted_CompletesTheCommand_WithoutThrowing_AndShowsNothing()
    {
        var game = await _h.Add(Foo());
        _h.Igdb.Candidates = _ => [Cand(Cat.Igdb, "1", "late")];
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "2", "late")];
        var scheduler = new ManualScheduler();
        var dialog = Open(game);
        dialog.WorkScheduler = scheduler;

        var search = dialog.SearchCommand.ExecuteAsync(null);      // both providers' workers are queued, none has started
        Assert.Equal(2, scheduler.Queued);
        Assert.True(dialog.IsSearching);
        dialog.Cancel();                                            // the window closes right now
        scheduler.RunAll();

        await search;                                               // used to surface a TaskCanceledException from Task.WhenAll
        Assert.Empty(dialog.Candidates);
        Assert.False(dialog.IsSearching);                           // busy state restored
    }

    [Fact]
    public async Task ClosingWhileACoverListingIsQueued_CompletesTheCommand_AndClearsTheBusyState()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        var scheduler = new ManualScheduler();
        var dialog = Open(game);
        dialog.WorkScheduler = scheduler;

        var load = dialog.LoadCoversCommand.ExecuteAsync(null);
        Assert.True(dialog.IsBusy);
        dialog.Cancel();
        scheduler.RunAll();

        await load;
        Assert.Empty(dialog.Covers);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task ClosingDuringACoverDownload_SavesNothing_EvenThoughTheDownloadFinishes()
    {
        var (game, _) = await ConfirmedToB();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        _h.Sgdb.Download = _ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return TestImages.Png(60, 120);                         // a perfectly good image arrives AFTER the window closed
        };
        var dialog = Open(game);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();
        var over = _h.Over(game.Id)!;
        var (artworkBefore, revisionBefore) = (over.Artwork, over.ArtworkRevision);
        var pixelsBefore = IdentityHarness.Height(game);

        var apply = dialog.ApplyCoverCommand.ExecuteAsync(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        dialog.Cancel();
        release.Set();
        await apply;

        Assert.Same(artworkBefore, over.Artwork);                    // no cover was pinned
        Assert.Equal(revisionBefore, over.ArtworkRevision);
        Assert.Equal(pixelsBefore, IdentityHarness.Height(game));
        Assert.DoesNotContain("Custom cover applied", dialog.StatusMessage);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task ClosingAfterTheAssetIsWritten_ButBeforeTheCoverCommit_PinsNothing()
    {
        var (game, _) = await ConfirmedToB();
        _h.Sgdb.Covers = _ => [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)];
        _h.Sgdb.Download = _ => TestImages.Png(60, 120);
        var dialog = Open(game);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        dialog.SelectedCover = dialog.Covers.Single();
        var over = _h.Over(game.Id)!;
        var (artworkBefore, revisionBefore) = (over.Artwork, over.ArtworkRevision);
        var pixelsBefore = IdentityHarness.Height(game);
        var assetWritten = false;
        // The image downloaded and validated, and the asset was staged: the window closes in the one gap left before the commit.
        _h.Vm.AfterAssetWrittenForTest = _ => { assetWritten = true; dialog.Cancel(); };

        await dialog.ApplyCoverCommand.ExecuteAsync(null);                // must not throw

        Assert.True(assetWritten);
        Assert.Same(artworkBefore, over.Artwork);                          // selection unchanged
        Assert.Equal(revisionBefore, over.ArtworkRevision);                // revision unchanged
        Assert.Equal(pixelsBefore, IdentityHarness.Height(game));          // displayed image unchanged
        Assert.DoesNotContain("Custom cover applied", dialog.StatusMessage);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task AClosedDialog_IgnoresEveryCommand_NothingIsSearchedOrChanged()
    {
        var (game, _) = await ConfirmedToB();
        var dialog = Open(game);
        dialog.Cancel();
        var searchesBefore = _h.Igdb.TitleSearches;
        _h.Igdb.Candidates = _ => throw new InvalidOperationException("a closed dialog must not search");
        _h.Sgdb.Candidates = _ => throw new InvalidOperationException("a closed dialog must not search");
        dialog.SelectedCandidate = new CandidateItem(Cand(Cat.Sgdb, "Z", "Zed"), "SteamGridDB");
        var identityBefore = _h.Record(game.Id)!.Confirmed!.Key;

        await dialog.SearchCommand.ExecuteAsync(null);
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await dialog.RejectCommand.ExecuteAsync(null);
        await dialog.ClearCommand.ExecuteAsync(null);
        await dialog.LoadCoversCommand.ExecuteAsync(null);
        await dialog.ApplyCoverCommand.ExecuteAsync(null);

        Assert.Equal(identityBefore, _h.Record(game.Id)!.Confirmed!.Key);
        Assert.Empty(dialog.Candidates);
        Assert.Equal(searchesBefore, _h.Igdb.TitleSearches);
    }

    [Fact]
    public async Task ClosingBeforeAConfirmationsFollowUpLookupStarts_LeavesTheConfirmationCommitted_AndReportsNoFailure()
    {
        var game = await _h.Add(Foo());
        _h.Sgdb.Candidates = _ => [Cand(Cat.Sgdb, "B", "Bar")];
        var dialog = Open(game);
        await dialog.SearchCommand.ExecuteAsync(null);
        dialog.SelectedCandidate = dialog.Candidates.Single();
        // The follow-up automatic unit builds its provider context first; the window closes at exactly that moment, so its
        // (already cancelled) work is abandoned with an OperationCanceledException that must not become an error message.
        var context = _h.Context;
        _h.Vm.ResolutionContextForTest = () => { dialog.Cancel(); return context; };

        await dialog.ConfirmCommand.ExecuteAsync(null);              // must not throw

        Assert.Equal(new IdentityKey(Cat.Sgdb, "B"), _h.Record(game.Id)!.Confirmed!.Key);  // the user's decision stands
        Assert.False(dialog.IsBusy);
        Assert.DoesNotContain("went wrong", dialog.StatusMessage);
    }

    [Fact]
    public async Task ACoverListingThatFinishesAfterTheWindowClosed_FillsNothing()
    {
        var (game, _) = await ConfirmedToB();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _h.Sgdb.Covers = _ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return [new CoverChoice("b1", "https://cdn2.steamgriddb.com/b1.png", null)]; // completes normally, ignoring the token
        };
        var dialog = Open(game);

        var load = dialog.LoadCoversCommand.ExecuteAsync(null);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        dialog.Cancel();
        release.Set();
        await load;

        Assert.Empty(dialog.Covers);
        Assert.False(dialog.IsBusy);
    }
}
