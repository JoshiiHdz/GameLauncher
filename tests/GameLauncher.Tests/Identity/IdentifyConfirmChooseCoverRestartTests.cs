using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.Tests.TestSupport;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>A run-through of what a user does, as physically as a test can: the REAL Identify Game window driven by automation
/// clicks, the REAL view model, the REAL IGDB/SteamGridDB provider classes over fake HTTP transports (never a real host), the
/// REAL id-keyed cache on disk and settings.json in a temp directory - across FOUR simulated processes, two of them with the
/// network down. It proves the pieces work together; it cannot show how anything looks, and it is not the gaming PC.
///
///   P1 online : the automatic scan finds nothing -> "?" badge -> Identify Game -> search -> "This is the game" -> cover by id
///   P2 offline: restart; the identity persisted; the cover comes from the id-keyed cache; NOT ONE network attempt
///   P3 online : Choose Cover -> a different catalog cover is pinned; identity untouched
///   P4 offline: restart; the pinned cover shows; Reset to automatic returns to the cached cover; NOT ONE network attempt</summary>
[Collection(WpfStaCollection.Name)]
public class IdentifyConfirmChooseCoverRestartTests : IDisposable
{
    private readonly WpfStaFixture _sta;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameLauncherTests-RunThrough-" + Guid.NewGuid());
    private string DataDir => Path.Combine(_root, "data");
    private string AssetDir => Path.Combine(_root, "assets");
    private string IgdbCache => Path.Combine(_root, "cache-igdb");
    private string SgdbCache => Path.Combine(_root, "cache-sgdb");
    private string LegacyRoot => Path.Combine(_root, "legacy");

    private volatile bool _offline;
    private readonly List<string> _imageUrls = new();
    private readonly List<string> _offlineAttempts = new();
    private readonly FakeIgdbHandler _igdbHandler;
    private readonly FuncHttpHandler _sgdbHandler;

    public IdentifyConfirmChooseCoverRestartTests(WpfStaFixture sta)
    {
        _sta = sta;
        Directory.CreateDirectory(_root);
        _igdbHandler = new FakeIgdbHandler
        {
            OnApi = (request, _, _) =>
            {
                if (_offline)
                {
                    lock (_offlineAttempts) _offlineAttempts.Add("IGDB api " + request.RequestUri!.AbsolutePath + " " + request.Content!.ReadAsStringAsync().Result);
                    throw new HttpRequestException("the network is down");
                }

                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content!.ReadAsStringAsync().Result;
                if (path.EndsWith("/games", StringComparison.Ordinal))
                {
                    // the picker asks for release dates and covers; the automatic title lookup only for names
                    return FakeIgdbHandler.Json(body.Contains("first_release_date", StringComparison.Ordinal)
                        ? """[{"id":42,"name":"Foo Tactics","first_release_date":1321574400,"cover":{"image_id":"co42"}},{"id":43,"name":"Foo Tactics 2","first_release_date":1600000000}]"""
                        : """[{"id":1,"name":"An Unrelated Game"}]""");
                }

                return FakeIgdbHandler.Json(path.EndsWith("/covers", StringComparison.Ordinal)
                    ? """[{"image_id":"co42"},{"image_id":"alt7"}]"""
                    : "[]");
            },
            OnImage = (request, _) =>
            {
                if (_offline)
                {
                    lock (_offlineAttempts) _offlineAttempts.Add("IGDB image " + request.RequestUri);
                    throw new HttpRequestException("the network is down");
                }

                lock (_imageUrls) _imageUrls.Add(request.RequestUri!.ToString());
                var height = request.RequestUri!.ToString().Contains("alt7", StringComparison.Ordinal) ? 120 : 90;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(TestImages.Png(60, height)) };
            },
        };
        _sgdbHandler = new FuncHttpHandler((request, _) =>
        {
            if (_offline)
            {
                lock (_offlineAttempts) _offlineAttempts.Add("SGDB " + request.RequestUri);
                throw new HttpRequestException("the network is down");
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") };
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ---- plumbing -------------------------------------------------------------------------------------------------------------

    private IReadOnlyList<ICatalogProvider> Providers() =>
    [
        new IgdbCatalog(new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token", HttpHandlerOverrideForTest = _igdbHandler, BackoffDelayOverrideForTest = _ => { },
        }, IgdbCache),
        new SteamGridDbCatalog(new SteamGridDbCoverArtProvider("key") { HttpHandlerOverrideForTest = _sgdbHandler }, SgdbCache),
    ];

    /// <summary>One process: a brand-new view model over the SAME settings directory - what a restart is.</summary>
    private LibraryViewModel NewProcess() => new(new SettingsService(DataDir), new PendingUpdateNotesService(DataDir))
    {
        AssetStoreDirOverrideForTest = AssetDir,
        IconFallbackForTest = _ => null,
        ResolutionContextForTest = () => Cat.Ctx(Providers(), legacyRoot: LegacyRoot),
    };

    private static GameEntry NewGame() => Games.Manual("manual-foo", "Foo");

    /// <summary>The game list a process starts with (no scan yet): identity and covers as persisted.</summary>
    private static async Task<GameEntry> Startup(LibraryViewModel vm)
    {
        var game = NewGame();
        await vm.ApplyScanResultAsync(new ScanResult([game], [], [], [], new Dictionary<string, ArtworkApplyResult>()));
        return game;
    }

    /// <summary>A scan of one game, exactly as RefreshAsync does it: the worker method off the snapshot, then the UI-thread publish.</summary>
    private static async Task<GameEntry> Scan(LibraryViewModel vm, ResolutionContext context)
    {
        var over = vm.GetOverride("manual-foo");
        var snapshot = over is null ? null : new GameOverride
        {
            Identity = over.Identity?.Clone(), IdentityRevision = over.IdentityRevision, DecisionRevision = over.DecisionRevision,
            ArtworkRevision = over.ArtworkRevision, Artwork = over.Artwork,
        };
        var unit = GameScannerService.ResolveGameUnit(NewGame(), snapshot, over?.ArtworkRevision ?? 0, vm.SnapshotIdentityGenerations(), context, CancellationToken.None,
            coverDisplayed: vm.SnapshotDisplayedCovers().Contains("manual-foo"));
        var game = NewGame();
        await vm.ApplyScanResultAsync(new ScanResult([game], [], [], [], new Dictionary<string, ArtworkApplyResult> { [game.Id] = unit }));
        return game;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            Pump();
            if (condition())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for: " + what);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }

    private static void Click(DependencyObject window, string content)
    {
        var button = Descendants<Wpf.Ui.Controls.Button>(window).Single(b => b.Content as string == content);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        Pump();
    }

    private static IdentifyGameWindow ShowWindow(LibraryViewModel vm, GameEntry game, out IdentifyGameViewModel dialog)
    {
        dialog = new IdentifyGameViewModel(vm, vm.CreateCatalogProvidersForPicker(), game.Id, game.Name);
        var window = new IdentifyGameWindow(dialog)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        Pump();
        return window;
    }

    private string OfflineAttempts() { lock (_offlineAttempts) return string.Join(" | ", _offlineAttempts); }

    private int NetworkAttempts => _igdbHandler.ApiCalls + _igdbHandler.ImageCalls + _sgdbHandler.Calls;

    // ---- the run-through --------------------------------------------------------------------------------------------------------

    [Fact]
    public void IdentifyConfirmChooseCover_ThenRestartOnline_ThenOffline_EveryStepHolds()
    {
        _sta.RunAsync(async () =>
        {
            // ============ P1 (online): nothing found -> badge -> Identify Game -> Confirm ============
            var vm1 = NewProcess();
            await Startup(vm1);
            var game = await Scan(vm1, Cat.Ctx(Providers(), legacyRoot: LegacyRoot));
            Assert.True(game.NeedsIdentity);                                          // the "?" badge invites the user in
            Assert.Contains("No confident match", game.IdentityBadgeText);
            Assert.False(game.IsCoverArt);

            var window = ShowWindow(vm1, game, out var dialog);
            try
            {
                await WaitUntil(() => dialog.Candidates.Count == 2, "the picker's first search (opened on the detected title)");
                Assert.Equal("Foo", dialog.SearchText);
                Assert.Equal(new[] { "Foo Tactics", "Foo Tactics 2" }, dialog.Candidates.Select(c => c.Title));
                Assert.Equal("IGDB · 2011", dialog.Candidates[0].Subtitle);

                window.CandidateList.SelectedItem = window.CandidateList.Items[0];
                Click(window, "This is the game");
                await WaitUntil(() => vm1.GetOverride(game.Id)?.Artwork is { IsUserSelected: false } && game.IsCoverArt, "the confirmed game's cover");
            }
            finally
            {
                window.Close();
            }

            var over1 = vm1.GetOverride(game.Id)!;
            Assert.Equal(new IdentityKey(Cat.Igdb, "42"), over1.Identity!.Confirmed!.Key);
            Assert.Equal(new IdentityKey(Cat.Igdb, "42"), over1.Artwork!.DerivedFrom);   // the cover was fetched BY ID for the chosen game
            Assert.Equal(480, IdentityHarness.Height(game));
            Assert.False(game.NeedsIdentity);                                            // and the badge is gone
            Assert.True(File.Exists(IdKeyedCoverCache.PathFor(IgdbCache, "42", IgdbCoverArtProvider.IdCacheVersion)));
            Assert.True(vm1.SaveNowForTest());

            // ============ P2 (OFFLINE restart): the identity persisted; the cover comes from the id-keyed cache ============
            _offline = true;
            var attemptsBefore = NetworkAttempts;
            var vm2 = NewProcess();
            var game2 = await Startup(vm2);
            var over2 = vm2.GetOverride(game2.Id)!;
            Assert.Equal(new IdentityKey(Cat.Igdb, "42"), over2.Identity!.Confirmed!.Key);   // persisted
            game2 = await Scan(vm2, Cat.Ctx(Providers(), legacyRoot: LegacyRoot));

            Assert.Equal(480, IdentityHarness.Height(game2));                                // the cover is shown...
            Assert.Equal(ArtworkRetrievalMethod.LocalCache, vm2.GetOverride(game2.Id)!.Artwork!.RetrievedFrom); // ...from disk
            Assert.False(game2.NeedsIdentity);
            Assert.True(attemptsBefore == NetworkAttempts, "P2 made network attempts: " + OfflineAttempts()); // not one request while offline
            Assert.True(vm2.SaveNowForTest());

            // ============ P3 (online): Choose Cover pins a different cover; identity is untouched ============
            _offline = false;
            var vm3 = NewProcess();
            var game3 = await Startup(vm3);
            var over3 = vm3.GetOverride(game3.Id)!;
            var (decision, identityRevision, artworkRevision) = (over3.DecisionRevision, over3.IdentityRevision, over3.ArtworkRevision);

            var window3 = ShowWindow(vm3, game3, out var dialog3);
            try
            {
                window3.Tabs.SelectedItem = window3.CoverTab;
                window3.UpdateLayout();
                Pump();
                Click(window3, "Load covers");
                await WaitUntil(() => dialog3.Covers.Count == 2, "the confirmed game's covers");
                Assert.All(dialog3.Covers, c => Assert.Equal("42", c.Entry.Id));             // listed for the identity, never a title guess

                window3.CoverList.SelectedItem = window3.CoverList.Items[1];                  // the alternative cover (a different image)
                Click(window3, "Use this cover");
                await WaitUntil(() => vm3.GetOverride(game3.Id)!.Artwork is { IsUserSelected: true }, "the custom cover to be saved");
            }
            finally
            {
                window3.Close();
            }

            over3 = vm3.GetOverride(game3.Id)!;
            Assert.Equal(640, IdentityHarness.Height(game3));
            Assert.Equal(("alt7", "42", null), (over3.Artwork!.ProviderArtworkRef, over3.Artwork.ProviderGameId, over3.Artwork.DerivedFrom?.ToString()));
            Assert.Equal(artworkRevision + 1, over3.ArtworkRevision);
            Assert.Equal((decision, identityRevision), (over3.DecisionRevision, over3.IdentityRevision));  // picking an IMAGE never touches identity
            Assert.Equal(new IdentityKey(Cat.Igdb, "42"), over3.Identity!.Confirmed!.Key);
            Assert.True(vm3.SaveNowForTest());

            // ============ P4 (OFFLINE restart): the pinned cover shows; Reset returns to the cached automatic cover ============
            _offline = true;
            attemptsBefore = NetworkAttempts;
            var vm4 = NewProcess();
            var game4 = await Startup(vm4);
            Assert.Equal(640, IdentityHarness.Height(game4));                                 // the pinned cover, from the asset store
            game4 = await Scan(vm4, Cat.Ctx(Providers(), legacyRoot: LegacyRoot));
            Assert.Equal(640, IdentityHarness.Height(game4));                                 // an offline scan neither replaces nor clears it
            Assert.True(attemptsBefore == NetworkAttempts, "P4 scan made network attempts: " + OfflineAttempts());

            Assert.Equal(ArtworkChangeOutcome.Success, await vm4.ResetCoverToAutomaticAsync(game4.Id));
            var over4 = vm4.GetOverride(game4.Id)!;
            Assert.Equal(480, IdentityHarness.Height(game4));                                 // back to the identity's own cover, offline
            Assert.False(over4.Artwork!.IsUserSelected);
            Assert.Equal(new IdentityKey(Cat.Igdb, "42"), over4.Identity!.Confirmed!.Key);    // Reset kept the identity
            Assert.True(attemptsBefore == NetworkAttempts, "P4 reset made network attempts: " + OfflineAttempts()); // still not one request
        });
    }
}
