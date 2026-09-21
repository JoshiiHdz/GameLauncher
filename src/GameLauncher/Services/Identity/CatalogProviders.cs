using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

/// <summary>IGDB as a catalog: the PRIMARY provider. Reached with the user's own credentials from Settings, or - with none - through the
/// project's relay (see DefaultIgdbRelay), which holds the shared credentials server-side.</summary>
public sealed class IgdbCatalog : ICatalogProvider
{
    private readonly IgdbCoverArtProvider _provider;
    private readonly string? _cacheDirOverride;

    public IgdbCatalog(IgdbCoverArtProvider provider, string? cacheDirOverride = null)
    {
        _provider = provider;
        _cacheDirOverride = cacheDirOverride;
    }

    public IdentifierNamespace Namespace => IdentifierNamespace.IgdbGame;
    public string DisplayName => "IGDB";

    public CatalogSearchResult SearchByTitle(string title, CancellationToken ct) => _provider.SearchByTitleForIdentity(title, ct);

    public CatalogSearchResult SearchByAlternativeName(string title, CancellationToken ct) => _provider.SearchByAlternativeName(title, ct);

    public CatalogSearchResult MapLauncherId(LauncherIdentifier launcherId, CancellationToken ct) =>
        IgdbCoverArtProvider.ExternalSourceNameFor(launcherId.Namespace) is { } source
            ? _provider.MapExternalId(source, launcherId.Id, ct)
            : CatalogSearchResult.NoMatch("no id mapping for this launcher");

    public LauncherConsistency CheckLauncherConsistency(CatalogMatch candidate, LauncherIdentifier launcherId, CancellationToken ct) =>
        IgdbCoverArtProvider.ExternalSourceNameFor(launcherId.Namespace) is { } source
            ? _provider.CheckExternalConsistency(candidate.Id, source, launcherId.Id, ct)
            : LauncherConsistency.Unknown;

    public CatalogCoverResult FetchCover(string id, string title, CancellationToken ct) =>
        _provider.FetchCoverForId(id, title, _cacheDirOverride, ct);

    public BitmapImage? TryReadCachedCover(string id) => _provider.ReadCachedCoverForId(id, _cacheDirOverride);

    public IReadOnlyList<CatalogCandidate> SearchCandidates(string text, CancellationToken ct) =>
        _provider.SearchCandidatesForPicker(text, ct);

    public IReadOnlyList<CoverChoice> ListCovers(string id, CancellationToken ct) => _provider.ListCoversForId(id, ct);

    public byte[]? DownloadImage(string url, CancellationToken ct) => _provider.DownloadPickerImage(url, ct);
}

/// <summary>SteamGridDB as a catalog: the FALLBACK provider (identity by exact unique title; art by its own game id).</summary>
public sealed class SteamGridDbCatalog : ICatalogProvider
{
    private readonly SteamGridDbCoverArtProvider _provider;
    private readonly string? _cacheDirOverride;

    public SteamGridDbCatalog(SteamGridDbCoverArtProvider provider, string? cacheDirOverride = null)
    {
        _provider = provider;
        _cacheDirOverride = cacheDirOverride;
    }

    public IdentifierNamespace Namespace => IdentifierNamespace.SteamGridDbGame;
    public string DisplayName => "SteamGridDB";

    public CatalogSearchResult SearchByTitle(string title, CancellationToken ct) => _provider.SearchByTitleForIdentity(title, ct);

    // SteamGridDB's platform-id lookup is unverified here, and Steam art is already keyed by app id through Steam CDN.
    public CatalogSearchResult MapLauncherId(LauncherIdentifier launcherId, CancellationToken ct) =>
        CatalogSearchResult.NoMatch("no id mapping for this launcher");

    public CatalogCoverResult FetchCover(string id, string title, CancellationToken ct) =>
        _provider.FetchCoverForId(id, title, _cacheDirOverride, ct);

    public BitmapImage? TryReadCachedCover(string id) => _provider.ReadCachedCoverForId(id, _cacheDirOverride);

    public IReadOnlyList<CatalogCandidate> SearchCandidates(string text, CancellationToken ct) =>
        _provider.SearchCandidatesForPicker(text, ct);

    public IReadOnlyList<CoverChoice> ListCovers(string id, CancellationToken ct) => _provider.ListCoversForId(id, ct);

    public byte[]? DownloadImage(string url, CancellationToken ct) => _provider.DownloadPickerImage(url, ct);
}

/// <summary>Builds the ordered provider list (IGDB primary, SteamGridDB fallback) from VALUE snapshots of the
/// configuration - never live settings, which a background worker must not read.</summary>
public static class CatalogProviders
{
    /// <param name="relayOverride">Test seam: where the build's own IGDB relay address comes from. Null means the real one embedded in this
    /// assembly, so production callers pass nothing; a test passes <c>() =&gt; null</c> to stay independent of what the machine that
    /// built it happened to embed.</param>
    public static IReadOnlyList<ICatalogProvider> Create(string? igdbClientId, string? igdbClientSecret, string? steamGridDbApiKey,
        string? cacheDirOverride = null, Func<RelayEndpoint?>? relayOverride = null)
    {
        var providers = new List<ICatalogProvider>();
        var igdb = IgdbAccess.Resolve(igdbClientId, igdbClientSecret, relayOverride is null ? DefaultIgdbRelay.Current : relayOverride());
        if (igdb.Kind == IgdbAccessKind.UserCredentials)
            providers.Add(new IgdbCatalog(new IgdbCoverArtProvider(igdb.Credentials!.ClientId, igdb.Credentials.ClientSecret), cacheDirOverride));
        else if (igdb.Kind == IgdbAccessKind.SharedRelay)
            providers.Add(new IgdbCatalog(new IgdbCoverArtProvider(igdb.Relay!), cacheDirOverride));

        // A key entered in Settings wins; otherwise the key compiled into the build, exactly as CoverArtService did.
        var apiKey = string.IsNullOrWhiteSpace(steamGridDbApiKey) ? DefaultApiKey.SteamGridDb : steamGridDbApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
            providers.Add(new SteamGridDbCatalog(new SteamGridDbCoverArtProvider(apiKey), cacheDirOverride));

        return providers;
    }
}
