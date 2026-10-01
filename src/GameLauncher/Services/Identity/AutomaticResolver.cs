using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.CoverArt;

namespace GameLauncher.Services.Identity;

public enum ArtworkHalfKind { None, Set, ClearAutomatic }

/// <summary>The ARTWORK half of an automatic unit: publish this cover, clear the automatic one, or leave things alone.
/// Never applied on its own - the commit publishes it only through gate G1 and the authorization predicate (6.5).</summary>
public sealed record ArtworkHalf(ArtworkHalfKind Kind, ArtworkSelection? Selection = null, BitmapImage? Image = null, bool LegacyContinuity = false)
{
    public static readonly ArtworkHalf None = new(ArtworkHalfKind.None);
    public static readonly ArtworkHalf Clear = new(ArtworkHalfKind.ClearAutomatic);
}

/// <summary>Everything one automatic unit decided for one game, as value snapshots computed OFF the UI thread.
/// NewRecord is the IDENTITY half (the full intended record); null means the identity half was NOT APPLICABLE (a
/// quarantined record, which is never written). Nothing here touches live state.</summary>
public sealed class UnitOutput
{
    public GameIdentityRecord? NewRecord { get; init; }
    public ArtworkHalf Artwork { get; init; } = ArtworkHalf.None;
    public bool IdentityNotApplicable => NewRecord is null;
}

/// <summary>What a unit is asked to do for one game.</summary>
public sealed class UnitInput
{
    public required string GameId { get; init; }
    public required IdentityQuery Query { get; init; }

    /// <summary>A snapshot of the game's identity record when the unit started (never the live object).</summary>
    public GameIdentityRecord? Prior { get; init; }

    /// <summary>A snapshot of what the game's override says it shows now (null when nothing).</summary>
    public ArtworkSelection? CurrentArtwork { get; init; }

    /// <summary>The pixels of CurrentArtwork are on the card right now. The recorded selection alone is only METADATA: after a
    /// restart, or with a missing/corrupt cache file, it can describe a cover nobody is showing - and an outage must not
    /// suppress a working fallback in favour of pixels that do not exist (I5 protects a cover, not a record of one).</summary>
    public bool CurrentArtworkDisplayed { get; init; }

    public string Trigger { get; init; } = "Scan";

    /// <summary>The game shows a user-pinned cover: identity is still resolved (knowing which game this is has value even
    /// when the display decision was taken away, S22) but no cover is fetched - the artwork half could never publish.</summary>
    public bool ArtworkPinned { get; init; }

    /// <summary>Reset's explicit re-attempt: bypasses the negative-result cooldown, never the rejection filters (6.6).</summary>
    public bool Force { get; init; }
}

/// <summary>Per-scan provider circuit breaker (design 4.8): after N consecutive Unavailable outcomes a provider is
/// skipped for the rest of the scan, so an outage costs a few calls, not one failing request per game.</summary>
public sealed class ProviderBreaker
{
    private readonly int _threshold;
    private readonly Dictionary<IdentifierNamespace, int> _consecutive = new();

    public ProviderBreaker(int threshold = 3) => _threshold = threshold;

    public bool ShouldSkip(IdentifierNamespace ns) => _consecutive.GetValueOrDefault(ns) >= _threshold;

    public void Note(IdentifierNamespace ns, bool unavailable) =>
        _consecutive[ns] = unavailable ? _consecutive.GetValueOrDefault(ns) + 1 : 0;
}

/// <summary>Bounds the number of legacy revalidations per scan so a large library cannot burst a provider's rate limit.</summary>
public sealed class ResolutionBudget
{
    private int _remaining;
    public ResolutionBudget(int revalidations) => _remaining = revalidations;

    public bool TryConsume()
    {
        if (_remaining <= 0)
            return false;

        _remaining--;
        return true;
    }
}

public sealed class ResolutionContext
{
    /// <summary>Catalogs in preference order: IGDB (primary) then SteamGridDB (fallback).</summary>
    public required IReadOnlyList<ICatalogProvider> Providers { get; init; }

    /// <summary>Launcher-derived art (Steam CDN, keyed by the launcher's own app id): the image and whether it came from
    /// the cache, or null. Never searches by name. Null seam = no launcher art.</summary>
    public Func<IdentityQuery, CancellationToken, (BitmapImage? Image, bool FromCache)>? LauncherArt { get; init; }

    /// <summary>Where the LEGACY name-keyed SteamGridDB cache lives (CoverArtCache root); null disables discovery.</summary>
    public string? LegacyCacheRoot { get; init; }

    public ProviderBreaker Breaker { get; init; } = new();
    public ResolutionBudget Budget { get; init; } = new(int.MaxValue);
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// <summary>How long a negative result (NoMatch/Ambiguous/Contradicted) for unchanged inputs is trusted before the
    /// providers are asked again. Reset bypasses it.</summary>
    public TimeSpan NegativeCooldown { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>The automatic resolution UNIT (design 6.5): per game, off the UI thread, resolve identity, then fetch art for
/// the resolved identity BY ID (no second title search when an id is known). It is a pure function of its inputs: it
/// reads only the snapshots it is handed and returns value snapshots - the two halves the UI-thread commit validates.
///
/// Policy in one place:
///  - a user-confirmed identity is never re-derived; its own provider supplies art by id, others only fall back by its
///    canonical TITLE and are recorded as ARTWORK SOURCES, never as identity (7.1);
///  - with no confirmed identity, each consulted provider resolves independently against the DETECTED inputs
///    (id-mapping first, then exact-unique title, with rejections and store-id contradictions applied) and a fresh
///    resolved identity is STICKY;
///  - an outage (Unavailable) never clears or downgrades what exists (I5);
///  - legacy (pre-identity) matches are revalidated, never trusted (I10, 8.1).</summary>
public static class AutomaticResolver
{
    public static UnitOutput Run(UnitInput input, ResolutionContext ctx, CancellationToken ct)
    {
        var query = input.Query;
        var record = input.Prior?.Clone() ?? new GameIdentityRecord();
        if (record.IsQuarantined)
            return RunQuarantined(input, ctx, ct);

        var signature = string.Join(",", ctx.Providers.Select(p => p.Namespace.Value));
        var inCooldown = !input.Force && IsInNegativeCooldown(record, query, ctx, signature);
        if (inCooldown && record.LastAttempt is { } cooledAttempt)
        {
            // Without this a skipped game logs nothing at all, and "why no cover?" has no visible answer.
            var left = ctx.NegativeCooldown - (ctx.Now() - cooledAttempt.At);
            Logger.Info($"  identity: '{query.DetectedTitle}' - catalogs not asked again: cooldown, last result {cooledAttempt.Outcome}, "
                + $"{FormatRemaining(left)} left (same inputs; Reset lookup asks again now).");
        }
        var outcomes = new List<LookupOutcome>();
        var legacyArtFailed = false;

        // ---- A. LEGACY REVALIDATION (8.1): a legacy match is unverified evidence, judged against the CURRENT inputs ----
        if (!inCooldown)
            legacyArtFailed = RevalidateLegacy(input, ctx, record, outcomes, ct);

        // ---- B. CATALOG IDENTITY + ART ----
        ArtworkHalf? published = null;
        ArtworkHalf? nonPortraitFallback = null;
        var sawUnavailable = false;
        var sawDefiniteNegative = false;
        var ambiguousSeen = false;

        // Whether any lookup actually ran. A unit that only honoured the cooldown asked nobody anything and must not rewrite the
        // record of the attempt that DID ask (its outcome and its timestamp are the evidence the cooldown stands on).
        //
        // A confirmed identity with a pinned cover has nothing left for any catalog to decide (the identity is the user's) or supply
        // (the cover is the user's): asking anyone would only cost a request per scan to record an art source that is never shown.
        var nothingToDecide = input.ArtworkPinned && record.Confirmed is not null;
        var attempted = !inCooldown && !nothingToDecide;

        // Launcher-derived art is asked for at most once per unit (the outage rule below may need it before step C does).
        ArtworkHalf? launcherArt = null;
        var launcherArtTried = false;
        ArtworkHalf? LauncherArtOnce()
        {
            if (!launcherArtTried)
            {
                launcherArtTried = true;
                launcherArt = TryLauncherArt(query, record, ctx, ct);
            }

            return launcherArt;
        }

        // The current cover's pixels when they are NOT on the card but can still be had: validated in its own source's cache (a
        // catalog id is read cache-only, no network), or Steam CDN's cache-first fetch. Null = the recorded cover has no usable image.
        ArtworkHalf? restored = null;
        var restoreTried = false;
        bool CurrentCoverIsUsable(ArtworkSelection current, IdentityKey from)
        {
            if (input.CurrentArtworkDisplayed)
                return true;

            if (!restoreTried)
            {
                restoreTried = true;
                if (from.Namespace == IdentifierNamespace.SteamApp)
                    restored = LauncherArtOnce();
                else if (ctx.Providers.FirstOrDefault(p => p.Namespace == from.Namespace)?.TryReadCachedCover(from.Id) is { } cached)
                {
                    var selection = current.Clone();
                    selection.RetrievedFrom = ArtworkRetrievalMethod.LocalCache;
                    restored = new ArtworkHalf(ArtworkHalfKind.Set, selection, cached);
                }
            }

            return restored is not null;
        }

        // I5 (design 4.8): an outage of a higher-priority source never downgrades. While something above has been unavailable, an
        // existing, still-authorized automatic cover from a DIFFERENT source stays - but only a cover that has usable pixels (on the
        // card, or validated in its cache). A recorded selection with no image behind it is not a cover to protect: the fallback runs.
        bool PreservesCurrent(IdentifierNamespace sourceNamespace) =>
            sawUnavailable
            && input.CurrentArtwork is { IsUserSelected: false, DerivedFrom: { } from } current
            && from.Namespace != sourceNamespace
            && ArtworkAuthorization.IsAuthorized(current, IdentitySelection.SelectActive(record, query), query, record)
            && CurrentCoverIsUsable(current, from);

        if (!nothingToDecide && (!inCooldown || IdentitySelection.SelectActive(record, query).Primary is not null))
        {
            attempted = true;
            var ordered = OrderProviders(ctx.Providers, record.Confirmed);
            foreach (var provider in ordered)
            {
                ct.ThrowIfCancellationRequested();
                if (ctx.Breaker.ShouldSkip(provider.Namespace))
                {
                    Logger.Warn($"  identity: {provider.DisplayName} skipped for '{query.DetectedTitle}' - it failed several lookups in a row, so it is not asked again this scan.");
                    sawUnavailable = true;
                    continue;
                }

                // Ambiguity is a settled fact about the NAME (design 4.7): once one title-search provider found it ambiguous,
                // the others must not go on to guess independently against the same name. (An identity that already exists,
                // or one the user confirmed, is not a guess and is unaffected.)
                if (ambiguousSeen && record.Confirmed is null
                    && IdentitySelection.SelectActive(record, query).IdFor(provider.Namespace) is null)
                {
                    Logger.Info($"  identity: {provider.DisplayName} not asked for '{query.DetectedTitle}' - the title was already ambiguous at another catalog, "
                        + "and a second independent search must not guess against the same name.");
                    outcomes.Add(LookupOutcome.Ambiguous);
                    sawDefiniteNegative = true;
                    continue;
                }

                var entry = EnsureEntryFor(provider, query, record, ctx, outcomes, ct);
                if (entry is null)
                {
                    var last = outcomes.LastOrDefault();
                    if (last == LookupOutcome.Unavailable) sawUnavailable = true;
                    else sawDefiniteNegative = true;
                    if (last == LookupOutcome.Ambiguous) ambiguousSeen = true;
                    continue;
                }

                if (input.ArtworkPinned)
                    continue; // identity recorded above; nothing here could ever publish over a pinned cover

                if (PreservesCurrent(provider.Namespace))
                {
                    Logger.Info($"  art: '{query.DetectedTitle}' keeps its current cover - a higher-priority catalog was unavailable, so the {provider.DisplayName} fallback is not fetched over it.");
                    continue; // identity above is recorded; the fallback's art is not fetched over a working cover
                }

                var cover = provider.FetchCover(entry.Id, entry.Title, ct);
                ctx.Breaker.Note(provider.Namespace, cover.Status == CoverLookupStatus.Unavailable);
                if (cover.Image is not null)
                {
                    var candidateArt = new ArtworkHalf(ArtworkHalfKind.Set, BuildSelection(provider, entry, cover.FromCache, record), cover.Image);
                    if (cover.Image.PixelHeight > cover.Image.PixelWidth)
                    {
                        Logger.Info($"  art: {provider.DisplayName} cover for '{entry.Title}' (id {entry.Id}) {cover.Image.PixelWidth}x{cover.Image.PixelHeight}"
                            + (cover.FromCache ? " (cached)" : string.Empty) + " - used.");
                        published = candidateArt;
                        break;
                    }

                    Logger.Info($"  art: {provider.DisplayName} cover for '{entry.Title}' (id {entry.Id}) is {cover.Image.PixelWidth}x{cover.Image.PixelHeight}, "
                        + "not portrait - asking the next catalog for a portrait one first.");

                    // A valid catalog identity need not supply poster-shaped art. Keep its image as
                    // a fallback, but let the next catalog offer a verified portrait before stopping.
                    // Explicit user-selected covers bypass this loop unchanged.
                    nonPortraitFallback ??= candidateArt;
                    continue;
                }

                Logger.Warn($"  art: {provider.DisplayName} had no usable cover for '{entry.Title}' (id {entry.Id}): {cover.Status}.");
                if (cover.Status == CoverLookupStatus.Unavailable) sawUnavailable = true;
                else sawDefiniteNegative = true;
            }
        }

        // Re-check authorization: consulting another catalog may have changed the active identity.
        if (published is null && nonPortraitFallback is { Selection: { } fallbackSelection })
        {
            if (ArtworkAuthorization.IsAuthorized(fallbackSelection, IdentitySelection.SelectActive(record, query), query, record))
            {
                Logger.Info($"  art: no catalog had a portrait cover for '{query.DetectedTitle}' - using the non-portrait {fallbackSelection.Provider} cover for "
                    + $"'{fallbackSelection.ProviderTitle}'.");
                published = nonPortraitFallback;
            }
            else
            {
                Logger.Warn($"  art: the non-portrait {fallbackSelection.Provider} cover for '{fallbackSelection.ProviderTitle}' is not authorized "
                    + $"for '{query.DetectedTitle}' any more (the active identity changed) - not showing it.");
            }
        }

        // ---- C. LAUNCHER-DERIVED ART (Steam CDN): keyed by the launcher's own id, and only where authorized (D7) ----
        if (published is null && !input.ArtworkPinned && !PreservesCurrent(IdentifierNamespace.SteamApp))
            published = LauncherArtOnce();

        // The outage rule kept a cover whose pixels are not on the card, only in its cache: put them back.
        if (published is null && !input.ArtworkPinned && restored is not null)
            published = restored;

        // ---- D. LEGACY CONTINUITY: only while nothing fresh exists ----
        var continuity = published is null && !input.ArtworkPinned ? TryLegacyContinuity(input, ctx, record) : null;
        published ??= continuity;

        // ---- E. ARTWORK HALF + the attempt record ----
        if (attempted)
        {
            record.LastAttempt = new ResolutionAttempt
            {
                Outcome = Aggregate(outcomes, ctx.Providers.Count, record, query, sawUnavailable),
                At = ctx.Now(),
                Fingerprint = query.Fingerprint,
                ResolverVersion = IdentitySelection.ResolverVersion,
                Trigger = $"{input.Trigger};p={signature}",
            };
        }

        ArtworkHalf half;
        if (input.ArtworkPinned)
            half = ArtworkHalf.None;
        else if (published is not null)
            half = published;
        else if (input.CurrentArtwork is { IsUserSelected: false } && ((sawDefiniteNegative && !sawUnavailable) || legacyArtFailed))
            half = ArtworkHalf.Clear;        // a definite "no art anywhere" - or its derivation just failed revalidation
        else
            half = ArtworkHalf.None;         // an outage or an unconfigured provider says nothing: leave what exists (I5)

        return new UnitOutput { NewRecord = record, Artwork = half };
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Quarantined record: identity half NOT APPLICABLE; the one defined exception is launcher-derived art (6.5).

    private static UnitOutput RunQuarantined(UnitInput input, ResolutionContext ctx, CancellationToken ct)
    {
        var half = TryLauncherArt(input.Query, input.Prior!, ctx, ct) ?? ArtworkHalf.None;
        return new UnitOutput { NewRecord = null, Artwork = half };
    }

    // ------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<ICatalogProvider> OrderProviders(IReadOnlyList<ICatalogProvider> providers, ProviderIdentity? confirmed) =>
        confirmed is null
            ? providers
            : providers.OrderBy(p => p.Namespace == confirmed.Namespace ? 0 : 1); // the confirmed identity's own catalog supplies art first

    /// <summary>The identity entry (Identity or ArtworkSource) `provider` should fetch art for, resolving and recording one
    /// if there is none yet. Null when the provider has nothing usable (the outcome is appended to `outcomes`).</summary>
    private static ActiveEntry? EnsureEntryFor(ICatalogProvider provider, IdentityQuery query, GameIdentityRecord record,
        ResolutionContext ctx, List<LookupOutcome> outcomes, CancellationToken ct)
    {
        var active = IdentitySelection.SelectActive(record, query);
        if (active.IdFor(provider.Namespace) is { } existing)
        {
            outcomes.Add(LookupOutcome.Resolved);
            return existing;
        }

        if (record.Confirmed is { } confirmed)
        {
            // The user chose a game in ANOTHER catalog. This provider may only try the confirmed CANONICAL TITLE to
            // generate a candidate, and records the hit as an artwork source - never as identity (7.1).
            if (confirmed.Namespace == provider.Namespace)
            {
                outcomes.Add(LookupOutcome.Resolved);
                return new ActiveEntry(confirmed.Namespace, confirmed.Id, confirmed.Title, EntryRole.Identity, EntryAuthority.User, true, null);
            }

            var result = provider.SearchByTitle(confirmed.Title, ct);
            ctx.Breaker.Note(provider.Namespace, result.Status == CatalogStatus.Unavailable);
            if (result.Status != CatalogStatus.Match || result.Match is null)
            {
                outcomes.Add(ToOutcome(result.Status));
                return null;
            }

            if (!IdentitySelection.UserDecisionConsistent(provider.Namespace, result.Match.Id, record))
            {
                outcomes.Add(LookupOutcome.Contradicted); // rejected, or a competing id in the confirmed namespace
                return null;
            }

            record.ArtworkSources.RemoveAll(s => s.Namespace == provider.Namespace);
            record.ArtworkSources.Add(new ArtworkSourceAssociation
            {
                Namespace = provider.Namespace,
                Id = result.Match.Id,
                Title = result.Match.Title,
                ResolverVersion = IdentitySelection.ResolverVersion,
                ResolvedAt = ctx.Now(),
                Basis = confirmed.Key,
            });
            outcomes.Add(LookupOutcome.Resolved);
            return IdentitySelection.SelectActive(record, query).IdFor(provider.Namespace);
        }

        // No confirmed identity: an ordinary, independent resolution against the DETECTED inputs.
        var resolution = ResolveAuto(provider, query, record, ctx, ct);
        outcomes.Add(resolution.Outcome);
        if (resolution.Entry is null)
            return null;

        record.Resolved.RemoveAll(r => r.Namespace == provider.Namespace);
        record.Resolved.Add(resolution.Entry);
        return IdentitySelection.SelectActive(record, query).IdFor(provider.Namespace);
    }

    private sealed record NsResolution(LookupOutcome Outcome, ResolvedIdentity? Entry);

    /// <summary>Resolves `provider`'s identity for these inputs (no confirmed identity exists). Fresh and sticky first; then
    /// the id path (authoritative for its namespace); then the exact-unique title path with contradictions applied.</summary>
    private static NsResolution ResolveAuto(ICatalogProvider provider, IdentityQuery query, GameIdentityRecord record,
        ResolutionContext ctx, CancellationToken ct)
    {
        if (IdentitySelection.FreshResolved(record, query, provider.Namespace) is { } sticky)
            return new NsResolution(LookupOutcome.Resolved, sticky);

        ResolvedIdentity Entry(CatalogMatch match, IdentityTier tier) => new()
        {
            Namespace = provider.Namespace,
            Id = match.Id,
            Title = match.Title,
            Tier = tier,
            EvidenceFingerprint = query.Fingerprint,
            ResolverVersion = IdentitySelection.ResolverVersion,
            ResolvedAt = ctx.Now(),
        };

        bool Rejected(CatalogMatch m) => !IdentitySelection.UserDecisionConsistent(provider.Namespace, m.Id, record);

        // ---- ID path (4.2): a store id the launcher supplied, mapped by the provider itself ----
        foreach (var launcherId in query.LauncherIds)
        {
            var mapped = provider.MapLauncherId(launcherId, ct);
            if (mapped.Status == CatalogStatus.Ambiguous)
            {
                Logger.Warn($"  identity: {provider.DisplayName}: store id {launcherId.Namespace}:{launcherId.Id} maps to more than one game ({mapped.Note ?? "no reason given"}).");
                return new NsResolution(LookupOutcome.Ambiguous, null);
            }

            if (mapped.Status == CatalogStatus.Match && mapped.Match is { } idMatch)
            {
                if (Rejected(idMatch))
                {
                    Logger.Warn($"  identity: {provider.DisplayName}: store id {launcherId.Namespace}:{launcherId.Id} maps to '{idMatch.Title}' (id {idMatch.Id}), "
                        + "which you rejected earlier - not used.");
                    return new NsResolution(LookupOutcome.Contradicted, null);
                }

                Logger.Info($"  identity: {provider.DisplayName}: store id {launcherId.Namespace}:{launcherId.Id} -> '{idMatch.Title}' (id {idMatch.Id}).");
                return new NsResolution(LookupOutcome.Resolved, Entry(idMatch, IdentityTier.IdMapped));
            }
            // NoMatch / Unavailable here never blocks the title path: the id path can only ADD a mapping.
        }

        // ---- Title path (4.3): exact and unique, then contradictions (4.4) ----
        var found = provider.SearchByTitle(query.SearchTitle, ct);
        ctx.Breaker.Note(provider.Namespace, found.Status == CatalogStatus.Unavailable);

        // ---- Alternative-name path (4.5): only when the primary title said NoMatch. The provider's own alternative-name data,
        // queried exhaustively, must name exactly ONE game; several is Ambiguous, none NoMatch. Never a similarity guess. ----
        var viaAlternativeName = false;
        if (found.Status == CatalogStatus.NoMatch)
        {
            var alternative = provider.SearchByAlternativeName(query.SearchTitle, ct);
            ctx.Breaker.Note(provider.Namespace, alternative.Status == CatalogStatus.Unavailable);
            if (alternative.Status != CatalogStatus.NoMatch)
            {
                found = alternative;
                viaAlternativeName = alternative.Status == CatalogStatus.Match;
            }
        }

        LogTitleLookup(provider, query, found, viaAlternativeName);

        if (found.Status != CatalogStatus.Match || found.Match is not { } match)
            return new NsResolution(ToOutcome(found.Status), null);

        if (Rejected(match))
        {
            Logger.Warn($"  identity: {provider.DisplayName}: '{match.Title}' (id {match.Id}) matched '{query.SearchTitle}' but you rejected it earlier - not used.");
            return new NsResolution(LookupOutcome.Contradicted, null);
        }

        var tier = viaAlternativeName ? IdentityTier.AlternativeName : IdentityTier.TitleExact;
        foreach (var launcherId in query.LauncherIds)
        {
            switch (provider.CheckLauncherConsistency(match, launcherId, ct))
            {
                case LauncherConsistency.Contradicted:
                    // A unique exact title is NOT accepted when the launcher's own id says it is a different game.
                    Logger.Warn($"  identity: {provider.DisplayName}: '{match.Title}' (id {match.Id}) matches the title '{query.SearchTitle}' but the launcher's own id "
                        + $"{launcherId.Namespace}:{launcherId.Id} points at a different game - not used.");
                    return new NsResolution(LookupOutcome.Contradicted, null);
                case LauncherConsistency.Consistent:
                    tier = IdentityTier.TitleExactCorroborated;
                    break;
            }
        }

        return new NsResolution(LookupOutcome.Resolved, Entry(match, tier));
    }

    private static LookupOutcome ToOutcome(CatalogStatus status) => status switch
    {
        CatalogStatus.Match => LookupOutcome.Resolved,
        CatalogStatus.Ambiguous => LookupOutcome.Ambiguous,
        CatalogStatus.Unavailable => LookupOutcome.Unavailable,
        _ => LookupOutcome.NoMatch,
    };

    private static ArtworkSelection BuildSelection(ICatalogProvider provider, ActiveEntry entry, bool fromCache, GameIdentityRecord record) => new()
    {
        Provider = provider.Namespace == IdentifierNamespace.IgdbGame ? ArtworkProvider.Igdb : ArtworkProvider.SteamGridDb,
        RetrievedFrom = fromCache ? ArtworkRetrievalMethod.LocalCache : ArtworkRetrievalMethod.NetworkDownload,
        ProviderGameId = entry.Id,
        ProviderTitle = entry.Title,
        MatchMethod = entry.Authority == EntryAuthority.User ? "UserConfirmed"
            : entry.Role == EntryRole.ArtworkSource ? "ConfirmedTitle"
            : record.Resolved.FirstOrDefault(r => r.Namespace == entry.Namespace && r.Id == entry.Id)?.Tier.Value ?? "TitleExact",
        IsUserSelected = false,
        SelectedAt = DateTime.UtcNow,
        DerivedFrom = new IdentityKey(entry.Namespace, entry.Id),
    };

    // ------------------------------------------------------------------------------------------------------------------

    private static ArtworkHalf? TryLauncherArt(IdentityQuery query, GameIdentityRecord record, ResolutionContext ctx, CancellationToken ct)
    {
        var steamId = query.LauncherIds.FirstOrDefault(l => l.Namespace == IdentifierNamespace.SteamApp);
        if (steamId is null || ctx.LauncherArt is null)
            return null;

        var selection = new ArtworkSelection
        {
            Provider = ArtworkProvider.SteamCdn,
            ProviderGameId = $"steam-{steamId.Id}",
            MatchMethod = "SteamAppId",
            IsUserSelected = false,
            SelectedAt = DateTime.UtcNow,
            DerivedFrom = new IdentityKey(steamId.Namespace, steamId.Id),
        };

        // D7: launcher art is authorized only when its launcher id is live and, after a user confirmation, proven
        // consistent with it. Checked BEFORE any fetch so unauthorized art costs nothing.
        if (!ArtworkAuthorization.IsAuthorized(selection, IdentitySelection.SelectActive(record, query), query, record))
            return null;

        var (image, fromCache) = ctx.LauncherArt(query, ct);
        if (image is null)
            return null;

        selection.RetrievedFrom = fromCache ? ArtworkRetrievalMethod.LocalCache : ArtworkRetrievalMethod.NetworkDownload;
        return new ArtworkHalf(ArtworkHalfKind.Set, selection, image);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Legacy (pre-identity) matches: revalidate (8.1), then - only while nothing fresh exists - show continuity (9.1).

    /// <summary>Applies 8.1's table to every legacy association. Returns whether a failed association's continuity artwork
    /// must be cleared.</summary>
    private static bool RevalidateLegacy(UnitInput input, ResolutionContext ctx, GameIdentityRecord record,
        List<LookupOutcome> outcomes, CancellationToken ct)
    {
        var artworkFailed = false;
        foreach (var legacy in record.LegacyEvidence.ToList())
        {
            var provider = ctx.Providers.FirstOrDefault(p => p.Namespace == legacy.Namespace);
            if (provider is null || ctx.Breaker.ShouldSkip(provider.Namespace) || !ctx.Budget.TryConsume())
                continue; // an outage or an unconfigured provider preserves the existing status (S27)

            var resolution = ResolveAuto(provider, input.Query, record, ctx, ct);
            outcomes.Add(resolution.Outcome);
            var usesThisArtwork = input.CurrentArtwork is { IsUserSelected: false, DerivedFrom: null } art
                && art.Provider == legacy.SourceProvider && art.ProviderGameId == legacy.Id;

            if (resolution.Outcome == LookupOutcome.Unavailable)
                continue;

            if (resolution.Entry is { } fresh)
            {
                record.LegacyEvidence.Remove(legacy);
                record.Resolved.RemoveAll(r => r.Namespace == provider.Namespace);
                record.Resolved.Add(fresh);
                if (fresh.Id != legacy.Id && usesThisArtwork)
                    artworkFailed = true; // the fresh, different identity replaces the derived artwork (fetched by id below)
                continue;
            }

            // NoMatch / Ambiguous / Contradicted: failed - removed even with no replacement.
            record.LegacyEvidence.Remove(legacy);
            if (usesThisArtwork)
                artworkFailed = true;
        }

        return artworkFailed;
    }

    private static ArtworkHalf? TryLegacyContinuity(UnitInput input, ResolutionContext ctx, GameIdentityRecord record)
    {
        if (ctx.LegacyCacheRoot is null || record.Confirmed is not null
            || input.CurrentArtwork is not { IsUserSelected: false, DerivedFrom: null } art || art.ProviderGameId is null)
        {
            return null;
        }

        var legacy = record.LegacyEvidence.FirstOrDefault(l => l.SourceProvider == art.Provider && l.Id == art.ProviderGameId);
        if (legacy is null || legacy.Status != LegacyStatus.Pending)
            return null;

        var found = LegacyCacheDiscovery.Discover(ctx.LegacyCacheRoot, input.GameId, art.ProviderGameId, input.Query.SearchTitle,
            SteamGridDbCoverArtProvider.CacheVersionForTest);
        switch (found.Status)
        {
            case LegacyDiscoveryStatus.Valid:
                return new ArtworkHalf(ArtworkHalfKind.Set, art, found.Image, LegacyContinuity: true);
            case LegacyDiscoveryStatus.StaleLookup:
                // Kept, not removed and NOT a rejection (R8): the continuity image is withheld until fresh resolution decides.
                record.LegacyEvidence.Remove(legacy);
                record.LegacyEvidence.Add(legacy with { Status = LegacyStatus.StaleLookup });
                return null;
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------

    internal static string FormatRemaining(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
            return "0m";
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    /// <summary>The one line that says what a catalog answered for a title - including WHY it was ambiguous or unavailable
    /// (the provider's own note), which was previously visible nowhere once a verdict came from the identity path.</summary>
    private static void LogTitleLookup(ICatalogProvider provider, IdentityQuery query, CatalogSearchResult found, bool viaAlternativeName)
    {
        var how = viaAlternativeName ? " (alternative name)" : string.Empty;
        switch (found.Status)
        {
            case CatalogStatus.Match when found.Match is { } m:
                Logger.Info($"  identity: {provider.DisplayName}: '{query.SearchTitle}' -> '{m.Title}' (id {m.Id}){how}.");
                break;
            case CatalogStatus.Ambiguous:
                Logger.Warn($"  identity: {provider.DisplayName}: '{query.SearchTitle}' is ambiguous - {found.Note ?? "no reason given"}; "
                    + "no cover is guessed, and the other catalog is not asked either.");
                break;
            case CatalogStatus.Unavailable:
                Logger.Warn($"  identity: {provider.DisplayName}: '{query.SearchTitle}' could not be looked up ({found.Note ?? "unavailable"}); nothing is cleared.");
                break;
            default:
                Logger.Info($"  identity: {provider.DisplayName}: no confident match for '{query.SearchTitle}'{(found.Note is null ? "" : " - " + found.Note)}.");
                break;
        }
    }

    private static bool IsInNegativeCooldown(GameIdentityRecord record, IdentityQuery query, ResolutionContext ctx, string signature)
    {
        var attempt = record.LastAttempt;
        if (attempt is null
            || attempt.Fingerprint != query.Fingerprint
            || attempt.ResolverVersion != IdentitySelection.ResolverVersion
            || attempt.Trigger is null || !attempt.Trigger.EndsWith($";p={signature}", StringComparison.Ordinal)) // a newly configured provider ends it
        {
            return false;
        }

        var negative = attempt.Outcome == LookupOutcome.NoMatch || attempt.Outcome == LookupOutcome.Ambiguous || attempt.Outcome == LookupOutcome.Contradicted;
        return negative && ctx.Now() - attempt.At < ctx.NegativeCooldown;
    }

    private static LookupOutcome Aggregate(List<LookupOutcome> outcomes, int providerCount, GameIdentityRecord record, IdentityQuery query,
        bool sawUnavailable)
    {
        if (providerCount == 0)
            return LookupOutcome.NotConfigured;
        if (outcomes.Contains(LookupOutcome.Resolved) || IdentitySelection.SelectActive(record, query).Primary is not null)
            return LookupOutcome.Resolved;
        if (outcomes.Contains(LookupOutcome.Ambiguous))
            return LookupOutcome.Ambiguous;
        if (outcomes.Contains(LookupOutcome.Contradicted))
            return LookupOutcome.Contradicted;
        if (outcomes.Count > 0 && outcomes.All(o => o == LookupOutcome.Unavailable))
            return LookupOutcome.Unavailable;
        if (outcomes.Count == 0)
            return sawUnavailable ? LookupOutcome.Unavailable : LookupOutcome.NotConfigured; // every provider skipped by the breaker is an outage
        return LookupOutcome.NoMatch;
    }
}
