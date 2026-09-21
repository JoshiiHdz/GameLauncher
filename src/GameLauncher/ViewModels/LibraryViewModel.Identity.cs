using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Serialization;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;

namespace GameLauncher.ViewModels;

// The identity half of the view model. Identity (WHICH GAME an entry is) and artwork (WHICH IMAGE it shows) are two
// separate decisions with their own revisions and commit rules - see docs/design/identity-artwork-pipeline.md, sections
// 6 and 7, which this file implements. Every mutation of live identity state happens in ONE synchronous UI-thread method
// (no await inside), which is what lets the single dispatcher serialize them without a lock.
public partial class LibraryViewModel
{
    // ---- Session-only identity generation and merge aliases (design 6.1, 6.4) ----------------------------------------
    //
    // The generation advances on EVERY write to a game's identity record - user, merge or automatic, including
    // metadata-only ones. It is what detects a late OLDER automatic unit without inventing a persisted counter for every
    // timestamp. It is deliberately not persisted: it only has to outlive in-flight work.
    private readonly Dictionary<string, long> _identityGeneration = new();

    // loser -> winner, from MergedGameIds, so work (or a dialog) holding a merged-away id can still find its game.
    private readonly Dictionary<string, string> _mergeAliases = new();

    /// <summary>Test seam: replaces the real catalog providers, launcher art and legacy cache for every unit this view model
    /// starts (Reset, Confirm follow-ups) and for RefreshAsync's scan. Production leaves it null.</summary>
    internal Func<ResolutionContext>? ResolutionContextForTest { get; set; }

    /// <summary>Test seam: which IGDB relay (if any) this view model's catalog providers see, instead of the address embedded in this
    /// build. Per instance, so a test's result never depends on what the machine that built it happened to embed - and never reaches the
    /// real relay. Production leaves it null (the embedded address).</summary>
    internal Func<RelayEndpoint?>? RelayOverrideForTest { get; set; }

    private long IdentityGenerationOf(string gameId) => _identityGeneration.GetValueOrDefault(gameId);

    private void BumpIdentityGeneration(string gameId) => _identityGeneration[gameId] = IdentityGenerationOf(gameId) + 1;

    /// <summary>The generation map a scan captures when it STARTS (a copy - the scan reads it from a worker thread).</summary>
    internal IReadOnlyDictionary<string, long> SnapshotIdentityGenerations() => new Dictionary<string, long>(_identityGeneration);

    /// <summary>The games whose card shows cover pixels right now - what lets a scan tell a cover worth protecting through an outage
    /// (its pixels are on screen) from a recorded one that has no image behind it (a restart with its cache gone).</summary>
    internal IReadOnlySet<string> SnapshotDisplayedCovers() =>
        _allGames.Where(g => g.IsCoverArt && g.Icon is not null).Select(g => g.Id).ToHashSet();

    private string ResolveAlias(string gameId)
    {
        for (var hops = 0; hops < 16 && _mergeAliases.TryGetValue(gameId, out var winner); hops++)
            gameId = winner;
        return gameId;
    }

    internal long IdentityGenerationForTest(string gameId) => IdentityGenerationOf(gameId);

    /// <summary>Test hook: persists the live settings now (RefreshAsync does this after a scan; a test that drives
    /// ApplyScanResultAsync directly does not).</summary>
    internal bool SaveNowForTest() => _settingsService.Save(_settings);

    /// <summary>Test setup: the live override for `gameId`, creating an empty one if there is none - lets a test arrange
    /// identity state directly (a hand-preserved file, an exhausted counter) without a full round trip per setup step.</summary>
    internal GameOverride EnsureOverrideForTest(string gameId)
    {
        if (!_settings.Overrides.TryGetValue(gameId, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        return over;
    }

    // ---- The automatic unit's commit (design 6.5) ------------------------------------------------------------------------

    internal enum IdentityHalf
    {
        Committed, ValidatedNoChange, Superseded, RejectedRevision, RejectedFingerprint, RejectedExhausted, NotApplicable,
    }

    internal sealed record AutomaticCommitReport(IdentityHalf Identity, bool ArtworkPublished);

    /// <summary>What each game's most recent automatic unit committed (test observability: the algorithm's outcome is a value
    /// it carries, not something inferred from id comparisons).</summary>
    internal Dictionary<string, AutomaticCommitReport> UnitReportsForTest { get; } = new();

    private static bool IsValidated(IdentityHalf half) => half is IdentityHalf.Committed or IdentityHalf.ValidatedNoChange;

    private static bool IdentityRecordsEqual(GameIdentityRecord? a, GameIdentityRecord? b)
    {
        static string Canon(GameIdentityRecord? r) =>
            JsonSerializer.Serialize(r ?? new GameIdentityRecord(), IdentityJson.Inner);
        return string.Equals(Canon(a), Canon(b), StringComparison.Ordinal);
    }

    /// <summary>Publishes one game's automatic unit on the UI thread. The IDENTITY half runs first and produces exactly one
    /// outcome; the ARTWORK half then publishes only through the explicit gates (6.5):
    ///   G1 the identity half validated (an id match proves nothing) - the one narrow exception is a launcher-derived Set
    ///      on a still-current quarantined record;
    ///   G2 the artwork is authorized against the identity ACTUALLY ACTIVE after step 3 (the single predicate, I11);
    ///   G3 the artwork revision is unchanged;  G4 the game is not pinned.
    /// `inPlace` is a follow-up unit for an already-displayed game (Reset/Confirm): the display is left alone unless the
    /// unit publishes or the removal pass must remove something.</summary>
    private AutomaticCommitReport CommitAutomaticUnit(GameEntry game, ArtworkApplyResult scanResult, AutomaticUnitResult unit,
        GameEntry? previousLiveGame, Dictionary<string, PreparedCoverRestore> decodedById, bool inPlace)
    {
        _settings.Overrides.TryGetValue(game.Id, out var over);
        var liveQuery = IdentityQuery.From(game);
        var identityHalf = CommitIdentityHalf(game.Id, ref over, unit, liveQuery);

        // Pinned (a user-selected cover): the identity half above still applied (knowing the game has value even when the
        // display decision was taken away, S22); the artwork half is dropped (G4) and the pinned cover is shown as before.
        if (over?.Artwork is { IsUserSelected: true })
        {
            if (!inPlace)
                ReconcileArtwork(game, over, null, previousLiveGame, decodedById);
            RefreshIdentityBadge(game);
            return Report(game.Id, identityHalf, false);
        }

        var record = over?.Identity;
        var active = IdentitySelection.SelectActive(record, liveQuery);
        var half = unit.Output.Artwork;
        var artworkRevisionCurrent = (over?.ArtworkRevision ?? 0) == scanResult.AsOfRevision;
        var g1 = IsValidated(identityHalf)
            || (identityHalf == IdentityHalf.NotApplicable && half.Kind == ArtworkHalfKind.Set && half.Selection?.DerivedFrom is { Namespace.IsLauncher: true });

        var published = false;
        if (half.Kind == ArtworkHalfKind.Set && half.Selection is { } selection && half.Image is { } image
            && g1 && artworkRevisionCurrent
            && ArtworkAuthorization.IsAuthorized(selection, active, liveQuery, record, half.LegacyContinuity))
        {
            if (!half.LegacyContinuity)
            {
                if (over is null)
                {
                    over = new GameOverride { ArtworkRevision = scanResult.AsOfRevision };
                    _settings.Overrides[game.Id] = over;
                }

                over.Artwork = selection; // automatic writes never advance ArtworkRevision (unchanged rule)
            }

            game.Icon = image;
            game.IsCoverArt = true;
            published = true;
        }
        else if (half.Kind == ArtworkHalfKind.ClearAutomatic && IsValidated(identityHalf) && artworkRevisionCurrent)
        {
            if (over is not null)
                over.Artwork = null;
            published = true; // a scan's worker already put the exe icon on the entry (its half was not a Set)...
            if (inPlace)
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest); // ...a follow-up unit did not
        }

        if (!published && !inPlace)
        {
            // The worker's provisional cover (if any) was NOT authorized: never show it.
            if (unit.WorkerShowedArt)
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);

            // Carried-forward pixels: the last known-good frame is reused only if the predicate holds for it NOW.
            if (over?.Artwork is { IsUserSelected: false } existing && previousLiveGame is { Icon: not null, IsCoverArt: true }
                && ArtworkAuthorization.IsAuthorized(existing, active, liveQuery, record, legacyContinuityValidated: true))
            {
                game.Icon = previousLiveGame.Icon;
                game.IsCoverArt = true;
            }
        }

        // REMOVAL PASS: automatic artwork that is no longer authorized is removed here too (never pinned artwork, I1) -
        // this is what stops an ineligible identity's cover from surviving a rescan or a restart (S33-S46).
        RemoveUnauthorizedAutomaticArtwork(game, over, liveQuery, legacyContinuityValidated: published && half.LegacyContinuity);
        RefreshIdentityBadge(game);

        return Report(game.Id, identityHalf, published);
    }

    private AutomaticCommitReport Report(string gameId, IdentityHalf identityHalf, bool published)
    {
        var report = new AutomaticCommitReport(identityHalf, published);
        UnitReportsForTest[gameId] = report;
        return report;
    }

    private void RemoveUnauthorizedAutomaticArtwork(GameEntry game, GameOverride? over, IdentityQuery query, bool legacyContinuityValidated)
    {
        if (over?.Artwork is not { IsUserSelected: false } art)
            return;

        var active = IdentitySelection.SelectActive(over.Identity, query);
        if (ArtworkAuthorization.IsAuthorized(art, active, query, over.Identity, legacyContinuityValidated))
            return;

        over.Artwork = null;
        CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
    }

    /// <summary>Step 2 and 3 of 6.5. The COMMON currency checks run first, in this order, for every record - including a
    /// quarantined one; only a still-current unit can ever reach NotApplicable.</summary>
    private IdentityHalf CommitIdentityHalf(string gameId, ref GameOverride? over, AutomaticUnitResult unit, IdentityQuery liveQuery)
    {
        if (IdentityGenerationOf(gameId) != unit.IdentityGeneration)
            return IdentityHalf.Superseded;                       // any identity write since the unit started: discard it whole

        var liveRevision = over?.IdentityRevision ?? 0;
        if (liveRevision != unit.IdentityRevision)
            return IdentityHalf.RejectedRevision;

        if (!string.Equals(liveQuery.Fingerprint, unit.Query.Fingerprint, StringComparison.Ordinal))
            return IdentityHalf.RejectedFingerprint;              // the inputs changed (a rescan with a different detected title)

        var live = over?.Identity;
        if (live is { IsQuarantined: true } || unit.Output.NewRecord is null)
            return IdentityHalf.NotApplicable;                    // cannot be validated or written

        var scratch = unit.Output.NewRecord.Clone();
        var keyChanged = IdentitySelection.SelectActive(live, liveQuery).Key != IdentitySelection.SelectActive(scratch, liveQuery).Key;
        var nextRevision = liveRevision;
        if (keyChanged && !TryGetNextRevision(liveRevision, out nextRevision))
        {
            Logger.Error($"'{gameId}': identity revision counter exhausted - rejecting this automatic identity change.");
            return IdentityHalf.RejectedExhausted;                // before anything is mutated
        }

        if (IdentityRecordsEqual(live, scratch))
            return IdentityHalf.ValidatedNoChange;

        if (over is null)
        {
            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        over.Identity = scratch;
        if (keyChanged)
            over.IdentityRevision = nextRevision;
        BumpIdentityGeneration(gameId);
        return IdentityHalf.Committed;
    }

    // ---- Following up: Reset and Confirm start a SEPARATE, ordinary automatic unit ----------------------------------------

    private ResolutionContext BuildResolutionContext()
    {
        if (ResolutionContextForTest is { } factory)
            return factory();

        // Value snapshots taken here, on the UI thread, before any background work.
        return new ResolutionContext
        {
            Providers = CatalogProviders.Create(_settings.IgdbClientId, _credentialStore.LoadSecret(), _settings.SteamGridDbApiKey,
                relayOverride: RelayOverrideForTest),
            LauncherArt = SteamLauncherArt.Fetch,
            LegacyCacheRoot = System.IO.Path.Combine(AppPaths.DataDir, "CoverArtCache"),
        };
    }

    /// <summary>One automatic unit for one already-displayed game, committed through exactly the same algorithm a scan uses.
    /// This is how Reset (Trigger=Reset, cooldown bypassed) and Confirm/Clear pick up the result of the identity change they
    /// just made - as a SEPARATE transaction, never as part of the artwork or identity commit itself (I2, 6.6).</summary>
    internal async Task RunAutomaticUnitAsync(string gameId, string trigger, bool force, CancellationToken ct = default)
    {
        gameId = ResolveAlias(gameId);
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return;

        _settings.Overrides.TryGetValue(gameId, out var over);
        var query = IdentityQuery.From(game);
        var generation = IdentityGenerationOf(gameId);
        var identityRevision = over?.IdentityRevision ?? 0;
        var artworkRevision = over?.ArtworkRevision ?? 0;
        var input = new UnitInput
        {
            GameId = gameId,
            Query = query,
            Prior = over?.Identity?.Clone(),
            CurrentArtwork = over?.Artwork is { } a ? CloneArtwork(a) : null,
            CurrentArtworkDisplayed = game.IsCoverArt && game.Icon is not null,
            ArtworkPinned = over?.Artwork is { IsUserSelected: true },
            Trigger = trigger,
            Force = force,
        };
        var context = BuildResolutionContext();

        UnitOutput output;
        try
        {
            output = await Task.Run(() => AutomaticResolver.Run(input, context, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"'{game.Name}': automatic identity/cover resolution ({trigger}) failed unexpectedly.", ex);
            return; // best-effort: whatever was committed before it stands
        }

        // The worker finished, but the caller may have cancelled while it ran (Task.Run's token only stops a task that has not
        // started). Its result is discarded here, on the UI thread, before anything is committed or saved.
        ct.ThrowIfCancellationRequested();

        // Back on the UI thread. Re-resolve: a refresh may have replaced or merged the game while the unit ran.
        gameId = ResolveAlias(gameId);
        var live = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (live is null)
            return;

        var unit = new AutomaticUnitResult(output, query, identityRevision, generation, WorkerShowedArt: false);
        CommitAutomaticUnit(live, new ArtworkApplyResult(artworkRevision, output.Artwork.Selection, unit), unit,
            previousLiveGame: null, decodedById: new Dictionary<string, PreparedCoverRestore>(), inPlace: true);
        _settingsService.Save(_settings); // best effort - automatic results are re-derivable
    }

    private static ArtworkSelection CloneArtwork(ArtworkSelection a) => new()
    {
        Provider = a.Provider, RetrievedFrom = a.RetrievedFrom, ProviderGameId = a.ProviderGameId, ProviderArtworkRef = a.ProviderArtworkRef,
        ProviderTitle = a.ProviderTitle, AssetId = a.AssetId, AssetExtension = a.AssetExtension, MatchMethod = a.MatchMethod,
        IsUserSelected = a.IsUserSelected, SelectedAt = a.SelectedAt, DerivedFrom = a.DerivedFrom,
    };

    // ---- User identity operations (design 6.3, 6.7, 7) ---------------------------------------------------------------------

    /// <summary>What an identity dialog captures when it opens, and what it shows.</summary>
    public sealed record IdentityDialogState(
        string GameId, string DetectedTitle, long IdentityRevision, long DecisionRevision, long ArtworkRevision,
        IdentityState State, string? UnresolvedReason, ActiveIdentity Active, GameIdentityRecord? Record, IdentityQuery Query);

    /// <summary>The current identity state of a game, for the Identify Game dialog (null if the game no longer exists).</summary>
    public IdentityDialogState? GetIdentityDialogState(string gameId)
    {
        gameId = ResolveAlias(gameId);
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return null;

        _settings.Overrides.TryGetValue(gameId, out var over);
        var query = IdentityQuery.From(game);
        var active = IdentitySelection.SelectActive(over?.Identity, query);
        var state = IdentitySelection.DeriveState(over?.Identity, query, active);
        return new IdentityDialogState(gameId, game.DetectedTitle, over?.IdentityRevision ?? 0, over?.DecisionRevision ?? 0,
            over?.ArtworkRevision ?? 0, state.State, state.UnresolvedReason, active, over?.Identity, query);
    }

    /// <summary>The catalogs the picker searches (IGDB primary, SteamGridDB fallback) - built from the same configuration
    /// snapshots the scan uses.</summary>
    internal IReadOnlyList<ICatalogProvider> CreateCatalogProvidersForPicker() => BuildResolutionContext().Providers;

    /// <summary>The user's explicit choice: this catalog entry IS this game. Never touches a pinned cover (I1); removes the
    /// automatic artwork that is no longer authorized (launcher art unless proven, legacy continuity, a competitor's cover);
    /// then a separate automatic unit fetches the confirmed identity's own art.</summary>
    public async Task<IdentityChangeOutcome> ConfirmIdentityAsync(string gameId, CatalogCandidate candidate,
        long expectedDecisionRevision, long expectedIdentityRevision, CancellationToken ct = default)
    {
        gameId = ResolveAlias(gameId);
        if (string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Namespace.Value) || candidate.Namespace.IsLauncher)
            return IdentityChangeOutcome.InvalidOperation;

        var key = new IdentityKey(candidate.Namespace, candidate.Id);

        // D7: which of the game's launcher ids does the CATALOG's own cross-reference data prove belong to this exact entry? Asked
        // BEFORE the (synchronous) commit, because it may need the network; the commit re-verifies the revisions, so a change that
        // lands meanwhile is StaleSelection, not a lost update. Unknown / unreachable proves nothing (launcher art is then dropped).
        var verified = await VerifyLauncherIdsAsync(gameId, candidate, ct);

        var outcome = CommitIdentityChange(gameId, expectedDecisionRevision, expectedIdentityRevision, (record, over) =>
        {
            var newConfirmed = new ProviderIdentity
            {
                Namespace = candidate.Namespace, Id = candidate.Id, Title = candidate.Title, At = DateTime.UtcNow,
                VerifiedLauncherIds = verified.Count > 0 ? verified : null,
            };
            var changed = record.Confirmed?.Key != key
                || !(record.Confirmed?.VerifiedLauncherIds ?? []).SequenceEqual(newConfirmed.VerifiedLauncherIds ?? []);
            record.Confirmed = newConfirmed;
            changed |= record.Rejected.RemoveAll(r => r.Key == key) > 0;                                   // it can no longer be "rejected"
            changed |= record.ArtworkSources.RemoveAll(s => s.Basis != key
                || !IdentitySelection.UserDecisionConsistent(s.Namespace, s.Id, record)) > 0;             // their basis is gone / inconsistent

            // Continuity ENDS the moment the user confirms (3.4): every legacy entry goes, except one naming the same id,
            // which is adopted - the user's decision is its corroboration - and its artwork gains DerivedFrom.
            ArtworkSelection? adopted = null;
            if (over?.Artwork is { IsUserSelected: false, DerivedFrom: null } art
                && record.LegacyEvidence.Any(l => l.Key == key && l.SourceProvider == art.Provider && l.Id == art.ProviderGameId))
            {
                adopted = CloneArtwork(art);
                adopted.DerivedFrom = key;
            }

            changed |= record.LegacyEvidence.RemoveAll(_ => true) > 0;
            return (changed, adopted, null);
        }, ct);

        if (outcome == IdentityChangeOutcome.Success)
            await RunAutomaticUnitAsync(gameId, "Confirm", force: true, ct);
        return outcome;
    }

    /// <summary>The launcher ids of `gameId` that the candidate's OWN catalog says belong to it (Consistent). Never throws except for
    /// the caller's cancellation; a provider that cannot say (no such catalog configured, Unknown, unreachable) proves nothing.</summary>
    private async Task<List<LauncherIdentifier>> VerifyLauncherIdsAsync(string gameId, CatalogCandidate candidate, CancellationToken ct)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return [];

        var launcherIds = IdentityQuery.From(game).LauncherIds;
        var provider = launcherIds.Count == 0 ? null : BuildResolutionContext().Providers.FirstOrDefault(p => p.Namespace == candidate.Namespace);
        if (provider is null)
            return [];

        var match = new CatalogMatch(candidate.Id, candidate.Title);
        try
        {
            return await Task.Run(() => launcherIds
                .Where(l => provider.CheckLauncherConsistency(match, l, ct) == LauncherConsistency.Consistent)
                .ToList(), CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Identify Game: couldn't verify launcher ids for '{game.Name}' - treating as unproven.", ex);
            return [];
        }
    }

    /// <summary>"Not this game": rejects one catalog candidate (an automatic identity, an artwork source or a legacy
    /// association). Removes every entry naming it in the same transaction, so it can never be picked again until Clear.
    /// Rejecting the CONFIRMED identity is not a Reject: it is Clear or Change.</summary>
    public IdentityChangeOutcome RejectIdentityCandidate(string gameId, IdentityKey key, string title,
        long expectedDecisionRevision, long expectedIdentityRevision)
    {
        gameId = ResolveAlias(gameId);
        return CommitIdentityChange(gameId, expectedDecisionRevision, expectedIdentityRevision, (record, over) =>
        {
            if (record.Confirmed?.Key == key)
                return (false, null, IdentityChangeOutcome.InvalidOperation);

            var changed = false;
            if (!record.Rejected.Any(r => r.Key == key))
            {
                record.Rejected.Add(new ProviderIdentity { Namespace = key.Namespace, Id = key.Id, Title = title, At = DateTime.UtcNow });
                changed = true;
            }

            changed |= record.Resolved.RemoveAll(r => r.Key == key) > 0;
            changed |= record.ArtworkSources.RemoveAll(s => s.Key == key) > 0;
            changed |= record.LegacyEvidence.RemoveAll(l => l.Key == key) > 0;
            return (changed, null, null);
        }, CancellationToken.None);
    }

    /// <summary>Back to automatic: removes the confirmation, the user's rejections and every artwork source. Fresh automatic
    /// identities that never depended on the user's decisions survive. On a QUARANTINED record this is the one explicit action
    /// that moves the raw subtree, untouched, into the archive and starts a fresh record.</summary>
    public async Task<IdentityChangeOutcome> ClearIdentityAsync(string gameId, long expectedDecisionRevision, long expectedIdentityRevision,
        CancellationToken ct = default)
    {
        gameId = ResolveAlias(gameId);
        var outcome = CommitIdentityChange(gameId, expectedDecisionRevision, expectedIdentityRevision, (record, over) =>
        {
            var changed = record.Confirmed is not null || record.Rejected.Count > 0 || record.ArtworkSources.Count > 0;
            record.Confirmed = null;
            record.Rejected.Clear();
            record.ArtworkSources.Clear();
            return (changed, null, null);
        }, ct, archiveQuarantine: true);

        if (outcome == IdentityChangeOutcome.Success)
            await RunAutomaticUnitAsync(gameId, "Clear", force: true, ct);
        return outcome;
    }

    /// <summary>The one place a user identity operation mutates live state - fully synchronous. Verifies BOTH revisions the
    /// dialog captured (a mismatch on either is StaleSelection), computes the next counters BEFORE mutating, applies the
    /// mutation to a scratch record, removes the automatic artwork that is no longer authorized, saves, and on failure
    /// restores every touched record and counter exactly (I4: durable before visible).</summary>
    private IdentityChangeOutcome CommitIdentityChange(string gameId, long expectedDecision, long expectedIdentity,
        Func<GameIdentityRecord, GameOverride?, (bool Changed, ArtworkSelection? AdoptedArtwork, IdentityChangeOutcome? Invalid)> mutate,
        CancellationToken ct, bool archiveQuarantine = false)
    {
        // The caller's cancellation (the dialog closing) is checked as the very first statement: this method is fully synchronous, so
        // nothing can interleave between the check and the mutation/save below. Whatever was awaited before it - launcher-id
        // verification, an earlier lookup - has already finished by now, and its result is discarded rather than committed.
        ct.ThrowIfCancellationRequested();

        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return IdentityChangeOutcome.GameNoLongerExists;

        _settings.Overrides.TryGetValue(gameId, out var existing);
        var decisionRevision = existing?.DecisionRevision ?? 0;
        var identityRevision = existing?.IdentityRevision ?? 0;
        if (decisionRevision != expectedDecision || identityRevision != expectedIdentity)
            return IdentityChangeOutcome.StaleSelection;      // the user's decisions, or the premise the dialog showed, changed

        var query = IdentityQuery.From(game);
        var liveRecord = existing?.Identity;
        JsonElement? archived = null;
        GameIdentityRecord scratch;
        if (liveRecord is { IsQuarantined: true })
        {
            if (!archiveQuarantine)
                return IdentityChangeOutcome.InvalidOperation; // nothing automatic or ordinary replaces an opaque record
            archived = liveRecord.Quarantined;
            scratch = new GameIdentityRecord();
        }
        else
        {
            scratch = liveRecord?.Clone() ?? new GameIdentityRecord();
        }

        var (changed, adoptedArtwork, invalid) = mutate(scratch, existing);
        if (invalid is { } reason)
            return reason;
        if (!changed && archived is null)
            return IdentityChangeOutcome.NoChange;             // a no-op advances nothing and needs no Save

        var keyChanged = IdentitySelection.SelectActive(liveRecord, query).Key != IdentitySelection.SelectActive(scratch, query).Key;
        if (!TryGetNextRevision(decisionRevision, out var nextDecision)
            || (keyChanged && !TryGetNextRevision(identityRevision, out _)))
        {
            Logger.Error($"'{game.Name}': an identity revision counter is exhausted - rejecting this change before touching anything.");
            return IdentityChangeOutcome.RevisionExhausted;
        }

        TryGetNextRevision(identityRevision, out var nextIdentity);

        // ---- apply, remembering exactly what to restore ----
        var hadOverride = existing is not null;
        var over = existing ?? new GameOverride();
        var beforeIdentity = over.Identity;
        var beforeArtwork = over.Artwork;
        var beforeArchiveCount = _settings.QuarantineArchive.Count;

        over.Identity = scratch;
        over.DecisionRevision = nextDecision;
        if (keyChanged)
            over.IdentityRevision = nextIdentity;
        if (archived is { } raw)
            _settings.QuarantineArchive.Add(raw.Clone());
        if (!hadOverride)
            _settings.Overrides[gameId] = over;

        // REMOVAL PASS: automatic artwork for which the predicate no longer holds goes in the SAME transaction; pinned
        // artwork is never touched (I1). An adopted legacy cover is re-authorized by its new DerivedFrom instead.
        var artworkRemoved = false;
        if (over.Artwork is { IsUserSelected: false } art)
        {
            if (adoptedArtwork is not null)
                over.Artwork = adoptedArtwork;
            else if (!ArtworkAuthorization.IsAuthorized(art, IdentitySelection.SelectActive(scratch, query), query, scratch))
            {
                over.Artwork = null;
                artworkRemoved = true;
            }
        }

        if (!_settingsService.Save(_settings))
        {
            over.Identity = beforeIdentity;
            over.DecisionRevision = decisionRevision;
            over.IdentityRevision = identityRevision;
            over.Artwork = beforeArtwork;
            while (_settings.QuarantineArchive.Count > beforeArchiveCount)
                _settings.QuarantineArchive.RemoveAt(_settings.QuarantineArchive.Count - 1);
            if (!hadOverride)
                _settings.Overrides.Remove(gameId);
            return IdentityChangeOutcome.SaveFailed;          // display untouched
        }

        BumpIdentityGeneration(gameId);                        // any in-flight automatic unit is now superseded
        if (artworkRemoved)
            CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
        RefreshIdentityBadge(game);
        return IdentityChangeOutcome.Success;
    }

    // ---- Dedup merges (design 8.2) -----------------------------------------------------------------------------------------

    /// <summary>Merges the loser's identity into the winner's, extending MigrateMergedOverrides. NAMESPACE NEVER DECIDES: two
    /// confirmations are "the same decision" only when they are the same (namespace, id); anything else is a conflict that is
    /// RECORDED, not dropped. Automatic entries (Resolved, ArtworkSources, LegacyEvidence) are dropped and re-derived - a
    /// merged install is a new evidence fingerprint. If a counter is exhausted the migration is skipped but the loser's user
    /// decisions are still recorded.</summary>
    private void MigrateMergedIdentity(string loserId, string winnerId, GameOverride loser, GameOverride winner)
    {
        var lRec = loser.Identity;
        var wRec = winner.Identity;
        if (lRec is null)
            return;

        // An opaque record is never interpreted or edited: archive the loser's, keep the winner's.
        if (lRec.IsQuarantined)
        {
            _settings.QuarantineArchive.Add(lRec.Quarantined!.Value.Clone());
            return;
        }

        if (wRec is { IsQuarantined: true })
        {
            RecordLoserDecisions(loserId, winnerId, lRec, IdentityConflictKind.MergeRevisionExhausted);
            return;
        }

        var canAdvance = TryGetNextRevision(Math.Max(winner.IdentityRevision, loser.IdentityRevision), out var nextIdentity)
            & TryGetNextRevision(Math.Max(winner.DecisionRevision, loser.DecisionRevision), out var nextDecision);
        if (!canAdvance)
        {
            RecordLoserDecisions(loserId, winnerId, lRec, IdentityConflictKind.MergeRevisionExhausted);
            Logger.Error($"Merge migration for '{winnerId}': an identity revision counter is exhausted - recording the loser's decisions as conflicts.");
            return;
        }

        var merged = wRec?.Clone() ?? new GameIdentityRecord();
        var decisionsChanged = false;

        if (lRec.Confirmed is { } lConfirmed)
        {
            if (merged.Confirmed is null)
            {
                if (merged.Rejected.Any(r => r.Key == lConfirmed.Key))
                    AddConflict(loserId, winnerId, IdentityConflictKind.ConfirmedVsRejected, lConfirmed, null);
                else
                {
                    merged.Confirmed = lConfirmed;    // a user decision beats an automatic one
                    decisionsChanged = true;
                }
            }
            else if (merged.Confirmed.Key != lConfirmed.Key)
            {
                AddConflict(loserId, winnerId,
                    merged.Confirmed.Namespace == lConfirmed.Namespace ? IdentityConflictKind.SameNamespaceDifferentId : IdentityConflictKind.CrossNamespaceUnproven,
                    lConfirmed, null);
                decisionsChanged = true;
            }
        }

        foreach (var rejected in lRec.Rejected)
        {
            if (merged.Confirmed?.Key == rejected.Key)
            {
                AddConflict(loserId, winnerId, IdentityConflictKind.ConfirmedVsRejected, null, new List<ProviderIdentity> { rejected });
                decisionsChanged = true;
            }
            else if (!merged.Rejected.Any(r => r.Key == rejected.Key))
            {
                merged.Rejected.Add(rejected);
                decisionsChanged = true;
            }
        }

        // Dropped and re-derived: a merged install is a new evidence fingerprint.
        merged.LegacyEvidence.Clear();
        merged.Resolved.RemoveAll(r => merged.Rejected.Any(x => x.Key == r.Key));
        merged.ArtworkSources.RemoveAll(s => merged.Confirmed is null || s.Basis != merged.Confirmed.Key);

        winner.Identity = merged;
        winner.IdentityRevision = nextIdentity;
        winner.DecisionRevision = decisionsChanged ? nextDecision : Math.Max(winner.DecisionRevision, loser.DecisionRevision);
        BumpIdentityGeneration(winnerId);
    }

    /// <summary>The winner had no override, so the loser's whole override (identity included) is adopted under the winner's id.
    /// Its counters are bumped strictly past their own prior values - a unit computed against the pre-merge state must never
    /// validate against the post-merge id by numeric coincidence - and the generation moves.</summary>
    private void AdoptMergedIdentityRevisions(string winnerId, GameOverride adopted)
    {
        if (adopted.Identity is null && adopted.IdentityRevision == 0 && adopted.DecisionRevision == 0)
            return;

        if (TryGetNextRevision(adopted.IdentityRevision, out var nextIdentity))
            adopted.IdentityRevision = nextIdentity;
        if (TryGetNextRevision(adopted.DecisionRevision, out var nextDecision))
            adopted.DecisionRevision = nextDecision;
        BumpIdentityGeneration(winnerId);
    }

    private void RecordLoserDecisions(string loserId, string winnerId, GameIdentityRecord lRec, IdentityConflictKind kind)
    {
        if (lRec.Confirmed is not null || lRec.Rejected.Count > 0)
            AddConflict(loserId, winnerId, kind, lRec.Confirmed, lRec.Rejected.ToList());
    }

    private void AddConflict(string loserId, string winnerId, IdentityConflictKind kind, ProviderIdentity? loserConfirmed, List<ProviderIdentity>? loserRejected)
    {
        var duplicate = _settings.IdentityConflicts.Any(c => c.Raw is null && c.WinnerGameId == winnerId && c.LoserGameId == loserId
            && c.Kind == kind.ToString() && c.LoserConfirmed?.Key == loserConfirmed?.Key);
        if (duplicate)
            return;

        _settings.IdentityConflicts.Add(new IdentityConflict
        {
            Kind = kind.ToString(), WinnerGameId = winnerId, LoserGameId = loserId, LoserConfirmed = loserConfirmed,
            LoserRejected = loserRejected ?? new List<ProviderIdentity>(), DetectedAt = DateTime.UtcNow,
        });
    }

    /// <summary>Read-only test access to the recorded identity conflicts.</summary>
    internal IReadOnlyList<IdentityConflict> IdentityConflictsForTest => _settings.IdentityConflicts;

    internal IReadOnlyList<JsonElement> QuarantineArchiveForTest => _settings.QuarantineArchive;

    // ---- The card badge ---------------------------------------------------------------------------------------------------

    /// <summary>Marks a card whose identity automatic resolution could not settle, so the user is invited to identify it. A
    /// PENDING revalidation (an upgraded library re-verifying its old covers) deliberately does not badge: that is normal, and
    /// nothing is wrong with the cover the user is looking at.</summary>
    private void RefreshIdentityBadge(GameEntry game)
    {
        _settings.Overrides.TryGetValue(game.Id, out var over);
        var state = IdentitySelection.DeriveState(over?.Identity, IdentityQuery.From(game));
        var text = state.State == IdentityState.Unresolved
            ? state.UnresolvedReason switch
            {
                "NoMatch" => "No confident match found. Right-click to identify this game.",
                "Ambiguous" => "Several games share this name, so it was not guessed. Right-click to identify this game.",
                "Contradicted" => "The only match disagreed with the launcher, so it was not used. Right-click to identify this game.",
                "Quarantined" => "This game's identity data couldn't be read. Right-click for options.",
                _ => null,
            }
            : null;

        game.NeedsIdentity = text is not null;
        game.IdentityBadgeText = text ?? "";
    }

    // ---- Identify Game / Choose Cover: the UI entry points -----------------------------------------------------------------

    /// <summary>Test seam: replaces the real window. Receives the dialog view model and whether to open on the covers tab.</summary>
    internal Action<IdentifyGameViewModel, bool>? IdentifyDialogForTest { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void IdentifyGame(GameEntry? game) => OpenIdentifyDialog(game, startOnCovers: false);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChooseCatalogCover(GameEntry? game) => OpenIdentifyDialog(game, startOnCovers: true);

    /// <summary>`game` is read only for its Id/Name, captured before the dialog blocks (a refresh can replace the entry while the
    /// user has the window open). Wrapped in a catch-all: a failure to even open the window must never crash the dispatcher.</summary>
    private void OpenIdentifyDialog(GameEntry? game, bool startOnCovers)
    {
        if (game is null)
            return;

        var (gameId, gameName) = (game.Id, game.Name);
        try
        {
            var dialog = new IdentifyGameViewModel(this, BuildResolutionContext().Providers, gameId, gameName);
            if (IdentifyDialogForTest is { } seam)
            {
                seam(dialog, startOnCovers);
                return;
            }

            var window = new IdentifyGameWindow(dialog, startOnCovers) { Owner = System.Windows.Application.Current?.MainWindow };
            window.ShowDialog();
            StatusText = string.IsNullOrWhiteSpace(dialog.StatusMessage) ? StatusText : dialog.StatusMessage;
        }
        catch (Exception ex)
        {
            Logger.Error($"Identify Game failed to open for '{gameName}'.", ex);
            StatusText = $"Couldn't open Identify Game for {gameName} - see the log for details.";
        }
    }

    // ---- IGDB credentials (Settings) ---------------------------------------------------------------------------------------

    /// <summary>The user's OWN IGDB Twitch application id (dev.twitch.tv/console/apps). Per installation; never embedded in the build.</summary>
    [ObservableProperty]
    private string _igdbClientId = string.Empty;

    /// <summary>Whether a client secret has been saved (the secret itself is never read back into the UI).</summary>
    [ObservableProperty]
    private bool _igdbSecretSaved;

    partial void OnIgdbClientIdChanged(string value)
    {
        _settings.IgdbClientId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _settingsService.Save(_settings);
    }

    /// <summary>Stores the secret OS-protected (DPAPI, this user only) outside settings.json; a blank value removes it.</summary>
    public void SaveIgdbClientSecret(string? secret)
    {
        _credentialStore.SaveSecret(string.IsNullOrWhiteSpace(secret) ? null : secret.Trim());
        IgdbSecretSaved = _credentialStore.HasSecret();
    }
}
