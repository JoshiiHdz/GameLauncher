using GameLauncher.Models;

namespace GameLauncher.Services.Identity;

/// <summary>Migration from v1.18.x settings (design 8.1): lazy, in memory on Load, persisted by the next ordinary Save.
/// IDEMPOTENT - running it twice changes nothing the second time - and non-destructive: no field is rewritten in place,
/// nothing is deleted, no network call is made, and no identity is INFERRED.
///
///  - User-selected covers are untouched, byte for byte; a user cover implies no identity.
///  - Existing automatic SteamGridDB artwork with a provider id becomes a LEGACY ASSOCIATION (Pending): unverified
///    evidence that may permit display continuity of its cached image and nothing else. It is NOT a resolved identity.
///  - Existing automatic Steam CDN artwork gains DerivedFrom = the launcher's own app id (the id is verifiable against
///    the live launcher query every scan, so it needs no legacy association).
///  - Automatic IGDB artwork exists only on the developer's local build and is not migrated: with no DerivedFrom and
///    no legacy association nothing authorizes it, so the next scan re-derives it under the identity pipeline.</summary>
public static class IdentityMigration
{
    /// <returns>Whether anything was added (so a caller may persist it; not required for correctness).</returns>
    public static bool Apply(AppSettings settings, DateTime? now = null)
    {
        var stamp = now ?? DateTime.UtcNow;
        var changed = false;

        foreach (var (gameId, over) in settings.Overrides)
        {
            if (over.Artwork is not { IsUserSelected: false, DerivedFrom: null } art)
                continue;

            if (over.Identity is { IsQuarantined: true })
                continue; // never touch what we could not understand

            if (art.Provider == ArtworkProvider.SteamCdn && ParseSteamAppId(art.ProviderGameId) is { } appId)
            {
                art.DerivedFrom = new IdentityKey(IdentifierNamespace.SteamApp, appId);
                changed = true;
                continue;
            }

            if (art.Provider != ArtworkProvider.SteamGridDb || string.IsNullOrWhiteSpace(art.ProviderGameId))
                continue;

            over.Identity ??= new GameIdentityRecord();
            if (over.Identity.LegacyEvidence.Any(l => l.SourceProvider == art.Provider && l.Id == art.ProviderGameId))
                continue; // already migrated

            over.Identity.LegacyEvidence.Add(new LegacyAssociation
            {
                Namespace = IdentifierNamespace.SteamGridDbGame,
                Id = art.ProviderGameId,
                Title = art.ProviderTitle ?? "",
                SourceProvider = art.Provider,
                MigratedAt = stamp,
                Status = LegacyStatus.Pending,
            });
            changed = true;
        }

        return changed;
    }

    private static string? ParseSteamAppId(string? providerGameId) =>
        providerGameId is { Length: > 6 } id && id.StartsWith("steam-", StringComparison.Ordinal) ? id[6..] : null;
}
