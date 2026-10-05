using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Applying a scan to the library: carrying favorites and artwork over merged or renamed games, publishing covers, and the refresh itself.</summary>
public partial class LibraryViewModel
{
    private CancellationTokenSource? _refreshCts;

    /// <summary>True when the next scan should walk the disks for games without a launcher instead of reusing the folders the last
    /// walk found. Set by a rescan the user asked for; cleared once a scan has published.</summary>
    private bool _forceDiskSweep;

    /// <summary>The user's own "scan again": the same scan as the startup one, plus a fresh look at the disks. (A scan started by adding a
    /// folder or by startup reuses the disk search of the last 12 hours, which keeps those quick.)</summary>
    [RelayCommand]
    private Task RescanAsync()
    {
        _forceDiskSweep = true;
        return RefreshAsync();
    }

    private Task ForcedRescan() => RescanCommand.ExecuteAsync(null);

    /// <summary>Test-only: invoked (and awaited) by ApplyScanResultAsync right after its PREPARE phase
    /// (off-thread cover decode) completes and before PUBLISH starts - the exact window in which a real
    /// concurrent action (a Change Cover/Reset commit, a ToggleFavorite click, a superseding refresh)
    /// could land in production. Lets tests deterministically exercise that window without real threading
    /// - see LibraryViewModelArtworkTests' controlled-interleaving tests. Always null in production.
    /// Invoked unconditionally, even when PREPARE had nothing to decode (no real await occurred) - a test
    /// simulating a plain UI action during a scan publish doesn't require an artwork decode to be in
    /// flight to be meaningful.</summary>
    internal Func<Task>? DuringCoverDecodeForTest { get; set; }

    /// <summary>The one place _allGames is ever replaced wholesale - RefreshAsync (a real scan) and
    /// SimulateRefreshResult (the test seam standing in for one) both route through this, so the test
    /// seam can never drift from what a real refresh actually does to running-game tracking.</summary>
    private void ReplaceAllGames(List<GameEntry> games)
    {
        // A drive switched off while this scan ran: its games must not come back with the result.
        var filter = NewDriveFilter();
        if (filter.HasAny)
            games.RemoveAll(g => filter.IsIgnored(g.InstallDir));

        _allGames = games;
        ReapplyRunningBadge();
    }

    /// <summary>Test seam: applies exactly the _allGames-replacement + running-badge-reconciliation a
    /// real scan performs (see ReplaceAllGames), without requiring GameScannerService's real
    /// filesystem/registry work behind it. Production code only ever reaches ReplaceAllGames via
    /// RefreshAsync. See LibraryViewModelRunningGameTests.</summary>
    internal void SimulateRefreshResult(List<GameEntry> games) => ReplaceAllGames(games);

    /// <summary>Test seam mirroring exactly what RefreshAsync itself does to establish ownership of the
    /// current refresh (cancel-and-replace _refreshCts) - lets a test drive ApplyScanResultAsync's
    /// `ownershipToken` parameter directly, and simulate a superseding refresh mid-decode, without
    /// needing a real GameScannerService scan behind it. Returns the new token.</summary>
    internal CancellationTokenSource SetRefreshOwnershipForTest()
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        return cts;
    }

    /// <summary>Migrates a superseded entry's override onto the surviving entry it was merged into -
    /// see GameScannerService.DeduplicateByInstallLocation (e.g. a Manual entry reconciled into a
    /// launcher-detected one for the same install, a real confirmed case for EA's "A Way Out"). Extracted
    /// as its own method (called from ApplyScanResult) so it can be exercised directly against a
    /// scripted id mapping in tests, without needing a real scan. ApplyScanResultAsync also feeds this
    /// the very same map for a ScanResult.LegacyIdRemap transition (a scanner's own detection-method
    /// change giving the SAME install a new id, not a within-scan merge) - the "old id's state moves onto
    /// the new id" logic below is identical either way.
    ///
    /// FIELD-LEVEL merge, not "loser wins outright" or "winner wins outright": a real, confirmed gap in
    /// an earlier version of this method removed the loser's override unconditionally, then only
    /// migrated it if the winner had NO override at all - but a winner CAN already have an override that
    /// is purely automatic metadata (GameScannerService's own NewDateAddedByGameId stamps a bare
    /// DateAdded-only GameOverride the first time any new id is seen, with no explicit user choice
    /// behind it at all), and that bare record was enough to block migration while the loser's own
    /// explicit Favorite/Hidden/CustomName was already gone. GameOverride's Favorite/Hidden are plain
    /// bools, though, with no way to represent "explicitly set to false" separately from "never
    /// touched" - so this can only ever ADD a true value or fill in an unset field, never take a true
    /// value AWAY from either side. That is a real, accepted limitation of the current data model (an
    /// explicit un-favorite on the winner, immediately followed by a merge with a loser that still has
    /// Favorite=true from its own history, would resurrect it) - not something this method can perfectly
    /// resolve without GameOverride itself becoming tri-state, which is a larger change than this fix
    /// warrants on its own.</summary>
    internal void MigrateMergedOverrides(Dictionary<string, string> mergedGameIds)
    {
        foreach (var (loserId, winnerId) in mergedGameIds)
        {
            if (!_settings.Overrides.Remove(loserId, out var loserOverride))
                continue; // nothing to migrate

            if (!_settings.Overrides.TryGetValue(winnerId, out var winnerOverride))
            {
                // Still bumped past its own prior value, not carried over as-is - see the ArtworkRevision
                // bump below for why a merge can never simply copy a revision number across untouched. If
                // exhausted, the loser's own revision is already at the terminal value (see
                // TryGetNextRevision) - adopted as-is rather than reused-but-relabeled; every future
                // artwork-touching operation on this survivor id will itself be safely rejected from here
                // on, exactly as it already would have been under the loser's own id.
                if (TryGetNextRevision(loserOverride.ArtworkRevision, out var adoptedRevision))
                    loserOverride.ArtworkRevision = adoptedRevision;
                else
                    Logger.Error($"Merge migration for '{winnerId}': artwork revision counter exhausted for the loser's history - adopting it as-is.");
                _settings.Overrides[winnerId] = loserOverride;
                AdoptMergedIdentityRevisions(winnerId, loserOverride);
                continue;
            }

            winnerOverride.Favorite = winnerOverride.Favorite || loserOverride.Favorite;
            winnerOverride.Collections = MergeCollectionNames(winnerOverride.Collections, loserOverride.Collections);
            winnerOverride.Notes = MergeNotes(winnerOverride.Notes, loserOverride.Notes);
            winnerOverride.Hidden = winnerOverride.Hidden || loserOverride.Hidden;
            winnerOverride.CustomName ??= loserOverride.CustomName;
            winnerOverride.DateAdded ??= loserOverride.DateAdded;

            // Both sides are the SAME install, so their tracked time is time spent on one game - summed,
            // not "whichever side had more". Last-played takes the later of the two for the same reason.
            winnerOverride.TotalPlaySeconds += loserOverride.TotalPlaySeconds;
            if (loserOverride.LastPlayedUtc is { } loserLastPlayed
                && (winnerOverride.LastPlayedUtc is not { } winnerLastPlayed || loserLastPlayed > winnerLastPlayed))
            {
                winnerOverride.LastPlayedUtc = loserLastPlayed;
            }

            // Identity merges independently of artwork: its own counters, its own conflicts (design 8.2).
            MigrateMergedIdentity(loserId, winnerId, loserOverride, winnerOverride);

            // Strictly greater than EITHER side's prior value, never a copy of one side's number - a
            // scan result computed against either side's PRE-merge revision (on this id or, by numeric
            // coincidence, some entirely unrelated game) must never be able to validate against the
            // post-merge id just because the numbers happen to match. See ScanResult.ArtworkApplyResult
            // and ApplyScanResultAsync's reconciliation below for the other half of this guarantee.
            //
            // Only migrate the ARTWORK data when a genuinely new, distinguishing revision can actually be
            // produced - if TryGetNextRevision can't (exhausted), migrating the data anyway would be an
            // unsignaled change: an outstanding scan/lookup that already captured this exact revision
            // would wrongly keep treating itself as current even though the winner's artwork just changed
            // underneath it. Skipping the migration (Favorite/Hidden/CustomName/DateAdded above are
            // unaffected and still apply) is the safe choice at a boundary real usage cannot reach anyway.
            if (TryGetNextRevision(Math.Max(winnerOverride.ArtworkRevision, loserOverride.ArtworkRevision), out var nextRevision))
            {
                MigrateMergedArtwork(loserId, winnerId, loserOverride, winnerOverride);
                winnerOverride.ArtworkRevision = nextRevision;
            }
            else
            {
                // The winner's active artwork/revision are left completely untouched here - not a
                // partial or best-effort migration, since that's exactly the unsignaled-change risk this
                // branch exists to avoid (see the remarks above). But the loser's override was already
                // removed at the top of this loop, and if it held an EXPLICIT user selection, silently
                // letting it fall out of ArtworkConflicts too would lose that selection's identity forever
                // with nothing left even referencing it - a real, if extraordinarily unlikely, gap
                // distinct from the winner-side skip above. Recording it costs nothing (no revision
                // change, no active-artwork change) and matches exactly what MigrateMergedArtwork already
                // does for the ordinary user-selection-conflict case.
                if (loserOverride.Artwork is { IsUserSelected: true } loserSelection)
                {
                    _settings.ArtworkConflicts.Add(new ArtworkConflict
                    {
                        WinnerGameId = winnerId,
                        LoserGameId = loserId,
                        LoserSelection = loserSelection,
                        DetectedAt = DateTime.UtcNow,
                    });
                }

                Logger.Error($"Merge migration for '{winnerId}': artwork revision counter exhausted - skipping artwork migration for this merge.");
            }
        }
    }

    /// <summary>The one place ArtworkRevision is ever incremented - and the one place a mutation is
    /// REJECTED outright once the counter is exhausted, rather than reusing a value that's already been
    /// handed out. An earlier version saturated at long.MaxValue instead of rejecting: repeatedly
    /// returning the same long.MaxValue meant a mutation performed AFTER saturation was indistinguishable,
    /// by revision alone, from the state that existed BEFORE it - an outstanding scan/lookup that had
    /// already captured long.MaxValue as "current" would keep matching it forever, even across a real,
    /// later change it never actually saw. Rejecting instead guarantees the invariant every caller of this
    /// method actually depends on: if a mutation is accepted, the resulting revision is provably distinct
    /// from anything captured before it; if it can't be, nothing about the live state changes at all, so
    /// whatever was already correctly "current" simply stays correctly current. Every caller must check
    /// the `out` result and refuse to change/remove the override it was about to touch when this returns
    /// false. SettingsService.Load applies a separate clamp for a hand-edited or corrupted settings.json -
    /// that protects against untrusted persisted input, not this method's own in-session correctness, so
    /// both are kept.</summary>
    private static bool TryGetNextRevision(long current, out long next)
    {
        if (current >= long.MaxValue)
        {
            next = default;
            return false;
        }

        next = current + 1;
        return true;
    }

    /// <summary>Artwork merge precedence, explicit: a user selection always beats an automatic one (or
    /// none at all); when the winner already has a DIFFERENT explicit user selection, that conflict is
    /// not resolved by picking one and discarding the other - the winner's stays active (least
    /// disruptive - it's already what's showing), and the loser's is recorded in
    /// AppSettings.ArtworkConflicts instead of becoming unreferenced and eventually reclaimed. That is
    /// not a resolution UI (deliberately out of scope for now) - just a guarantee that "preserved" is
    /// actually true rather than "logged, then silently lost" the moment the loser's own GameOverride
    /// is removed above.</summary>
    private void MigrateMergedArtwork(string loserId, string winnerId, GameOverride loserOverride, GameOverride winnerOverride)
    {
        if (loserOverride.Artwork is not { } loserArt)
            return;

        if (winnerOverride.Artwork is not { IsUserSelected: true })
        {
            if (loserArt.IsUserSelected || winnerOverride.Artwork is null)
                winnerOverride.Artwork = loserArt;
            return;
        }

        if (loserArt.IsUserSelected && winnerOverride.Artwork.AssetId != loserArt.AssetId)
        {
            _settings.ArtworkConflicts.Add(new ArtworkConflict
            {
                WinnerGameId = winnerId,
                LoserGameId = loserId,
                LoserSelection = loserArt,
                DetectedAt = DateTime.UtcNow,
            });
            Logger.Warn($"Dedup merge: '{loserId}' and its winner '{winnerId}' both had a user-selected "
                + $"cover (loser asset {loserArt.AssetId} vs winner asset {winnerOverride.Artwork.AssetId}); "
                + "keeping the winner's, recording the loser's in ArtworkConflicts instead of discarding it.");
        }
    }

    /// <summary>Publishes a completed ScanResult to live state - the one place RefreshAsync (a real
    /// scan) and tests both route through, so test coverage of merge/reconciliation behavior (id
    /// migration, override migration, DateAdded, the running-game badge) exercises the actual production
    /// path rather than a parallel copy of it that could silently drift from what RefreshAsync really
    /// does.
    ///
    /// Two strictly separated publication phases: PREPARE decodes every
    /// live user-selected cover off the UI thread, touching NO live state at all - not _allGames, not
    /// _settings, not Games/FavoriteGames/HiddenGames. PUBLISH runs through ApplyFilter: fully
    /// synchronous, no yield point anywhere in it, so WPF's single UI-thread dispatcher alone guarantees
    /// no user action (ToggleFavorite/ToggleHidden, another refresh, a Change Cover commit) can ever run
    /// while it's partway through. A real, confirmed bug in an earlier version split _allGames/override
    /// reconciliation (before the await) from Games/FavoriteGames/HiddenGames population (ApplyFilter,
    /// after it) - a card still bound to the OLD GameEntry during that gap could have ToggleFavorite
    /// mutate the old instance and settings, then see ApplyFilter immediately afterward repopulate the
    /// grid from the NEW instances (already reconciled before the click, so untouched by it), making the
    /// click appear to silently revert. Folding ApplyFilter into this same synchronous block closes that
    /// gap entirely. After that block, a two-sided LegacyId migration that discarded a worker cover
    /// can run a fresh, cancellable automatic lookup against the merged state. It never replays the
    /// stale pre-merge identity and never separates publication of the library collections.
    ///
    /// `ownershipToken`, when given (RefreshAsync's own `cts`), is re-checked immediately before PUBLISH
    /// starts - as close to the mutation as the code structure allows - so a refresh superseded while
    /// PREPARE was decoding never publishes its now-stale result. Returns false if superseded before
    /// publication or during subsequent cover recovery;
    /// RefreshAsync uses this instead of re-checking ownership itself afterward, which would already be
    /// too late (the check needs to gate entry to PUBLISH, not run after it). Null (the default, used by
    /// every test that isn't exercising RefreshAsync's own cancel-and-replace machinery) means "always
    /// publish" - there is no competing refresh to be superseded by.
    ///
    /// `identityGenerationsAtScanStart`, when given (RefreshAsync's own SnapshotIdentityGenerations(), taken
    /// before the scan it's now publishing even started), is what lets a verified LegacyId transition's
    /// same-publish unit be trusted DESPITE the markers migration changes - see the re-stamping block below
    /// for why matching the unit's own IdentityRevision/AsOfRevision against the old override's CURRENT
    /// state is not enough by itself: a decision that doesn't move the active identity (rejecting a
    /// non-active candidate, say) advances DecisionRevision and the old id's own identity GENERATION without
    /// touching either revision the unit already checks. Null (every caller that isn't RefreshAsync's real
    /// scan path) means that proof is unavailable, so re-stamping never fires - fails closed, not open.</summary>
    internal async Task<bool> ApplyScanResultAsync(ScanResult result, CancellationTokenSource? ownershipToken = null,
        IReadOnlyDictionary<string, long>? identityGenerationsAtScanStart = null)
    {
        // ---- PREPARE: off the UI thread, read-only against live state ----
        // Snapshot of which games currently have a live user-selected cover, and exactly which asset/
        // revision it was AT SNAPSHOT TIME - re-validated against the LIVE override again, synchronously,
        // immediately before being applied in PUBLISH below. A newer Change Cover/Reset/merge landing on
        // this same game during the decode below is expected and handled there, not prevented here.
        var toDecode = new List<(GameEntry Game, ArtworkSelection Selection, long AsOfRevision)>();
        foreach (var game in result.Games)
        {
            if (_settings.Overrides.TryGetValue(game.Id, out var over) && over.Artwork is { IsUserSelected: true } selection)
                toDecode.Add((game, selection, over.ArtworkRevision));
        }

        var decodedById = toDecode.Count > 0
            ? await DecodePendingCoverRestoresAsync(toDecode)
            : new Dictionary<string, PreparedCoverRestore>();

        if (DuringCoverDecodeForTest is not null)
            await DuringCoverDecodeForTest();

        // ---- PUBLISH: synchronous through ApplyFilter; optional recovery follows afterward ----
        if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
            return false; // superseded while PREPARE was decoding - the newer refresh owns publication now

        // LegacyIdRemap (a scanner's own detection-method change giving the SAME install a new id - see
        // GameEntry.LegacyId) is folded into the very same "this id's state now lives under that other
        // id" map MergedGameIds already represents for a within-scan dedup merge, so it gets migrated
        // through the exact same machinery below rather than a parallel copy of it - running-game
        // tracking, in-flight identity dialogs, and persisted overrides all need to move for either kind
        // of transition, identically.
        var idTransitions = result.MergedGameIds;
        if (result.LegacyIdRemap is { Count: > 0 } legacyIdRemap)
        {
            idTransitions = new Dictionary<string, string>(result.MergedGameIds);
            foreach (var (oldId, newId) in legacyIdRemap)
                idTransitions[oldId] = newId;
        }

        // Before ReplaceAllGames (which reapplies the running badge against the new _allGames) - a
        // stale _runningGameId at that point would find nothing and silently lose the badge.
        ReconcileRunningGameId(idTransitions);

        // loser -> winner, so an identity dialog (or in-flight unit) still holding a merged-away id can find its game (6.4).
        foreach (var (mergedLoserId, mergedWinnerId) in idTransitions)
            _mergeAliases[mergedLoserId] = mergedWinnerId;

        // Captured BEFORE ReplaceAllGames discards these instances - ReconcileArtwork's stale/missing
        // branch needs the LAST live GameEntry for a given id, not the brand-new one this scan just
        // produced, to recover a display result a concurrent commit already applied to it (see
        // ReconcileArtwork's own remarks for the exact scenario this exists to fix).
        var previousGamesById = new Dictionary<string, GameEntry>();
        foreach (var g in _allGames)
            previousGamesById[g.Id] = g; // ids are unique by construction; last-wins is only a defensive fallback

        // A merge/LegacyId transition means the game ReconcileArtwork/CommitAutomaticUnit are about to
        // look up by its NEW id was, until this exact publish, still filed above under its OLD one - so
        // the GetValueOrDefault(game.Id) lookup below would find nothing for it. Without this, a
        // currently-displayed automatic cover can't be carried forward through the very same
        // ArtworkRevision bump MigrateMergedOverrides is about to apply to invalidate the pre-migration
        // worker result (see both methods' own "carried-forward pixels" remarks) - it would incorrectly
        // drop to the exe icon for one refresh even though nothing about the actual artwork changed.
        //
        // Only ever FILLS IN a missing entry - never overwrites one that's already there. The real,
        // confirmed bug this guards against: an ordinary dedup merge's winner is very often NOT brand
        // new - it can already be an existing, previously-displayed game with its own correct cover,
        // merging a loser that ALSO happens to already have its own (different) previously-displayed
        // cover from an earlier scan, before the merge relationship was recognized. Overwriting
        // previousGamesById[newId] unconditionally would clobber the winner's own, more authoritative
        // entry with the loser's - and since MigrateMergedOverrides/MigrateMergedArtwork correctly KEEP
        // the winner's own metadata active in exactly that case, publication would end up authorizing the
        // winner's metadata while displaying the loser's pixels. A transition target with no prior entry
        // at all (the ordinary LegacyId case: the new id has never been displayed before) still gets
        // filled in from the old id, exactly as before.
        foreach (var (oldId, newId) in idTransitions)
        {
            if (previousGamesById.ContainsKey(newId))
                continue;
            if (previousGamesById.TryGetValue(oldId, out var previous))
                previousGamesById[newId] = previous;
        }

        // Captured BEFORE migration, using the exact same condition MigrateMergedOverrides itself branches
        // on ("the winner has no override at all" -> ADOPT the loser's whole override unchanged, vs. both
        // sides having one -> MERGE their data together). Used below to let a same-publish unit for one of
        // these ids validate correctly despite the revision/generation bump adoption still (correctly,
        // unchanged) applies - see that loop's own remarks for why this is safe ONLY for a pure adoption,
        // never for a merge that actually combines two sides' identity/artwork data.
        var adoptedWithNoPriorOverride = new HashSet<string>(idTransitions.Values.Where(id => !_settings.Overrides.ContainsKey(id)));

        // Specifically a LegacyId transition (never an ordinary MergedGameIds dedup) whose worker-produced
        // unit can be PROVEN to have actually been computed against the exact old override now being
        // adopted - an ordinary dedup winner has no such tie: its own unit (if any) was resolved from the
        // WINNER's own prior state, which has nothing to do with whatever a merging loser happens to carry,
        // so "the winner lacks an override" alone is never enough to trust re-stamping it (a loser with its
        // own CONFIRMED identity must still beat the winner's unrelated automatic one, exactly as an
        // ordinary merge already requires). oldByNewId is 1:1 - ComputeLegacyIdRemap only ever reports an
        // old id unambiguously claimed by exactly one new id, in either direction, within one scan.
        var oldByNewId = result.LegacyIdRemap is { Count: > 0 } legacyRemap
            ? legacyRemap.ToDictionary(kv => kv.Value, kv => kv.Key)
            : new Dictionary<string, string>();

        // Previously duplicated Xbox entries can BOTH have persisted overrides. Their merge must
        // invalidate the worker's pre-merge result, but on restart there are no displayed pixels to
        // carry forward. Resolve afresh against the merged state after publication, never re-stamp
        // that stale result. Only actual two-sided migrations need this recovery.
        var mergedLegacyIds = oldByNewId.Where(pair =>
                _settings.Overrides.ContainsKey(pair.Key) && _settings.Overrides.ContainsKey(pair.Value))
            .Select(pair => pair.Key).ToHashSet();
        var coversToRecover = new List<string>();

        // The old override's OWN identity/artwork revision, read RIGHT NOW - before migration consumes it
        // (MigrateMergedOverrides below removes it outright) - so it can be compared, per game, against
        // exactly what that game's own unit captured from it. A live edit landing on the OLD entry AFTER
        // the worker computed its unit but BEFORE this publish runs (a user confirming a different identity
        // via a dialog while this scan was still finishing, say) bumps these on the OLD override - and
        // nothing else does, since the scan's own snapshot never touches the live dictionary. A mismatch
        // here means the worker's result no longer reflects what's actually about to be adopted, and must
        // never be re-stamped into looking current regardless.
        //
        // Revision alone is not enough, though: a decision that doesn't move the ACTIVE identity (rejecting
        // a candidate that wasn't the active one, say) advances DecisionRevision and the old id's own
        // identity GENERATION without touching IdentityRevision at all (see CommitIdentityHalf/
        // CommitIdentityChange - BumpIdentityGeneration runs on ANY change to the record, keyChanged or
        // not). ResolveGameUnit's own IdentityGeneration is captured relative to the NEW id (always 0 for a
        // transition target that's never existed before), so it says nothing about whether the OLD id moved
        // - that's exactly what identityGenerationsAtScanStart's own value for the old id, compared against
        // the old id's CURRENT generation here, closes: if they still match, nothing touched the old entry's
        // identity record - confirmed, rejected, or otherwise - for the whole scan window.
        var preMigrationOldState = new Dictionary<string, (long IdentityRevision, long ArtworkRevision, long Generation)>();
        foreach (var oldId in oldByNewId.Values)
        {
            var o = _settings.Overrides.GetValueOrDefault(oldId);
            preMigrationOldState[oldId] = (o?.IdentityRevision ?? 0, o?.ArtworkRevision ?? 0, IdentityGenerationOf(oldId));
        }

        ReplaceAllGames(result.Games);

        // Before the NewDateAddedByGameId loop below, so its "??=" sees the real, migrated DateAdded
        // already in place rather than treating the surviving id as brand-new and stamping today's date
        // over the game's actual add date.
        MigrateMergedOverrides(idTransitions);

        // Merged here, on the UI thread, rather than written straight into _settings.Overrides from the
        // background scan thread - see GameScannerService.ScanAllAsync's remarks on why that's a real
        // Dictionary-corruption risk, not just a staleness one.
        foreach (var (id, dateAdded) in result.NewDateAddedByGameId)
        {
            if (!_settings.Overrides.TryGetValue(id, out var over))
            {
                over = new GameOverride();
                _settings.Overrides[id] = over;
            }
            over.DateAdded ??= dateAdded;
        }

        // Same idea for any watched folder WatchedFolderResolver healed during this scan (a first-time
        // volume anchor, or a re-derived path after a drive-letter change) - the scan only ever touched
        // its own private copies (see GameScannerService.ScanAllAsync), so the healed values are applied
        // to the live, UI-bound object here instead. Matched by the path the folder had when this scan
        // started, since the healed Path may itself have changed.
        foreach (var healed in result.HealedWatchedFolders)
        {
            var live = _settings.WatchedFolders.FirstOrDefault(w =>
                string.Equals(w.Path, healed.OriginalPath, StringComparison.OrdinalIgnoreCase));
            if (live is null)
                continue; // removed while this scan was running - nothing to heal anymore

            live.Path = healed.HealedPath;
            live.VolumeSerialNumber = healed.VolumeSerialNumber;
            live.RelativePath = healed.RelativePath;
        }

        // Re-applied here, on the UI thread, rather than trusting the Hidden/Favorite/CustomName/
        // DateAdded GameScannerService already baked into each GameEntry: those came from an Overrides
        // snapshot taken when this scan started, and ToggleFavorite/ToggleHidden stay usable the whole
        // time a scan is running - and, for DateAdded specifically, a merged entry's real historical date
        // only becomes available via MigrateMergedOverrides above, which runs on the UI thread AFTER the
        // scanner already baked its own (wrong, "today") guess into the GameEntry for a brand-new
        // surviving id. Without re-applying DateAdded here too - a real, confirmed gap in an earlier
        // version of this method - "Recently Added" would show the wrong date for a merged game until
        // another, later refresh finally saw the migrated value in its own Overrides snapshot.
        foreach (var game in _allGames)
        {
            _settings.Overrides.TryGetValue(game.Id, out var over);
            if (!string.IsNullOrWhiteSpace(over?.CustomName))
                game.Name = over.CustomName;
            game.Hidden = over?.Hidden ?? false;
            game.Favorite = over?.Favorite ?? false;
            game.Collections = over?.Collections is { Count: > 0 } collections ? [.. collections] : [];
            if (over?.DateAdded is { } dateAdded)
                game.DateAdded = dateAdded;
            game.TotalPlaySeconds = over?.TotalPlaySeconds ?? 0;
            game.LastPlayedUtc = over?.LastPlayedUtc;

            var scanned = result.ArtworkResultsByGameId.GetValueOrDefault(game.Id);

            // Re-stamping a same-publish unit's own captured markers to the LIVE, post-migration values is
            // trustworthy ONLY when ALL of these hold together:
            //  1. This is a verified LegacyId transition (oldByNewId), never an ordinary dedup merge - a
            //     dedup winner's own unit (if any) was resolved from the WINNER's own prior state, which has
            //     no connection to whatever a merging loser happens to carry; "the winner lacks an override"
            //     alone proves nothing about what the submitted unit was actually computed against (a real,
            //     confirmed gap: a loser's own confirmed identity must still beat the winner's unrelated
            //     automatic one here, exactly as an ordinary merge already requires elsewhere).
            //  2. It's still a pure ADOPTION (adoptedWithNoPriorOverride) - the same existing requirement.
            //  3. The OLD override's identity/artwork revision, read JUST NOW before migration consumed it
            //     (preMigrationOldState), still EXACTLY matches what this unit captured when the worker
            //     computed it.
            //  4. The OLD id's own identity GENERATION, read the SAME way, still matches what it was at
            //     SCAN START (identityGenerationsAtScanStart) - revision equality alone (point 3) misses a
            //     decision that never moves the active identity (rejecting a non-active candidate, say):
            //     DecisionRevision and the generation both advance, IdentityRevision does not. Without this
            //     check, that rejection would be silently erased by the worker's older, pre-rejection record.
            // A live edit to the old entry AFTER the worker ran but BEFORE this publish (a user confirming or
            // rejecting a candidate via a dialog while the scan was still finishing, say) breaks point 3, 4,
            // or both - the unit no longer reflects what's actually about to be adopted, and must be left to
            // fail its own ordinary, unmodified validation rather than being trusted. Only when all four hold
            // is the unit PROVABLY computed from exactly the override migration is about to adopt, with
            // nothing else having touched it since - safe to translate the markers migration itself changed,
            // without weakening the defensive bump for any other caller.
            if (scanned?.Unit is { } rawUnit
                && adoptedWithNoPriorOverride.Contains(game.Id)
                && oldByNewId.TryGetValue(game.Id, out var sourceOldId)
                && preMigrationOldState.TryGetValue(sourceOldId, out var sourceState)
                && rawUnit.IdentityRevision == sourceState.IdentityRevision
                && scanned.AsOfRevision == sourceState.ArtworkRevision
                && identityGenerationsAtScanStart is not null
                && identityGenerationsAtScanStart.GetValueOrDefault(sourceOldId) == sourceState.Generation)
            {
                var adjustedUnit = rawUnit with { IdentityGeneration = IdentityGenerationOf(game.Id), IdentityRevision = over?.IdentityRevision ?? 0 };
                scanned = scanned with { AsOfRevision = over?.ArtworkRevision ?? 0, Unit = adjustedUnit };
            }

            if (scanned?.Unit is { } unit)
            {
                // The identity pipeline's automatic unit: identity half first, then the artwork half through the gates.
                CommitAutomaticUnit(game, scanned, unit, previousGamesById.GetValueOrDefault(game.Id), decodedById, inPlace: false);
                if (mergedLegacyIds.Contains(game.Id) && !game.IsCoverArt
                    && over?.Artwork is not { IsUserSelected: true })
                    coversToRecover.Add(game.Id);
            }
            else
            {
                ReconcileArtwork(game, over, scanned, previousGamesById.GetValueOrDefault(game.Id), decodedById);
            }
        }

        // A game that keeps its current cover in its own install folder (Fortnite's splash screen) shows that, unless the user
        // picked a cover for it. Applied on top of the automatic result and never stored, so an update's new art is read again.
        InstallFolderArt.Apply(_allGames, g => _settings.Overrides.GetValueOrDefault(g.Id)?.Artwork is { IsUserSelected: true });

        // Populates Games/FavoriteGames/HiddenGames from the now-fully-reconciled _allGames - in the same
        // synchronous block as everything above, not left for a caller to run after its own later await
        // (see this method's own remarks for why that split was the actual bug).
        ApplyFilter();

        // The library collections are already published together. Recovery is an ordinary guarded
        // automatic lookup against LIVE post-merge identity/revisions, with refresh cancellation
        // carried through the worker and final commit. Explicit choices still take precedence.
        foreach (var gameId in coversToRecover)
        {
            if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
                return false;
            Logger.Info($"Artwork: resolving '{gameId}' against its merged identity after Xbox legacy-id reconciliation.");
            await RunAutomaticUnitAsync(gameId, "Legacy merge", force: false,
                ownershipToken?.Token ?? CancellationToken.None);
        }
        if (ownershipToken is not null && !ReferenceEquals(_refreshCts, ownershipToken))
            return false;

        return true;
    }

    /// <summary>One PREPARE-phase decode result for one game - see DecodePendingCoverRestoresAsync/
    /// ApplyScanResultAsync. Selection/AsOfRevision are exactly what was live when the decode was
    /// REQUESTED, not necessarily what's live now - ReconcileArtwork re-validates both against the
    /// CURRENT override before ever applying Decoded, so a selection that changed again while this was
    /// decoding is detected and discarded rather than silently applied over something newer.</summary>
    private sealed record PreparedCoverRestore(ArtworkSelection Selection, long AsOfRevision, BitmapImage? Decoded);

    /// <summary>Decodes every queued live user selection off the UI thread in one batch - pure read/decode
    /// work, touching no live state (not even the GameEntry each request came from; only its Id is used
    /// as the result key). Each item is isolated from every other: one corrupt/missing asset decodes to
    /// null for just that game, never aborting the batch or throwing out of this method.</summary>
    private Task<Dictionary<string, PreparedCoverRestore>> DecodePendingCoverRestoresAsync(
        List<(GameEntry Game, ArtworkSelection Selection, long AsOfRevision)> pending)
    {
        var storeDirOverride = AssetStoreDirOverrideForTest;
        return Task.Run(() =>
        {
            var result = new Dictionary<string, PreparedCoverRestore>();
            foreach (var (game, selection, asOfRevision) in pending)
            {
                BitmapImage? decoded;
                try
                {
                    decoded = CoverArtService.TryDecodeStored(selection, storeDirOverride);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"'{game.Name}': decoding the selected cover art failed unexpectedly.", ex);
                    decoded = null;
                }

                result[game.Id] = new PreparedCoverRestore(selection, asOfRevision, decoded);
            }

            return result;
        });
    }

    /// <summary>Reconciles ONE game's displayed Icon/IsCoverArt (and, for a current automatic match, its
    /// persisted metadata) against the LIVE override - the scan worker computed game.Icon/IsCoverArt
    /// against whatever state existed when it STARTED, which can be stale by the time this publish
    /// actually runs (a Change Cover, Reset, or dedup merge that landed on this exact game while the
    /// scan was still in flight - see ScanResult.ArtworkApplyResult's own remarks). Three explicit
    /// outcomes, never a default of "nothing recorded, trust the worker":
    ///  - A live user selection is authoritative regardless of what the worker computed. If
    ///    `decodedById` has an entry for this game whose Selection/AsOfRevision STILL exactly match the
    ///    CURRENT override (asset identity AND revision, both re-checked HERE, synchronously, at the
    ///    actual moment of applying it - not merely at the moment PREPARE decided what to decode), its
    ///    already-decoded, frozen bitmap is applied directly. Otherwise - no entry at all (this game
    ///    wasn't part of the PREPARE batch, e.g. its selection only became live afterward via a merge
    ///    migration), or a newer Change Cover/Reset changed the selection/revision while PREPARE was
    ///    decoding - the prepared result is discarded as stale and this falls back to a single,
    ///    synchronous, single-file decode instead (CoverArtService.ApplyStoredSafely): a small, bounded
    ///    cost for the rare case, never a reason to risk displaying the wrong image.
    ///  - A live automatic result whose revision still matches what the scan captured at start is
    ///    current: the worker's own game.Icon is already correct on this exact GameEntry and is left
    ///    alone, but its match evidence is always synced into the live override to match what's actually
    ///    displayed - replaced OR cleared, never left stale just because some earlier automatic result
    ///    happened to already be recorded there.
    ///  - Anything else (a revision mismatch, or no scan result for this game at all) is untrustworthy:
    ///    neither the worker's pixels nor its metadata for THIS GameEntry instance are used. But this is
    ///    NOT automatically "fall back to the exe icon" - the live override may already correctly
    ///    describe something newer than what this stale scan captured (a real, confirmed case: Reset's
    ///    own immediate single-game lookup lands and is applied to the PREVIOUS live GameEntry instance
    ///    for this id, and THEN an older, still-in-flight scan publishes and would otherwise silently
    ///    replace that already-correct result with a bare icon on the brand-new instance this scan
    ///    produced - ReplaceAllGames having just discarded the old instance entirely). So: if the live
    ///    override still has ANY current (non-user-selected - a user selection already returned above)
    ///    artwork, the last known-good display for this exact id (`previousLiveGame`, captured before
    ///    ReplaceAllGames ran) is what's trusted instead - its already-decoded, frozen pixels are carried
    ///    forward directly (an automatic result has no local asset to re-decode from). Only when the live
    ///    override has NOTHING selected at all does this fall back to the exe icon.</summary>
    private void ReconcileArtwork(GameEntry game, GameOverride? over, ArtworkApplyResult? scanResult,
        GameEntry? previousLiveGame, Dictionary<string, PreparedCoverRestore> decodedById)
    {
        if (over?.Artwork is { IsUserSelected: true } userSelection)
        {
            if (decodedById.TryGetValue(game.Id, out var prepared)
                && prepared.AsOfRevision == over.ArtworkRevision
                && prepared.Selection.AssetId == userSelection.AssetId
                && prepared.Selection.AssetExtension == userSelection.AssetExtension)
            {
                if (prepared.Decoded is not null)
                {
                    game.Icon = prepared.Decoded;
                    game.IsCoverArt = true;
                }
                else
                {
                    Logger.Warn($"'{game.Name}': selected cover art is missing or corrupt - showing the exe icon; the selection itself is kept.");
                    CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
                }
            }
            else
            {
                CoverArtService.ApplyStoredSafely(game, userSelection, IconFallbackForTest, AssetStoreDirOverrideForTest);
            }

            return;
        }

        var liveRevision = over?.ArtworkRevision ?? 0;
        // A missing scan result (this game has no entry in ArtworkResultsByGameId at all) is represented
        // as null, never as a numeric sentinel - a persisted ArtworkRevision could otherwise coincide
        // with whatever magic number meant "missing" and be wrongly treated as current. See
        // SettingsService.Load for the corresponding defense against a corrupted/out-of-range persisted
        // revision.
        var asOfRevision = scanResult?.AsOfRevision;

        if (asOfRevision == liveRevision)
        {
            // Worker's own pixels for this exact GameEntry are current and correct - left alone. Metadata
            // is still always synced to match exactly what's displayed: over.Artwork here can only be
            // null or a previous AUTOMATIC result (a live user selection already returned above), so
            // replacing or clearing it unconditionally can never discard a user's choice.
            var automaticResult = scanResult?.AutomaticResult;
            if (over is not null)
                over.Artwork = automaticResult;
            else if (automaticResult is not null)
            {
                over = new GameOverride { ArtworkRevision = liveRevision };
                _settings.Overrides[game.Id] = over;
                over.Artwork = automaticResult;
            }

            return;
        }

        if (over?.Artwork is not null)
        {
            if (previousLiveGame is { Icon: not null, IsCoverArt: true })
            {
                // Cheap and synchronous: reusing an already-decoded, frozen BitmapImage reference, not
                // re-reading or re-decoding anything. An automatic result has no local asset to restore
                // from at all - this is the only way to recover it without a fresh provider call during
                // reconciliation, which must stay network-free.
                game.Icon = previousLiveGame.Icon;
                game.IsCoverArt = previousLiveGame.IsCoverArt;
            }
            else
            {
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
            }

            return;
        }

        CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);
    }

    /// <summary>Raised at the end of a successful scan, once Games/FavoriteGames/HiddenGames/Drives
    /// all reflect the new results - MainWindow uses it to grow the window to fit the sidebar's
    /// actual content instead of leaving the user to resize it by hand every time a new drive or
    /// launcher shows up.</summary>
    public event Action? LibraryRefreshed;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Cancel-and-replace: AddFolder/RemoveFolder and a manual Refresh can each trigger a scan
        // while a previous one is still running (e.g. adding a folder before the initial startup scan
        // finishes). Both used to run to completion and write _allGames/Games/Drives/IsLoading in
        // whichever order they happened to finish, so a slower-but-older scan could silently overwrite
        // a newer one's results. Cancelling the previous token here, and only ever applying the
        // results of whichever scan is current when it completes (see the finally block below), means
        // exactly one scan's output ever reaches the UI.
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        IsLoading = true;
        StatusText = "Scanning...";
        ResetScanProgress();
        var scanProgress = new Progress<ScanProgress>(p => ApplyScanProgress(p, cts)); // created here, on the UI thread: reports arrive here too
        try
        {
            // Captured once and reused for both calls below - ApplyScanResultAsync needs the EXACT same
            // scan-start snapshot ScanAllAsync's own units were resolved against, to prove (not merely
            // assume) a LegacyId transition's same-publish unit still reflects the old entry's identity
            // untouched - see its own remarks on identityGenerationsAtScanStart.
            var identityGenerationsAtScanStart = SnapshotIdentityGenerations();
            var forceSweep = _forceDiskSweep;
            var result = await _scannerService.ScanAllAsync(_settings, token, identityGenerationsAtScanStart, ResolutionContextForTest?.Invoke(), SnapshotDisplayedCovers(), scanProgress, forceSweep);

            // GameScannerService checks the token internally too, but only cooperatively - a
            // cancelled scan can still be mid-flight on a background thread pool thread when this
            // await resumes (e.g. blocked in a synchronous cover-art HTTP call) and, depending on
            // exactly where cancellation landed, can complete "successfully" with a result that's
            // already stale. Cheap early exit before spending time on ApplyScanResultAsync's own
            // decode-prep phase - not the check that actually matters (that one lives INSIDE
            // ApplyScanResultAsync, immediately before it publishes; see its own remarks for why a
            // check made only here, after its await returns, would already be too late).
            if (!ReferenceEquals(_refreshCts, cts))
                return;

            var published = await ApplyScanResultAsync(result, cts, identityGenerationsAtScanStart);
            if (!published)
                return; // superseded while decoding - the newer refresh already owns everything below

            if (forceSweep)
                _forceDiskSweep = false;

            // What the disk search found is remembered, so the scans of the next hours only re-check these folders.
            if (result.DiskSweep is { } sweep)
            {
                _settings.DiscoveredFolders = sweep.Folders;
                _settings.LastDiskSweepUtc = sweep.SweptUtc;
                _settings.DiskSweepSignature = sweep.Signature;
            }

            _settingsService.Save(_settings); // persists the DateAdded/watched-folder changes merged above
            RefreshDrives();

            // Sizes support the "Largest installed" sort library-wide, even though their visual rows
            // are shown only in the selected-drive view. Fire-and-forget: a background refinement
            // of already-usable results, and it cancels itself if another pass supersedes it.
            _ = EstimateInstallSizesAsync();

            // Count from the filtered collection, not _allGames directly - scanning now always covers
            // every source (see GameScannerService), so _allGames includes games from sources the user
            // has toggled off. Games alone is the whole on-screen grid: favourites are a VIEW of it
            // now, not a separate section lifted out of it, so adding FavoriteGames here double-counted
            // every starred game ("9 of 6 games shown").
            var shown = Games.Count;
            var totalFound = _allGames.Count(g => !g.Hidden);
            var shownWord = shown == 1 ? "game" : "games";
            StatusText = shown == totalFound
                ? $"{shown} {shownWord} found"
                : $"{shown} of {totalFound} {shownWord} shown ({totalFound - shown} hidden by disabled sources)";

            LibraryRefreshed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh - that one owns IsLoading/StatusText/the results now.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Scan failed.", ex);

            // A superseded scan can still fault after being cancelled (it's cooperative, not
            // instant) - without this check, an old scan's failure could stomp "Scan failed" over
            // whatever the newer, still-running refresh has already put in StatusText.
            if (ReferenceEquals(_refreshCts, cts))
                StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            // Only the still-current refresh clears IsLoading/trims memory - a superseded refresh
            // reaching this point after being cancelled must not stomp on the state of whichever
            // refresh superseded it and is still in flight.
            if (ReferenceEquals(_refreshCts, cts))
            {
                IsLoading = false;
                ResetScanProgress();

                // A scan is a burst of allocation (file/registry walking, decoding cover art) and the
                // app goes idle straight after. Hand back what that burst left resident.
                MemoryTrimmer.Trim("after scan");
            }
        }
    }

    private void ResetScanProgress()
    {
        ScanProgressPercent = 0;
        ScanProgressText = "";
        IsScanIndeterminate = true;
    }

    /// <summary>Shows one step of the running scan. A report that arrives after the scan finished, or from a scan that has since been
    /// superseded, is ignored - it must not repaint a bar that now belongs to the newer scan (or to nothing). The bar never goes backwards.</summary>
    internal void ApplyScanProgress(ScanProgress progress, CancellationTokenSource? owner)
    {
        if (!IsLoading || (owner is not null && !ReferenceEquals(_refreshCts, owner)))
            return;

        IsScanIndeterminate = false;
        ScanProgressPercent = Math.Max(ScanProgressPercent, progress.Percent);
        ScanProgressText = progress.Text;
        StatusText = progress.Text;
    }
}
