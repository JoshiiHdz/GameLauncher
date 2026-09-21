namespace GameLauncher.Services.CoverArt;

/// <summary>What an automatic cover-art provider lookup actually concluded - an explicit status instead of
/// a pair of bool flags, because CoverArtService.Apply's fallback behavior AND its diagnostics both depend
/// on telling these apart: an earlier flag-based version couldn't distinguish "the provider rejected the
/// search" from "the provider was never reachable", so a network/auth failure was logged as "no confident
/// match".
///
/// Shared by every name-searching provider (IgdbCoverArtProvider, SteamGridDbCoverArtProvider) so the two
/// cannot drift apart in what these values mean, and so one conformance suite can assert the same contract
/// against both (CoverProviderConformanceTests). Formerly named for IGDB alone.</summary>
public enum CoverLookupStatus
{
    /// <summary>A unique, confident match with a validated cover (freshly fetched or a valid cache hit).</summary>
    Resolved,

    /// <summary>The search completed and no candidate was a confident match. The provider genuinely doesn't
    /// (or couldn't be shown to) have this game - a fallback provider may try independently.</summary>
    NoMatch,

    /// <summary>More than one distinct candidate was independently confident, or the name is a known
    /// multi-title umbrella. The IDENTITY is unresolved - not equivalent to "no artwork available" - so no
    /// other name-searching provider may guess against the same name.</summary>
    Ambiguous,

    /// <summary>The game was confidently identified but the provider has no usable cover for it (none
    /// listed, missing on the CDN, over the byte cap, or the image failed validation).</summary>
    IdentifiedWithoutUsableArt,

    /// <summary>The provider could not complete the lookup - authentication, network, timeout, rate-limit
    /// exhaustion, or a malformed response. Says NOTHING about whether the provider has the game.</summary>
    Unavailable,
}
