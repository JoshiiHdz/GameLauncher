using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services.Identity;

/// <summary>The ONE artwork-authorization predicate (design 3.3.1, I11). It governs every path by which AUTOMATIC pixels
/// can reach the screen or be reused: publication, cache reuse, carried-forward pixels, restart display, and the
/// removal pass inside Confirm/Reject/Clear/merge. Pinned (user-selected) artwork is never subject to it (I1, I3).
///
/// Evaluated live against SelectActive and the live IdentityQuery - never cached.</summary>
public static class ArtworkAuthorization
{
    /// <param name="legacyContinuityValidated">Only meaningful for migrated legacy artwork (DerivedFrom == null): whether
    /// the legacy cache entry passed the format-specific, bounded, validated discovery (design 9.1). The caller does the
    /// file work; this predicate stays pure.</param>
    public static bool IsAuthorized(ArtworkSelection art, ActiveIdentity active, IdentityQuery query,
        GameIdentityRecord? record, bool legacyContinuityValidated = false)
    {
        if (art.IsUserSelected)
            return true; // pinned artwork is a display decision the user made; identity never overrides it (I1)

        // A record we could not understand can hold a user decision we cannot see. Catalog artwork is never
        // authorized against it; launcher artwork only under the one defined exception (design 6.5).
        if (record is { IsQuarantined: true })
        {
            return art.DerivedFrom is { } q && q.Namespace.IsLauncher
                && query.HasLauncherId(q) && !QuarantinedRawShowsConfirmedIdentity(record.Quarantined);
        }

        if (art.DerivedFrom is { } derived)
        {
            if (derived.Namespace.IsLauncher)
            {
                // D7: with no confirmed identity a verified launcher id may authorize launcher art; once the user has
                // confirmed a catalog identity the art must be PROVEN consistent with it. Unproven = not authorized.
                if (!query.HasLauncherId(derived))
                    return false;

                if (record?.Confirmed is not { } confirmed)
                    return true;

                return confirmed.VerifiedLauncherIds?.Any(l => l.Namespace == derived.Namespace && l.Id == derived.Id) == true;
            }

            return IdentitySelection.UserDecisionConsistent(derived.Namespace, derived.Id, record)
                && active.Entries.Any(e => e.Namespace == derived.Namespace && e.Id == derived.Id && e.ArtworkEligible);
        }

        // Migrated legacy artwork (no DerivedFrom): display continuity only.
        if (record is null || record.Confirmed is not null || !legacyContinuityValidated)
            return false; // continuity ENDS the moment the user confirms an identity (3.4)

        var legacy = record.LegacyEvidence.FirstOrDefault(l =>
            l.Status == LegacyStatus.Pending && l.SourceProvider == art.Provider && l.Id == art.ProviderGameId);
        return legacy is not null && IdentitySelection.UserDecisionConsistent(legacy.Namespace, legacy.Id, record);
    }

    /// <summary>Shallow, read-only inspection of a quarantined raw subtree: does it hold a non-null Confirmed? If it
    /// cannot be determined (not an object, unreadable), the answer is "yes" - an opaque record may hold a user
    /// decision, and launcher art that contradicts one is forbidden (D7).</summary>
    public static bool QuarantinedRawShowsConfirmedIdentity(JsonElement? raw)
    {
        if (raw is not { } element || element.ValueKind != JsonValueKind.Object)
            return true;

        return element.TryGetProperty("Confirmed", out var confirmed) && confirmed.ValueKind != JsonValueKind.Null;
    }
}
