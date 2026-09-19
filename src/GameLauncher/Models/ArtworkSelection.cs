namespace GameLauncher.Models;

/// <summary>Where an ArtworkSelection's image came from, as a distinct catalog/product identity -
/// independent of ArtworkRetrievalMethod, which records HOW those bytes were obtained (a Steam image
/// read from Steam's own local cache is a different fact from one downloaded off the CDN, even though
/// both are ArtworkProvider.SteamCdn).</summary>
public enum ArtworkProvider
{
    UserLocalFile,
    SteamGridDb,
    SteamCdn,
}

public enum ArtworkRetrievalMethod
{
    UserSuppliedFile,
    NetworkDownload,
    LocalCache,
}

/// <summary>A specific cover image chosen for a game - either automatically (an exact-title match found
/// during a scan) or explicitly by the user (Change Cover's local-file or Identify-Game flows). Stored on
/// GameOverride.Artwork, keyed by the same stable GameEntry.Id every other override field already uses.
///
/// ProviderGameId identifies which GAME the provider matched; ProviderArtworkRef identifies which
/// SPECIFIC image among that game's available covers was actually picked - a game can have multiple
/// candidate cover images, so the two are deliberately separate fields, not one.
///
/// AssetId/AssetExtension locate the durable local copy every user selection gets (see
/// ArtworkAssetStore) - every user-selected image is copied into app-managed storage immediately on
/// selection, never left depending solely on the separate, version-bumped CoverArtCache an automatic
/// match uses, which is not safe to depend on for something meant to persist indefinitely.
///
/// GameOverride.ArtworkRevision (NOT a field here) is what guards a scan/lookup result against being
/// applied after it's gone stale - see GameOverride's own remarks for why it can't live on this type.</summary>
public sealed class ArtworkSelection
{
    public ArtworkProvider Provider { get; set; }
    public ArtworkRetrievalMethod RetrievedFrom { get; set; }
    public string? ProviderGameId { get; set; }
    public string? ProviderArtworkRef { get; set; }
    public string? ProviderTitle { get; set; }
    public string AssetId { get; set; } = "";
    public string AssetExtension { get; set; } = "";
    public string MatchMethod { get; set; } = "";
    public bool IsUserSelected { get; set; }
    public DateTime SelectedAt { get; set; }
}
