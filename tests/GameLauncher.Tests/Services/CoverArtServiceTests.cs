using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using System.Net.Http;
using GameLauncher.Services.CoverArt;
using GameLauncher.Tests.Services.CoverArt;

namespace GameLauncher.Tests.Services;

/// <summary>Covers ApplyStored/ApplyStoredSafely/TryDecodeStored (the stored-user-selection path) and
/// ApplyIconFallbackSafely (the shared crash-isolated exe-icon fallback used everywhere in this codebase
/// that needs one). Apply's own automatic-matcher/fallback-ORDERING logic (IGDB primary, SteamGridDB
/// fallback) is covered directly further down, using real provider instances with their OWN
/// transport-level test seams - each provider's own internal matching logic is covered in isolation by
/// SteamGridDbCoverArtProviderTests/IgdbCoverArtProviderTests instead.
///
/// Uses ISOLATED asset-store/cover-art-cache directories (ArtworkAssetStore's storeDirOverride,
/// CoverArtService.Apply's cacheDirOverride) rather than the real %AppData%\GameLauncher\CustomCovers/
/// CoverArtCache - a real, confirmed gap in an earlier version of this suite wrote real files there.</summary>
[Collection(IgdbStaticStateCollection.Name)] // IgdbCoverArtProvider's token cache and rate-limit clock are shared statics
public class CoverArtServiceTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Assets-" + Guid.NewGuid());

    // Only used by the Apply-level fallback-ordering tests below, which (unlike every other test in this
    // file) call the real GetCoverArt on real provider instances - without this, they would write into
    // the real %AppData%\GameLauncher\CoverArtCache, exactly the class of bug this file's own remarks
    // already warn about for ArtworkAssetStore.
    private readonly string _artCacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-ArtCache-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
        if (Directory.Exists(_artCacheDir))
            Directory.Delete(_artCacheDir, recursive: true);
    }

    private string WriteAsset(byte[] bytes) => ArtworkAssetStore.Write(bytes, "png", _storeDir);

    private static byte[] MakePng(int width = 8, int height = 8)
    {
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static GameEntry MakeGame() => new()
    {
        Id = "manual-test",
        Name = "Test Game",
        ExecutablePath = @"C:\Games\Test\game.exe",
        InstallDir = @"C:\Games\Test",
        Source = GameSource.Manual,
    };

    // ---- TryDecodeStored --------------------------------------------------------------------------------

    [Fact]
    public void TryDecodeStored_ValidAsset_ReturnsDecodedBitmap()
    {
        var assetId = WriteAsset(MakePng());
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        var bitmap = CoverArtService.TryDecodeStored(selection, _storeDir);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void TryDecodeStored_MissingAsset_ReturnsNull_DoesNotThrow()
    {
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        Assert.Null(CoverArtService.TryDecodeStored(selection, _storeDir));
    }

    [Fact]
    public void TryDecodeStored_DoesNotTouchTheGivenGameEntry()
    {
        // TryDecodeStored takes no GameEntry at all - proving the signature itself, since this is what
        // makes it safe to call from a background thread for a batch of different games without any of
        // them being the live, UI-bound instance.
        var assetId = WriteAsset(MakePng());
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        CoverArtService.TryDecodeStored(selection, _storeDir);
        // No GameEntry parameter exists to assert against - this test exists purely to document/pin the
        // signature; a future accidental parameter addition would be a deliberate, reviewable API change.
    }

    // ---- ApplyStored ----------------------------------------------------------------------------------

    [Fact]
    public void ApplyStored_ValidAsset_AppliesIconAndReturnsTrue()
    {
        var assetId = WriteAsset(MakePng());
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        var applied = CoverArtService.ApplyStored(game, selection, _storeDir);

        Assert.True(applied);
        Assert.NotNull(game.Icon);
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStored_MissingAsset_ReturnsFalseWithoutThrowing()
    {
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };

        Assert.False(CoverArtService.ApplyStored(game, selection, _storeDir));
    }

    [Fact]
    public void ApplyStored_CorruptAsset_ReturnsFalseWithoutThrowing()
    {
        var assetId = WriteAsset([1, 2, 3, 4, 5]); // not a real image
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        Assert.False(CoverArtService.ApplyStored(game, selection, _storeDir));
    }

    // ---- ApplyStoredSafely: retain the preference, never crash, never guess different artwork --------

    [Fact]
    public void ApplyStoredSafely_ValidAsset_AppliesIcon()
    {
        var assetId = WriteAsset(MakePng());
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = assetId, AssetExtension = "png", IsUserSelected = true };

        CoverArtService.ApplyStoredSafely(game, selection, storeDirOverride: _storeDir);

        Assert.NotNull(game.Icon);
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStoredSafely_MissingAsset_FallsBackToIcon_SelectionNotSubstituted()
    {
        var game = MakeGame();
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };
        var fallbackIcon = new BitmapImage();

        CoverArtService.ApplyStoredSafely(game, selection, getIcon: _ => fallbackIcon, storeDirOverride: _storeDir);

        Assert.Same(fallbackIcon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void ApplyStoredSafely_MissingAsset_AndIconFallbackAlsoThrows_LeavesIconNull_DoesNotThrow()
    {
        // The exact "both fail" case point 1 requires: loading the stored asset fails (missing), AND
        // the icon fallback itself throws - the method must still return normally, Icon null, IsCoverArt
        // false, never letting the second failure escape.
        var game = MakeGame();
        game.Icon = new BitmapImage(); // pre-existing value must be cleared, not left stale
        var selection = new ArtworkSelection { AssetId = Guid.NewGuid().ToString("D"), AssetExtension = "png", IsUserSelected = true };

        var exception = Record.Exception(() => CoverArtService.ApplyStoredSafely(
            game, selection, getIcon: _ => throw new InvalidOperationException("simulated icon extraction failure"), storeDirOverride: _storeDir));

        Assert.Null(exception);
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }

    // ---- ApplyIconFallbackSafely: the shared, never-throws exe-icon fallback -------------------------

    [Fact]
    public void ApplyIconFallbackSafely_NormalIcon_Applies()
    {
        var game = MakeGame();
        var icon = new BitmapImage();

        CoverArtService.ApplyIconFallbackSafely(game, _ => icon);

        Assert.Same(icon, game.Icon);
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void ApplyIconFallbackSafely_ThrowingGetIcon_LeavesIconNull_DoesNotThrow()
    {
        var game = MakeGame();
        game.Icon = new BitmapImage(); // pre-existing value must be cleared, not left stale

        var exception = Record.Exception(() => CoverArtService.ApplyIconFallbackSafely(
            game, _ => throw new InvalidOperationException("simulated icon extraction failure")));

        Assert.Null(exception);
        Assert.Null(game.Icon);
        Assert.False(game.IsCoverArt);
    }

    // ---- Apply: IGDB-primary/SteamGridDB-fallback ordering ---------------------------------------
    // Real provider instances with THEIR OWN transport-level seams, injected via Apply's test-only
    // override parameters - proves the ORDERING/fallback decision itself, not each provider's own
    // internal matching logic (already covered in isolation by IgdbCoverArtProviderTests/
    // SteamGridDbCoverArtProviderTests).

    private static byte[] MakeCoverPng() => MakePng();

    [Fact]
    public void Apply_IgdbResolvesWithArt_NeverCallsSteamGridDb_AndUsesIgdbsOwnId()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(555, "IGDB Title"),
            FetchCoverImageBytesOverride = _ => MakeCoverPng(),
        };
        var gridDbCalled = false;
        var gridDb = new SteamGridDbCoverArtProvider("key")
        {
            SearchGameIdOverride = _ => { gridDbCalled = true; return null; },
        };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.False(gridDbCalled); // SteamGridDB must never even be searched once IGDB succeeds
        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.Igdb, selection!.Provider);
        Assert.Equal("555", selection.ProviderGameId); // IGDB's own id, never SteamGridDB's id space
        Assert.True(game.IsCoverArt);
    }

    [Fact]
    public void Apply_IgdbHasNoConfidentMatch_FallsBackToSteamGridDb_WithItsOwnIndependentSearch()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => null, // no confident IGDB identity at all
        };
        string? gridDbSeenName = null;
        var gridDb = new SteamGridDbCoverArtProvider("key")
        {
            SearchGameIdOverride = g => { gridDbSeenName = g.Name; return new SteamGridDbCoverArtProvider.MatchedGame(777, "Grid Title"); },
            FetchGridImageBytesOverride = _ => MakeCoverPng(),
        };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.Equal("Test Game", gridDbSeenName); // SteamGridDB searched independently by name, never given an IGDB id
        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        Assert.Equal("777", selection.ProviderGameId);
    }

    [Fact]
    public void Apply_IgdbIdentifiesTheGameButHasNoCover_StillFallsBackToSteamGridDb_NotPassingIgdbsId()
    {
        // The real correction this proves: a confident IGDB identity with no usable artwork must not be
        // treated as "resolved, nothing more to do" - it falls back exactly like no match at all, and
        // SteamGridDB is never handed IGDB's id (777 below is SteamGridDB's OWN id, unrelated to IGDB's).
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(999, "IGDB Title"),
            FetchCoverImageBytesOverride = _ => null, // identified, but no cover
        };
        var gridDb = new SteamGridDbCoverArtProvider("key")
        {
            SearchGameIdOverride = _ => new SteamGridDbCoverArtProvider.MatchedGame(777, "Grid Title"),
            FetchGridImageBytesOverride = _ => MakeCoverPng(),
        };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        Assert.Equal("777", selection.ProviderGameId);
    }

    [Fact]
    public void Apply_NoIgdbCredentialsConfigured_SkipsIgdbEntirely_GoesStraightToSteamGridDb()
    {
        var game = MakeGame();
        var gridDbCalled = false;
        var gridDb = new SteamGridDbCoverArtProvider("key")
        {
            SearchGameIdOverride = _ => { gridDbCalled = true; return new SteamGridDbCoverArtProvider.MatchedGame(777, "Grid Title"); },
            FetchGridImageBytesOverride = _ => MakeCoverPng(),
        };

        var selection = CoverArtService.Apply(game, "gridkey", igdbClientId: null, igdbClientSecret: null, steamGridDbProviderOverride: gridDb, cacheDirOverride: _artCacheDir);

        Assert.True(gridDbCalled);
        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
    }

    [Fact]
    public void Apply_NeitherProviderResolves_FallsBackToExeIconFallback_ReturnsNull()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", SearchGameIdOverride = _ => null };
        var gridDb = new SteamGridDbCoverArtProvider("key") { SearchGameIdOverride = _ => null };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.Null(selection);
        Assert.False(game.IsCoverArt);
    }

    private static GameEntry MakeSteamGame() => new()
    {
        Id = "steam-730",
        Name = "Counter-Strike",
        ExecutablePath = @"C:\Games\CS",
        InstallDir = @"C:\Games\CS",
        Source = GameSource.Steam,
        LaunchUri = "steam://rungameid/730",
    };

    [Fact]
    public void Apply_SteamSourcedGame_IgdbIsTriedFirst_NotSteamCdn()
    {
        // Required behavior, tested against the branch a naive "Steam always wins" reading of the order
        // would miss entirely: even for a Steam-sourced game, IGDB runs BEFORE Steam CDN, not after.
        var game = MakeSteamGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(1, "Counter-Strike"),
            FetchCoverImageBytesOverride = _ => MakeCoverPng(),
        };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, cacheDirOverride: _artCacheDir);

        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.Igdb, selection!.Provider); // not SteamCdn - IGDB won because it ran first and resolved
    }

    [Fact]
    public void Apply_IgdbAmbiguous_SuppressesSteamGridDbSpecifically_NotEveryOtherStep()
    {
        // The real distinction this proves: ambiguity suppresses the OTHER name-SEARCHING provider
        // (SteamGridDB) specifically - not a blanket "stop everything" flag. A Manual-sourced game is
        // used here (rather than Steam) specifically to avoid SteamCoverArtProvider, which has no test
        // seam of its own and would otherwise make a real network call in this test; Steam CDN "still
        // runs after an ambiguous IGDB result because it never guesses by name" is a real, deliberate
        // behavior visible directly in CoverArtService.Apply's own ordering/gating code, not independently
        // unit-tested here for that reason.
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => """[{"id":1,"name":"Test Game"},{"id":2,"name":"Test Game"}]""",
        };
        var gridDbCalled = false;
        var gridDb = new SteamGridDbCoverArtProvider("key") { SearchGameIdOverride = _ => { gridDbCalled = true; return null; } };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.False(gridDbCalled); // ambiguous identity - SteamGridDB must not guess independently
        Assert.Null(selection);
    }

    [Fact]
    public void Apply_KnownUmbrellaProductName_SkipsIgdbAndSteamGridDb_NeitherIsSearched()
    {
        // The real, confirmed regression this closes at the Apply level (see IgdbCoverArtProvider's own
        // identical internal test for the defense-in-depth copy of this same guard): an earlier version
        // of Apply had no umbrella check of its own at all, so IGDB (which had no guard either at the
        // time) could search a bare "Call of Duty" and land on the wrong, real, distinct IGDB entry
        // before SteamGridDB's own guard ever ran.
        var game = new GameEntry
        {
            Id = "xbox-cod-test", Name = "Call of Duty®", // real detected name, trademark symbol and all
            ExecutablePath = @"C:\Games\COD\game.exe", InstallDir = @"C:\Games\COD", Source = GameSource.Xbox,
        };
        var igdbCalled = false;
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => { igdbCalled = true; return new IgdbCoverArtProvider.MatchedGame(1, "Call of Duty"); },
            FetchCoverImageBytesOverride = _ => MakeCoverPng(),
        };
        var gridDbCalled = false;
        var gridDb = new SteamGridDbCoverArtProvider("key") { SearchGameIdOverride = _ => { gridDbCalled = true; return null; } };

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir);

        Assert.False(igdbCalled);
        Assert.False(gridDbCalled);
        Assert.Null(selection);
        Assert.False(game.IsCoverArt);
    }

    // ---- Apply: the explicit IGDB status picks the diagnostic AND the fallback -----------------------------
    // Through the real coordinator (Apply), not just the provider: the provider correctly logged a request
    // failure, and Apply then printed "IGDB had no confident match" over the top of it, because its two
    // output flags could not tell an authentication/network failure from a rejected search.

    private static SteamGridDbCoverArtProvider ResolvingGridDb(Action? onCalled = null) => new("key")
    {
        SearchGameIdOverride = _ => { onCalled?.Invoke(); return new SteamGridDbCoverArtProvider.MatchedGame(777, "Grid Title"); },
        FetchGridImageBytesOverride = _ => MakeCoverPng(),
    };

    [Fact]
    public void Apply_IgdbUnavailable_FallsBackToSteamGridDb_AndSaysUnavailable_NotNoConfidentMatch()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("apply-unavailable-id", "apply-unavailable-secret")
        {
            TokenRequestOverride = (_, _) => throw new HttpRequestException("token endpoint returned 400"),
            SearchRequestOverride = _ => "[]",
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", "apply-unavailable-id", "apply-unavailable-secret",
            igdb, ResolvingGridDb(), _artCacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider); // fallback still happened
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_IgdbNoMatch_SaysNoConfidentMatch_AndFallsBack()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret") { AccessTokenOverrideForTest = "token", SearchGameIdOverride = _ => null };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, ResolvingGridDb(), _artCacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_IgdbIdentifiedButNoUsableCover_SaysSo_AndFallsBack()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => new IgdbCoverArtProvider.MatchedGame(999, "IGDB Title"),
            FetchCoverImageBytesOverride = _ => null,
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, ResolvingGridDb(), _artCacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("no usable cover", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no confident match", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_IgdbAmbiguous_SaysAmbiguous_AndDoesNotFallBack()
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchRequestOverride = _ => """[{"id":1,"name":"Test Game"},{"id":2,"name":"Test Game"}]""",
        };
        var gridDbCalled = false;
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, ResolvingGridDb(() => gridDbCalled = true), _artCacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.Null(selection);
        Assert.False(gridDbCalled);
        Assert.Contains("ambiguous", Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeIgdbFallback_EachStatusHasItsOwnDiagnostic_AndOnlyNoMatchClaimsNoConfidentMatch()
    {
        var texts = Enum.GetValues<CoverLookupStatus>()
            .Where(s => s != CoverLookupStatus.Resolved)
            .ToDictionary(s => s, s => CoverArtService.DescribeIgdbFallback("G", s));

        Assert.Equal(texts.Count, texts.Values.Distinct().Count());
        foreach (var (status, text) in texts)
        {
            Assert.Equal(status == CoverLookupStatus.NoMatch, text.Contains("no confident match", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- Apply: a malformed IGDB token response must not skip the fallback chain -------------------------
    // The scanner's outer safety net (SafeApplyCoverArt) falls straight to the exe icon on ANY escaping
    // exception - skipping SteamGridDB and Steam CDN. An unvalidated token response used to throw
    // KeyNotFoundException/FormatException, outside the provider's own catch filter, and reach it.

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"access_token":123,"expires_in":5000000}""")]
    [InlineData("""{"access_token":"t","expires_in":99999999999999999999}""")]
    [InlineData("not json")]
    public void Apply_MalformedIgdbTokenResponse_StillFallsBackToSteamGridDb(string malformedToken)
    {
        var game = MakeGame();
        var igdb = new IgdbCoverArtProvider("apply-malformed-id", "apply-malformed-secret")
        {
            TokenRequestOverride = (_, _) => malformedToken,
            SearchRequestOverride = _ => "[]",
        };
        var diagnostics = new List<string>();

        var selection = CoverArtService.Apply(game, "gridkey", "apply-malformed-id", "apply-malformed-secret",
            igdb, ResolvingGridDb(), _artCacheDir, diagnosticSinkForTest: diagnostics.Add);

        Assert.NotNull(selection);
        Assert.Equal(ArtworkProvider.SteamGridDb, selection!.Provider);
        Assert.Contains("unavailable", Assert.Single(diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Apply: caller cancellation stops the whole cascade ------------------------------------------------

    [Fact]
    public void Apply_CancelledDuringTheIgdbDownload_Throws_AndStartsNoFallbackProviderWork()
    {
        var game = MakeGame();
        var handler = new FakeIgdbHandler { OnImage = (_, _) => FakeIgdbHandler.StallingBody() };
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            HttpHandlerOverrideForTest = handler,
            SearchRequestOverride = _ => """[{"id":1,"name":"Test Game"}]""",
            ImageDownloadTimeoutOverrideForTest = TimeSpan.FromSeconds(30), // the CALLER's cancellation must be what ends it
        };
        var gridDbCalls = 0;
        var gridDb = ResolvingGridDb(() => gridDbCalls++);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(150);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, gridDb, _artCacheDir, cts.Token));

        Assert.Equal(1, handler.ImageCalls);
        Assert.Equal(0, gridDbCalls); // the whole point: no SteamGridDB request for an already-superseded scan
        Assert.Null(game.Icon);        // and no exe-icon "fallback" either - the cancellation was not swallowed
        Assert.False(game.IsCoverArt);
    }

    [Fact]
    public void Apply_CancelledAfterIgdbReportsNoMatch_ThrowsBeforeSteamGridDb()
    {
        var game = MakeGame();
        using var cts = new CancellationTokenSource();
        var igdb = new IgdbCoverArtProvider("id", "secret")
        {
            AccessTokenOverrideForTest = "token",
            SearchGameIdOverride = _ => { cts.Cancel(); return null; }, // the scan is superseded as IGDB finishes
        };
        var gridDbCalls = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CoverArtService.Apply(game, "gridkey", "id", "secret", igdb, ResolvingGridDb(() => gridDbCalls++), _artCacheDir, cts.Token));

        Assert.Equal(0, gridDbCalls);
    }

    [Fact]
    public void Apply_CancelledAfterSteamGridDb_ThrowsBeforeSteamCdn_SoNoSteamNetworkWorkStarts()
    {
        // A Steam-sourced game with no IGDB configured: SteamGridDB finds nothing (and cancels the scan
        // while doing so); the check before Steam CDN must throw. SteamCoverArtProvider has no test seam,
        // so if the check were missing this would fall through to a REAL Steam CDN request.
        var game = MakeSteamGame();
        using var cts = new CancellationTokenSource();
        var gridDb = new SteamGridDbCoverArtProvider("key") { SearchGameIdOverride = _ => { cts.Cancel(); return null; } };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CoverArtService.Apply(game, "gridkey", null, null, null, gridDb, _artCacheDir, cts.Token));
    }
}
