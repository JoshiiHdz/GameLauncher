using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;
using GameLauncher.Tests.Services.CoverArt;
using GameLauncher.ViewModels;

namespace GameLauncher.Tests.Identity;

/// <summary>A catalog whose every answer is scripted and whose every call is COUNTED, so a test can assert what the
/// resolver asked for - "zero title searches after Reset with a confirmed identity" is a claim about calls, not about
/// outcomes. Thread-safe enough for a unit's Task.Run.</summary>
internal sealed class FakeCatalog : ICatalogProvider
{
    private readonly object _gate = new();

    public FakeCatalog(IdentifierNamespace ns, string displayName = "Fake")
    {
        Namespace = ns;
        DisplayName = displayName;
    }

    public IdentifierNamespace Namespace { get; }
    public string DisplayName { get; }

    public Func<string, CatalogSearchResult> Title { get; set; } = _ => CatalogSearchResult.NoMatch();
    public Func<string, CatalogSearchResult> AlternativeName { get; set; } = _ => CatalogSearchResult.NoMatch();
    public Func<LauncherIdentifier, CatalogSearchResult> Map { get; set; } = _ => CatalogSearchResult.NoMatch();
    public Func<CatalogMatch, LauncherIdentifier, LauncherConsistency> Consistency { get; set; } = (_, _) => LauncherConsistency.Unknown;
    public Func<string, CatalogCoverResult> Cover { get; set; } = _ => new CatalogCoverResult(CoverLookupStatus.Resolved, TestBitmaps.Cover(), false);

    /// <summary>What the id-keyed cache holds for an id (the cache-only read the resolver uses to tell whether a recorded cover still
    /// has an image). Default: an empty cache.</summary>
    public Func<string, BitmapImage?> CachedCover { get; set; } = _ => null;
    public Func<string, IReadOnlyList<CatalogCandidate>> Candidates { get; set; } = _ => Array.Empty<CatalogCandidate>();
    public Func<string, IReadOnlyList<CoverChoice>> Covers { get; set; } = _ => Array.Empty<CoverChoice>();
    public Func<string, byte[]?> Download { get; set; } = _ => null;

    /// <summary>Runs (on the calling thread) just before each cover fetch - lets a test interleave a user operation
    /// between "the unit started" and "the unit finished".</summary>
    public Action<string>? OnCover { get; set; }

    public List<string> SearchedTitles { get; } = new();
    public List<string> AlternativeSearches { get; } = new();
    public List<LauncherIdentifier> MappedIds { get; } = new();
    public List<string> FetchedIds { get; } = new();
    public List<string> CacheReads { get; } = new();

    public int TitleSearches { get { lock (_gate) return SearchedTitles.Count; } }
    public int CoverFetches { get { lock (_gate) return FetchedIds.Count; } }

    public CatalogSearchResult SearchByTitle(string title, CancellationToken ct)
    {
        lock (_gate) SearchedTitles.Add(title);
        return Title(title);
    }

    public CatalogSearchResult SearchByAlternativeName(string title, CancellationToken ct)
    {
        lock (_gate) AlternativeSearches.Add(title);
        return AlternativeName(title);
    }

    public CatalogSearchResult MapLauncherId(LauncherIdentifier launcherId, CancellationToken ct)
    {
        lock (_gate) MappedIds.Add(launcherId);
        return Map(launcherId);
    }

    public LauncherConsistency CheckLauncherConsistency(CatalogMatch candidate, LauncherIdentifier launcherId, CancellationToken ct) =>
        Consistency(candidate, launcherId);

    public CatalogCoverResult FetchCover(string id, string title, CancellationToken ct)
    {
        lock (_gate) FetchedIds.Add(id);
        OnCover?.Invoke(id);
        return Cover(id);
    }

    public BitmapImage? TryReadCachedCover(string id)
    {
        lock (_gate) CacheReads.Add(id);
        return CachedCover(id);
    }

    public IReadOnlyList<CatalogCandidate> SearchCandidates(string text, CancellationToken ct) => Candidates(text);
    public IReadOnlyList<CoverChoice> ListCovers(string id, CancellationToken ct) => Covers(id);
    public byte[]? DownloadImage(string url, CancellationToken ct) => Download(url);
}

internal static class TestBitmaps
{
    /// <summary>A real, frozen, validated bitmap - what a provider hands the resolver.</summary>
    public static BitmapImage Cover(int width = 60, int height = 90) =>
        ArtworkImageValidator.ValidateProviderBytes(TestImages.Png(width, height), "test")
        ?? throw new InvalidOperationException("the test image should validate");

    /// <summary>Distinguishable images, so a test can tell WHICH cover ended up on a card (decoded width is always 320).</summary>
    public static BitmapImage Distinct(int height) => Cover(60, height);
}

internal static class Games
{
    public static GameEntry Steam(string appId = "1091500", string title = "Cyberpunk 2077") => new()
    {
        Id = $"steam-{appId}", Name = title, ExecutablePath = @"C:\Games\Steam\game.exe", InstallDir = $@"C:\Games\Steam\{title}",
        Source = GameSource.Steam,
    };

    public static GameEntry Manual(string id = "manual-test", string title = "Test Game") => new()
    {
        Id = id, Name = title, ExecutablePath = @"C:\Games\Test\game.exe", InstallDir = @"C:\Games\Test", Source = GameSource.Manual,
    };

    public static GameEntry Ea(string id = "ea-apex", string name = "Apex", string? catalogName = "Apex Legends") => new()
    {
        Id = id, Name = name, CatalogName = catalogName, ExecutablePath = @"C:\Games\EA\apex.exe", InstallDir = @"C:\Games\EA\Apex",
        Source = GameSource.Ea,
    };
}

internal static class Cat
{
    public static readonly IdentifierNamespace Igdb = IdentifierNamespace.IgdbGame;
    public static readonly IdentifierNamespace Sgdb = IdentifierNamespace.SteamGridDbGame;

    public static ProviderIdentity Confirmed(IdentifierNamespace ns, string id, string title, List<LauncherIdentifier>? verified = null) =>
        new() { Namespace = ns, Id = id, Title = title, At = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), VerifiedLauncherIds = verified };

    public static ResolvedIdentity Resolved(IdentityQuery query, IdentifierNamespace ns, string id, string title,
        IdentityTier? tier = null, int version = IdentitySelection.ResolverVersion, string? fingerprint = null) => new()
    {
        Namespace = ns, Id = id, Title = title, Tier = tier ?? IdentityTier.TitleExact,
        EvidenceFingerprint = fingerprint ?? query.Fingerprint, ResolverVersion = version, ResolvedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    public static ArtworkSelection Auto(IdentifierNamespace ns, string id, ArtworkProvider provider = ArtworkProvider.Igdb) => new()
    {
        Provider = provider, ProviderGameId = id, ProviderTitle = "t", RetrievedFrom = ArtworkRetrievalMethod.LocalCache,
        MatchMethod = "TitleExact", IsUserSelected = false, DerivedFrom = new IdentityKey(ns, id),
    };

    public static UnitInput Input(GameEntry game, GameIdentityRecord? prior = null, ArtworkSelection? current = null, bool pinned = false,
        string trigger = "Scan", bool force = false, bool displayed = false) => new()
    {
        GameId = game.Id, Query = IdentityQuery.From(game), Prior = prior, CurrentArtwork = current, ArtworkPinned = pinned,
        Trigger = trigger, Force = force, CurrentArtworkDisplayed = displayed,
    };

    public static ResolutionContext Ctx(IReadOnlyList<ICatalogProvider> providers, string? legacyRoot = null,
        Func<IdentityQuery, CancellationToken, (BitmapImage? Image, bool FromCache)>? launcherArt = null, Func<DateTime>? now = null) => new()
    {
        Providers = providers, LegacyCacheRoot = legacyRoot, LauncherArt = launcherArt, Now = now ?? (() => new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
    };
}

/// <summary>An isolated view model plus the plumbing to publish REAL automatic units through it: units are produced by the
/// same worker method a scan uses (GameScannerService.ResolveGameUnit) and published through ApplyScanResultAsync, so what
/// is tested is the production path, not a parallel copy. Every directory is per-instance and deleted on Dispose - no
/// static overrides, and the real AppData is never touched.</summary>
internal sealed class IdentityHarness : IDisposable
{
    public readonly string DataDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Identity-" + Guid.NewGuid());
    public readonly string AssetDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdentityAssets-" + Guid.NewGuid());
    public readonly string CacheDir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-IdentityCache-" + Guid.NewGuid());
    public readonly LibraryViewModel Vm;
    public readonly FakeCatalog Igdb = new(IdentifierNamespace.IgdbGame, "IGDB");
    public readonly FakeCatalog Sgdb = new(IdentifierNamespace.SteamGridDbGame, "SteamGridDB");
    public ResolutionContext Context;

    /// <summary>Launcher art (Steam CDN) for a query; default: none.</summary>
    public Func<IdentityQuery, CancellationToken, (BitmapImage? Image, bool FromCache)>? LauncherArt { get; set; }

    /// <param name="brokenSettings">Makes every settings Save fail (the data directory path is occupied by a FILE), for the
    /// save-failure rollback cases.</param>
    public IdentityHarness(bool withIgdb = true, bool withSgdb = true, bool brokenSettings = false)
    {
        if (brokenSettings)
            File.WriteAllText(DataDir, "in the way");

        Vm = new LibraryViewModel(new SettingsService(DataDir), new PendingUpdateNotesService(DataDir))
        {
            AssetStoreDirOverrideForTest = AssetDir,
            // A unit that runs without a ResolutionContext must never reach a real network or a real provider.
            ResolutionContextForTest = () => Context!,
            IconFallbackForTest = _ => null,
        };

        var providers = new List<ICatalogProvider>();
        if (withIgdb) providers.Add(Igdb);
        if (withSgdb) providers.Add(Sgdb);
        Context = Cat.Ctx(providers, legacyRoot: CacheDir, launcherArt: (q, ct) => LauncherArt?.Invoke(q, ct) ?? (null, false));
        Vm.ResolutionContextForTest = () => Cat.Ctx(providers, legacyRoot: CacheDir, launcherArt: (q, ct) => LauncherArt?.Invoke(q, ct) ?? (null, false));
    }

    /// <summary>Publishes `games` with no unit at all - the library as the very first scan would leave it.</summary>
    public async Task<GameEntry> Add(GameEntry game)
    {
        await Vm.ApplyScanResultAsync(new ScanResult([game], [], [], [], new Dictionary<string, ArtworkApplyResult>()));
        return game;
    }

    /// <summary>The unit a scan's worker would produce for `game` RIGHT NOW (snapshots captured at this instant - including which
    /// covers are on screen, taken from the view model exactly as RefreshAsync takes it).</summary>
    public ArtworkApplyResult Unit(GameEntry game, ResolutionContext? context = null, CancellationToken ct = default)
        => Unit(game, Vm, context, ct);

    /// <summary>The same, against another view model over the same data (a "restarted" process).</summary>
    public ArtworkApplyResult Unit(GameEntry game, LibraryViewModel vm, ResolutionContext? context = null, CancellationToken ct = default)
    {
        var over = vm.GetOverride(game.Id);
        var snapshot = over is null ? null : new GameOverride
        {
            Identity = over.Identity?.Clone(), IdentityRevision = over.IdentityRevision, DecisionRevision = over.DecisionRevision,
            ArtworkRevision = over.ArtworkRevision, Artwork = over.Artwork,
        };
        // A scratch entry, like a scan's worker (which builds its own GameEntry and never touches the displayed one).
        var scratch = new GameEntry
        {
            Id = game.Id, Name = game.DetectedTitle, CatalogName = game.CatalogName, ExecutablePath = game.ExecutablePath,
            InstallDir = game.InstallDir, Source = game.Source, LaunchUri = game.LaunchUri,
        };
        scratch.Name = game.Name; // the display name may differ; the DETECTED title was captured above and never changes
        return GameScannerService.ResolveGameUnit(scratch, snapshot, over?.ArtworkRevision ?? 0, vm.SnapshotIdentityGenerations(),
            context ?? Context, ct, coverDisplayed: vm.SnapshotDisplayedCovers().Contains(game.Id));
    }

    /// <summary>Publishes previously computed units (possibly STALE ones) for `games`.</summary>
    public Task Publish(IEnumerable<GameEntry> games, params (string Id, ArtworkApplyResult Result)[] units) =>
        Vm.ApplyScanResultAsync(new ScanResult(games.ToList(), [], [], [],
            units.ToDictionary(u => u.Id, u => u.Result)));

    /// <summary>A whole scan of one game: compute the unit, then publish it.</summary>
    public async Task<GameEntry> Scan(GameEntry game)
    {
        var unit = Unit(game);
        await Publish([game], (game.Id, unit));
        return game;
    }

    public GameOverride? Over(string id) => Vm.GetOverride(id);
    public GameIdentityRecord? Record(string id) => Over(id)?.Identity;

    /// <summary>A brand-new GameEntry for the same game - what every real scan publishes (it never re-publishes the instance that is
    /// already on screen, which is also why "previous" and "new" are different objects in the carry-forward logic).</summary>
    public static GameEntry Fresh(GameEntry game)
    {
        var copy = new GameEntry
        {
            Id = game.Id, Name = game.DetectedTitle, CatalogName = game.CatalogName, ExecutablePath = game.ExecutablePath,
            InstallDir = game.InstallDir, Source = game.Source, LaunchUri = game.LaunchUri,
        };
        copy.Name = game.Name;
        return copy;
    }

    /// <summary>The height of the cover currently on the card (a decoded cover is always 320 wide, so the height tells covers apart).</summary>
    public static int? Height(GameEntry game) => game.IsCoverArt ? game.Icon?.PixelHeight : null;

    public void Dispose()
    {
        foreach (var dir in new[] { DataDir, AssetDir, CacheDir })
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }

        if (File.Exists(DataDir))
            File.Delete(DataDir);
    }
}

internal static class JsonAssert
{
    public static void SameStructure(string expected, string actual)
    {
        using var a = JsonDocument.Parse(expected);
        using var b = JsonDocument.Parse(actual);
        Assert.Equal(Canon(a.RootElement), Canon(b.RootElement));
    }

    private static string Canon(JsonElement e) => JsonSerializer.Serialize(e);
}
