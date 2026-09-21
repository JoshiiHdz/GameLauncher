using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

public enum CatalogStatus
{
    /// <summary>Exactly one confident catalog entry.</summary>
    Match,

    /// <summary>The provider answered and has nothing confident for this. A statement about the catalog.</summary>
    NoMatch,

    /// <summary>More than one equally confident entry (or a hub/umbrella name): the identity is not guessable.</summary>
    Ambiguous,

    /// <summary>The provider could not answer (auth, network, timeout, malformed). Says NOTHING about the catalog (I5).</summary>
    Unavailable,
}

public sealed record CatalogMatch(string Id, string Title);

public sealed record CatalogSearchResult(CatalogStatus Status, CatalogMatch? Match = null, string? Note = null)
{
    public static CatalogSearchResult NoMatch(string? note = null) => new(CatalogStatus.NoMatch, null, note);
    public static CatalogSearchResult Ambiguous(string? note = null) => new(CatalogStatus.Ambiguous, null, note);
    public static CatalogSearchResult Unavailable(string? note = null) => new(CatalogStatus.Unavailable, null, note);
    public static CatalogSearchResult Found(string id, string title) => new(CatalogStatus.Match, new CatalogMatch(id, title));
}

/// <summary>The outcome of fetching one catalog entry's cover by ID (never by title).</summary>
public sealed record CatalogCoverResult(CoverLookupStatus Status, BitmapImage? Image, bool FromCache);

/// <summary>A candidate shown in the Identify Game picker.</summary>
public sealed record CatalogCandidate(IdentifierNamespace Namespace, string Id, string Title, string? Detail, string? ThumbnailUrl);

/// <summary>One cover image a catalog entry offers (Choose Cover).</summary>
public sealed record CoverChoice(string Ref, string ImageUrl, string? ThumbnailUrl);

/// <summary>Whether a title-path candidate agrees with the launcher's own id for the same game (design 4.4).</summary>
public enum LauncherConsistency
{
    /// <summary>The provider reports nothing that settles it (no external ids, or it could not answer). NOT a contradiction.</summary>
    Unknown,

    Consistent,

    /// <summary>The provider reports the candidate's id for that store and it is a DIFFERENT id than the launcher's.</summary>
    Contradicted,
}

/// <summary>A catalog the resolver can ask for identity and art. One implementation per provider (IGDB, SteamGridDB);
/// tests supply fakes. Every method is a provider BOUNDARY: a failure is reported as a status, never an exception -
/// except the caller's own cancellation, which always propagates (it is never a lookup outcome).</summary>
public interface ICatalogProvider
{
    IdentifierNamespace Namespace { get; }
    string DisplayName { get; }

    /// <summary>Title-path identity: exact collapsed title and UNIQUE among exact matches (design 4.3).</summary>
    CatalogSearchResult SearchByTitle(string title, CancellationToken ct);

    /// <summary>ID-path identity (design 4.2): does the provider map this launcher id to one of its own games? Returns
    /// NoMatch for a launcher namespace it cannot map. The mapping is authoritative for THIS provider's namespace only.</summary>
    CatalogSearchResult MapLauncherId(LauncherIdentifier launcherId, CancellationToken ct);

    /// <summary>Hard-contradiction check for a TITLE match (design 4.4): the launcher says store id N; does the candidate's
    /// own external id for that store agree? Default: Unknown (a provider that cannot say never contradicts).</summary>
    LauncherConsistency CheckLauncherConsistency(CatalogMatch candidate, LauncherIdentifier launcherId, CancellationToken ct) =>
        LauncherConsistency.Unknown;

    /// <summary>Verified alternative-title identity (design 4.5): an EXHAUSTIVE exact query on the provider's alternative-name
    /// data. Exactly one game has this name -> Match; none -> NoMatch; several (or too many rows to prove uniqueness) ->
    /// Ambiguous. Only consulted after the primary-title path found NoMatch. Default: a provider with no such data says NoMatch.</summary>
    CatalogSearchResult SearchByAlternativeName(string title, CancellationToken ct) => CatalogSearchResult.NoMatch();

    /// <summary>The cover for catalog entry `id`, by id, through the identity-bound cache. Never searches by title.</summary>
    CatalogCoverResult FetchCover(string id, string title, CancellationToken ct);

    /// <summary>The validated cover for `id` from the identity-bound cache ONLY: no network, never a search. Null on a miss (absent,
    /// corrupt or unverifiable). Lets the resolver tell whether a recorded cover still has usable pixels when the provider is down.
    /// Default: a provider with no cache has nothing to read.</summary>
    BitmapImage? TryReadCachedCover(string id) => null;

    /// <summary>Candidates for the picker: free-text, ranked, NOT restricted to exact titles. Throws on failure.</summary>
    IReadOnlyList<CatalogCandidate> SearchCandidates(string text, CancellationToken ct);

    /// <summary>The covers catalog entry `id` offers, for Choose Cover. Throws on failure.</summary>
    IReadOnlyList<CoverChoice> ListCovers(string id, CancellationToken ct);

    /// <summary>Downloads one image (bounded, deadline, cancellable) from a URL this provider listed. Null when it
    /// cannot be fetched or is not an address this provider is allowed to serve.</summary>
    byte[]? DownloadImage(string url, CancellationToken ct);
}
