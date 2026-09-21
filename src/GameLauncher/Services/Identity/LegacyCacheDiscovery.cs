using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

public enum LegacyDiscoveryStatus
{
    /// <summary>Every check passed: the frozen, validated bitmap may be shown as unverified continuity.</summary>
    Valid,

    /// <summary>The entry is intact but was looked up under a DIFFERENT search name than today's inputs: continuity is
    /// withheld and fresh resolution is due. NOT a verdict on the candidate (R8) - it never creates a rejection.</summary>
    StaleLookup,

    /// <summary>Missing, unreadable, oversized, unverifiable or mismatching: no continuity image, nothing deleted.</summary>
    NoContinuity,
}

public sealed record LegacyDiscovery(LegacyDiscoveryStatus Status, BitmapImage? Image, byte[]? Bytes);

/// <summary>Finds the image the PRE-identity SteamGridDB pipeline cached for a game, so an upgrade does not blank the
/// library while identities are re-verified (design 9.1). It is deliberately narrow and defensive:
///
///  - EXACT filename for the one supported cache version, resolved inside the cache directory (no globbing, no path
///    traversal) - a v12/v11/v14 file is a different, superseded logic era and is ignored;
///  - bounded read, then the shared validator (20 MB / 8000 px / faithful decode) - never CoverArtDecoder alone;
///  - the sidecar is also read under a hard cap and must have Id &gt; 0, a Title, and an ImageSha256 equal to the hash of
///    THESE bytes, and its Id must equal the ProviderGameId settings.json recorded for the game;
///  - a SearchedName that differs from today's search title is stale-lookup EVIDENCE, not a verdict.
///
/// It is READ-ONLY: it never deletes, rewrites or re-keys a legacy file.</summary>
public static class LegacyCacheDiscovery
{
    private sealed record LegacySidecar(int Id, string Title, string SearchedName, string ImageSha256);

    public static LegacyDiscovery Discover(string cacheRoot, string gameId, string settingsProviderGameId, string currentSearchTitle,
        int supportedVersion)
    {
        var none = new LegacyDiscovery(LegacyDiscoveryStatus.NoContinuity, null, null);
        try
        {
            // 1. exact filename for the supported version, inside the cache directory
            if (string.IsNullOrWhiteSpace(gameId) || !string.Equals(Path.GetFileName(gameId), gameId, StringComparison.Ordinal)
                || gameId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return none;
            }

            var imagePath = Path.Combine(cacheRoot, $"{gameId}-v{supportedVersion}.png");
            if (!File.Exists(imagePath))
                return none;

            // 2. bounded read, then the shared validator
            var bytes = ProviderImageIo.ReadBoundedCacheFile(imagePath, "Legacy SteamGridDB cache");
            var image = bytes is null ? null : ArtworkImageValidator.ValidateProviderBytes(bytes, imagePath);
            if (bytes is null || image is null)
                return none;

            // 3. sidecar: bounded, tolerant, and it must describe THESE bytes
            var sidecarText = ProviderImageIo.ReadBoundedSidecarText(imagePath + ".meta.json", "Legacy SteamGridDB cache");
            if (sidecarText is null)
                return none;

            var sidecar = JsonSerializer.Deserialize<LegacySidecar>(sidecarText);
            if (sidecar is null || sidecar.Id <= 0 || string.IsNullOrWhiteSpace(sidecar.Title) || string.IsNullOrWhiteSpace(sidecar.ImageSha256)
                || !string.Equals(sidecar.ImageSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), StringComparison.Ordinal))
            {
                return none;
            }

            // 4. cross-check with settings: the file is the image settings.json actually describes
            if (!string.Equals(sidecar.Id.ToString(), settingsProviderGameId, StringComparison.Ordinal))
                return none;

            // 5. the lookup must have been made under today's inputs, compared exactly as the provider did
            if (!string.Equals(sidecar.SearchedName, currentSearchTitle, StringComparison.Ordinal))
                return new LegacyDiscovery(LegacyDiscoveryStatus.StaleLookup, null, null);

            return new LegacyDiscovery(LegacyDiscoveryStatus.Valid, image, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return none;
        }
    }
}
