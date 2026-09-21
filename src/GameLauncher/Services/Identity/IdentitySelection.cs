using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

public enum EntryRole { Identity, ArtworkSource }

public enum EntryAuthority { User, Automatic }

/// <summary>One entry of the ACTIVE identity set. An ineligible entry stays in the list (it is evidence, and reported)
/// but is excluded by the artwork predicate - it is never filtered out, so it cannot be mistaken for "absent".</summary>
public sealed record ActiveEntry(
    IdentifierNamespace Namespace, string Id, string Title, EntryRole Role, EntryAuthority Authority,
    bool ArtworkEligible, string? IneligibleReason)
{
    public IdentityKey Key => new(Namespace, Id);
}

/// <summary>What SelectActive decided. Key covers IDENTITY entries only (it is what IdentityRevision tracks and what
/// identity dialogs depend on); AuthKey adds eligibility and artwork sources (what artwork publication depends on).</summary>
public sealed class ActiveIdentity
{
    public required IReadOnlyList<ActiveEntry> Entries { get; init; }
    public ActiveEntry? Primary { get; init; }
    public required string Key { get; init; }
    public required string AuthKey { get; init; }

    /// <summary>The eligible entry in `ns`, or null. An ineligible entry is never returned.</summary>
    public ActiveEntry? IdFor(IdentifierNamespace ns) =>
        Entries.FirstOrDefault(e => e.Namespace == ns && e.ArtworkEligible);

    public bool IsEmpty => Entries.Count == 0;
}

public enum IdentityState { UserConfirmed, AutoResolved, Detected, Unresolved }

/// <summary>The four required states, derived from facts - never stored (3.3).</summary>
public sealed record IdentityStateInfo(IdentityState State, string? UnresolvedReason);

public static class IdentitySelection
{
    /// <summary>Resolver versions below this are known-defective: every entry they produced is retired without a data
    /// migration (design 4.8). Bump when a resolver defect is found.</summary>
    public const int MinTrustedResolverVersion = 1;

    /// <summary>The current resolver version stamped on every automatic entry.</summary>
    public const int ResolverVersion = 1;

    // What the keys below evaluate to for a record with no entries at all.
    private const string EmptyKey = "|P=";
    private const string EmptyAuthKey = "|P=||";

    /// <summary>THE single source of truth for what is active (design 3.3). Pure, total and deterministic; every
    /// consumer calls it and nothing reads Resolved directly. LegacyEvidence is never consulted (I10).</summary>
    public static ActiveIdentity SelectActive(GameIdentityRecord? record, IdentityQuery query)
    {
        // "No record", "an empty record" and "a record we cannot read" all mean the same thing for what is ACTIVE - nothing -
        // so they share ONE key. (They used to differ, so adding the first rejection to a game with no record looked like
        // the active identity had changed and spuriously advanced IdentityRevision.)
        if (record is null || record.IsQuarantined)
            return new ActiveIdentity { Entries = Array.Empty<ActiveEntry>(), Primary = null, Key = EmptyKey, AuthKey = EmptyAuthKey };

        // ---- 1. IDENTITY ENTRIES ----
        var identity = new List<(ActiveEntry Entry, int Tier, int NsPriority, DateTime At)>();
        if (record.Confirmed is { } confirmed)
        {
            // Never filtered by Rejected, staleness or contradiction (a contradiction may be surfaced as a warning only).
            identity.Add((new ActiveEntry(confirmed.Namespace, confirmed.Id, confirmed.Title, EntryRole.Identity,
                EntryAuthority.User, true, null), int.MaxValue, 0, confirmed.At));
        }

        foreach (var resolved in record.Resolved)
        {
            if (resolved.EvidenceFingerprint != query.Fingerprint) continue;                    // stale
            if (resolved.ResolverVersion < MinTrustedResolverVersion) continue;               // known-defective resolver
            if (IsRejected(record, resolved.Namespace, resolved.Id)) continue;                 // rejected by the user
            if (record.Confirmed is { } c && c.Namespace == resolved.Namespace && c.Id == resolved.Id) continue; // the confirmed one itself

            identity.Add((new ActiveEntry(resolved.Namespace, resolved.Id, resolved.Title, EntryRole.Identity,
                EntryAuthority.Automatic, true, null), resolved.Tier.Rank, resolved.Namespace.AutomaticPriority, resolved.ResolvedAt));
        }

        // ---- 2. PRIMARY: the confirmed entry, else the highest-authority automatic entry ----
        // Authority: Confirmed (User) > automatic by (tier desc, namespace priority, ResolvedAt desc).
        var ordered = identity
            .OrderByDescending(e => e.Tier)
            .ThenBy(e => e.NsPriority)
            .ThenByDescending(e => e.At)
            .ThenBy(e => e.Entry.Namespace.Value, StringComparer.Ordinal)
            .ThenBy(e => e.Entry.Id, StringComparer.Ordinal)
            .Select(e => e.Entry)
            .ToList();
        var primary = ordered.FirstOrDefault();

        // ---- 3. ELIGIBILITY, always judged RELATIVE TO THE PRIMARY ----
        var canonicalPrimaryTitle = primary is null ? "" : SteamGridDbCoverArtProvider.CollapsedTitle(primary.Title);
        var entries = new List<ActiveEntry>();
        foreach (var e in ordered)
        {
            if (primary is null || ReferenceEquals(e, primary))
            {
                entries.Add(e);
                continue;
            }

            if (e.Namespace == primary.Namespace && e.Id != primary.Id)
            {
                // Two catalog entries in one namespace are two DIFFERENT products even when their titles are
                // identical: namespace-and-id, never title, decides within a namespace.
                entries.Add(e with { ArtworkEligible = false, IneligibleReason = "SameNamespaceDifferentId" });
            }
            else if (e.Namespace != primary.Namespace
                && !string.Equals(SteamGridDbCoverArtProvider.CollapsedTitle(e.Title), canonicalPrimaryTitle, StringComparison.Ordinal))
            {
                // No equivalence is ever inferred (there is no evidence store for it yet): different namespace and a
                // different canonical title means we cannot say these describe one product.
                entries.Add(e with { ArtworkEligible = false, IneligibleReason = "Disagreement" });
            }
            else
            {
                entries.Add(e);
            }
        }

        // ---- 4. ARTWORK-SOURCE ENTRIES (never identity, never in Key) ----
        var confirmedKey = record.Confirmed?.Key;
        foreach (var source in record.ArtworkSources)
        {
            if (confirmedKey is null || source.Basis != confirmedKey) continue;                // its basis no longer stands
            if (source.ResolverVersion < MinTrustedResolverVersion) continue;
            if (IsRejected(record, source.Namespace, source.Id)) continue;
            if (source.Namespace == confirmedKey.Namespace && source.Id != confirmedKey.Id) continue; // a competing id in the confirmed namespace

            entries.Add(new ActiveEntry(source.Namespace, source.Id, source.Title, EntryRole.ArtworkSource,
                EntryAuthority.Automatic, true, null));
        }

        // ---- 5. KEYS ----
        var identityEntries = entries.Where(e => e.Role == EntryRole.Identity).ToList();
        var key = string.Join("|", identityEntries
                .Select(e => $"{e.Namespace}:{e.Id}:{e.Authority}")
                .OrderBy(k => k, StringComparer.Ordinal))
            + "|P=" + (primary is null ? "" : $"{primary.Namespace}:{primary.Id}");
        var authKey = key + "||" + string.Join("|", entries
            .Select(e => $"{e.Namespace}:{e.Id}:{e.Role}:{e.ArtworkEligible}")
            .OrderBy(k => k, StringComparer.Ordinal));

        return new ActiveIdentity { Entries = entries, Primary = primary, Key = key, AuthKey = authKey };
    }

    /// <summary>The automatic identity in `ns` that is still trusted for these inputs (same filters SelectActive applies to
    /// Resolved entries: current fingerprint, trusted resolver version, not rejected), or null. This is what makes a
    /// resolved identity STICKY - and what stops a stale or rejected one from ever being reused.</summary>
    public static ResolvedIdentity? FreshResolved(GameIdentityRecord record, IdentityQuery query, IdentifierNamespace ns) =>
        record.Resolved.FirstOrDefault(r => r.Namespace == ns
            && r.EvidenceFingerprint == query.Fingerprint
            && r.ResolverVersion >= MinTrustedResolverVersion
            && !IsRejected(record, r.Namespace, r.Id));

    private static bool IsRejected(GameIdentityRecord record, IdentifierNamespace ns, string id) =>
        record.Rejected.Any(r => r.Namespace == ns && r.Id == id);

    /// <summary>The shared clause every catalog-identified branch requires (design 3.3.1), so a user decision cannot
    /// be bypassed by a branch that forgot to check it: the entry is not rejected, and no OTHER id in the confirmed
    /// identity's own namespace is ever consistent with it, whatever its title.</summary>
    public static bool UserDecisionConsistent(IdentifierNamespace ns, string id, GameIdentityRecord? record)
    {
        if (record is null)
            return true;

        if (IsRejected(record, ns, id))
            return false;

        return record.Confirmed is null || record.Confirmed.Namespace != ns || record.Confirmed.Id == id;
    }

    /// <summary>Derives one of the four states from SelectActive plus the recorded facts.</summary>
    public static IdentityStateInfo DeriveState(GameIdentityRecord? record, IdentityQuery query, ActiveIdentity? active = null)
    {
        if (record is { IsQuarantined: true })
            return new IdentityStateInfo(IdentityState.Unresolved, "Quarantined");

        active ??= SelectActive(record, query);
        if (record?.Confirmed is not null)
            return new IdentityStateInfo(IdentityState.UserConfirmed, null);
        if (active.Primary is not null)
            return new IdentityStateInfo(IdentityState.AutoResolved, null);

        var outcome = record?.LastAttempt?.Outcome;
        if (record is { LegacyEvidence.Count: > 0 } && outcome is null)
            return new IdentityStateInfo(IdentityState.Unresolved, "PendingRevalidation");
        if (outcome is null)
        {
            return query.LauncherIds.Count > 0 && record?.LastAttempt is null
                ? new IdentityStateInfo(IdentityState.Detected, null)
                : new IdentityStateInfo(IdentityState.Unresolved, "NeverAttempted");
        }

        return new IdentityStateInfo(IdentityState.Unresolved, outcome.Value.Value);
    }
}
