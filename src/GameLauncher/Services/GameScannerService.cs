using System.IO;
using System.Windows.Media.Imaging;
using GameLauncher.Models;
using GameLauncher.Services.Identity;

namespace GameLauncher.Services;

/// <summary>Output of a scan: the games found, plus any DateAdded values assigned to previously-unseen
/// (or previously-DateAdded-less) games, plus any watched folders that got healed (a first-time volume
/// anchor captured, or a path re-derived after a drive-letter change) during this scan. None of this
/// can be written straight into the live AppSettings from the background scan thread - see
/// ScanAllAsync's remarks - so it all travels back as plain data for LibraryViewModel to merge into
/// the live settings on the UI thread instead, once that scan is confirmed to still be the current one.</summary>
/// <summary>MergedGameIds maps a superseded entry's id to the id of the surviving entry it was folded
/// into during de-duplication (see GameScannerService.DeduplicateByInstallLocation) - the caller uses
/// this to migrate the loser's Favorite/Hidden/CustomName/DateAdded override onto the winner rather
/// than letting it become orphaned.</summary>
public sealed record ScanResult(
    List<GameEntry> Games,
    Dictionary<string, DateTime> NewDateAddedByGameId,
    List<HealedWatchedFolder> HealedWatchedFolders,
    Dictionary<string, string> MergedGameIds,
    Dictionary<string, ArtworkApplyResult> ArtworkResultsByGameId);

/// <summary>One game's artwork outcome from a single scan - AsOfRevision is the live GameOverride.
/// ArtworkRevision this scan captured (via its private Overrides snapshot) BEFORE doing any of its own
/// (possibly slow, network-bound) matching work. LibraryViewModel.ApplyScanResult only trusts
/// AutomaticResult if the LIVE revision still equals AsOfRevision at publish time - if a Change Cover or
/// Reset landed on this exact game while the scan was still running, the live revision will have moved
/// past it, and this result is correctly discarded as stale rather than clobbering something newer.
/// AutomaticResult is null when an existing user selection was kept as-is (nothing new to persist) or
/// when no automatic match was found (the game fell back to its exe icon).</summary>
public sealed record ArtworkApplyResult(long AsOfRevision, ArtworkSelection? AutomaticResult, AutomaticUnitResult? Unit = null);

/// <summary>One game's AUTOMATIC UNIT (design 6.5) as the worker produced it, with the currency tokens it captured
/// BEFORE any slow work: the identity revision, the session identity generation and (inside Query) the fingerprint of the
/// inputs it used. The UI-thread commit re-checks every one of them against the LIVE state before applying either half.
/// WorkerShowedArt records whether the worker provisionally put cover pixels on the GameEntry (so a commit that rejects
/// the artwork half knows it must replace them).</summary>
public sealed record AutomaticUnitResult(UnitOutput Output, IdentityQuery Query, long IdentityRevision, long IdentityGeneration, bool WorkerShowedArt);

/// <summary>A watched folder as it looked after WatchedFolderResolver ran on this scan's private copy
/// of it. OriginalPath is the Path the live WatchedFolder had when this scan snapshotted it, used to
/// find the matching live object again on the UI thread (the healed Path itself may have changed).</summary>
public sealed record HealedWatchedFolder(string OriginalPath, uint? VolumeSerialNumber, string? RelativePath, string HealedPath);

public sealed class GameScannerService
{
    private readonly IgdbCredentialStore _credentialStore;

    /// <summary>The credential store is required, not defaulted - a scanner that silently fell back to
    /// the production store would let any test that runs a scan read (and use, over the real network) the
    /// developer's real IGDB secret. LibraryViewModel passes the store it derived from its own
    /// SettingsService directory.</summary>
    public GameScannerService(IgdbCredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    /// <summary>
    /// Always scans every launcher, regardless of its Detect toggle - the sidebar needs to know
    /// whether a source has any games at all (to decide whether its row shows up) independent of
    /// whether the user currently has it switched on, and toggling a source should only filter the
    /// library view, not force a rescan. LibraryViewModel.ApplyFilter applies the Detect toggles.
    /// </summary>
    /// <param name="identityGenerations">A snapshot of the session-only per-game identity generation, taken on the UI thread
    /// when the scan starts - what lets the commit tell that ANY identity write (user, merge, another unit) happened since.</param>
    /// <param name="resolutionContextOverride">Test seam: replaces the real catalog providers, launcher art and legacy cache.</param>
    /// <param name="progress">Told what the scan is doing as it goes (each launcher source, then each game), for the progress bar. Reported from
    /// the scan's background thread; a Progress&lt;T&gt; created on the UI thread delivers it there.</param>
    /// <param name="displayedCoverIds">A snapshot, taken on the UI thread when the scan starts, of the games whose card is showing cover
    /// pixels right now. The scan builds brand-new entries, so this is the only way the resolver can tell a recorded cover that is on
    /// screen (worth protecting through an outage) from one that only exists as a record after a restart.</param>
    public Task<ScanResult> ScanAllAsync(AppSettings settings, CancellationToken ct = default,
        IReadOnlyDictionary<string, long>? identityGenerations = null, ResolutionContext? resolutionContextOverride = null,
        IReadOnlySet<string>? displayedCoverIds = null, IProgress<ScanProgress>? progress = null)
    {
        // Deep-copied here, synchronously on the caller's (UI) thread, rather than letting the
        // background scan below touch settings.WatchedFolders (or the WatchedFolder objects inside it)
        // live: AddFolder/RemoveFolder mutate that same List<WatchedFolder> on the UI thread, and
        // ManualFolderScanner enumerates it - a scan still walking the list when a folder is
        // added/removed concurrently would throw InvalidOperationException ("Collection was
        // modified"). A shallow .ToList() isn't enough on its own, though: WatchedFolderResolver
        // mutates a WatchedFolder's Path/VolumeSerialNumber/RelativePath in place while healing it
        // (see ManualFolderScanner -> WatchedFolderResolver.TryResolve), and a cancelled-but-still-
        // running scan can genuinely be doing that on a background thread at the same instant a
        // freshly-started replacement scan starts healing the very same shared object. Copying the
        // objects themselves, not just the list, means this scan only ever mutates its own private
        // copies - any healing gets reported back through ScanResult.HealedWatchedFolders instead, for
        // LibraryViewModel to merge into the live objects on the UI thread, the same way DateAdded is.
        var watchedFolders = settings.WatchedFolders
            .Select(w => new WatchedFolder { Path = w.Path, VolumeSerialNumber = w.VolumeSerialNumber, RelativePath = w.RelativePath })
            .ToList();
        var originalPaths = watchedFolders.Select(w => w.Path).ToList(); // parallel to watchedFolders, captured pre-heal

        // Same reasoning for Overrides, with a sharper failure mode: cancelling a superseded scan is
        // only cooperative (it stops at the next ct.ThrowIfCancellationRequested(), not instantly), so
        // a cancelled scan's Task.Run body and a freshly-started replacement's can both be genuinely
        // running on separate thread-pool threads at the same instant - not just interleaved, actually
        // concurrent. A shallow dictionary copy still shares every GameOverride value with the live
        // dictionary, and the UI thread can mutate one of those (ToggleFavorite/ToggleHidden) at any
        // moment while a scan is reading it - so every value is copied too. The scan below only ever
        // reads from this private snapshot - see the DateAdded handling further down for how new
        // entries get back to the live dictionary safely instead.
        var overridesSnapshot = settings.Overrides.ToDictionary(
            kv => kv.Key,
            kv => new GameOverride
            {
                CustomName = kv.Value.CustomName,
                Hidden = kv.Value.Hidden,
                Favorite = kv.Value.Favorite,
                DateAdded = kv.Value.DateAdded,
                ArtworkRevision = kv.Value.ArtworkRevision,
                // The identity record is copied too (Clone copies the lists; the entries are immutable records): a
                // scan reads only its own private snapshot, exactly as for every other override field.
                Identity = kv.Value.Identity?.Clone(),
                IdentityRevision = kv.Value.IdentityRevision,
                DecisionRevision = kv.Value.DecisionRevision,
                // A new ArtworkSelection instance, not the live one by reference - sharing it would let
                // the UI thread's own Change-Cover/Reset commit (which mutates a GameOverride's Artwork
                // field in place) race this scan thread reading the same object, the exact class of bug
                // this whole snapshot exists to prevent for every other override field already.
                Artwork = kv.Value.Artwork is { } a
                    ? new ArtworkSelection
                    {
                        Provider = a.Provider,
                        RetrievedFrom = a.RetrievedFrom,
                        ProviderGameId = a.ProviderGameId,
                        ProviderArtworkRef = a.ProviderArtworkRef,
                        ProviderTitle = a.ProviderTitle,
                        AssetId = a.AssetId,
                        AssetExtension = a.AssetExtension,
                        MatchMethod = a.MatchMethod,
                        IsUserSelected = a.IsUserSelected,
                        SelectedAt = a.SelectedAt,
                        DerivedFrom = a.DerivedFrom,
                    }
                    : null,
            });

        // Snapshotted for the same reason: CoverArtService.Apply used to receive the live AppSettings
        // and read SteamGridDbApiKey straight off it from the background thread, retaining a reference
        // to shared mutable state on the worker for no reason - it only ever needed this one value.
        var steamGridDbApiKey = settings.SteamGridDbApiKey;
        var igdbClientId = settings.IgdbClientId;
        // Loaded here, synchronously, before Task.Run below - not inside the background lambda - for the
        // same reason steamGridDbApiKey is captured as a local rather than read from a live reference:
        // this must be a snapshot taken before crossing onto the worker thread, not a live read from it.
        // IgdbCredentialStore.LoadSecret() does its own file IO/DPAPI unprotect, which is safe to call
        // from either thread in isolation, but capturing it here keeps the discipline identical to every
        // other credential this method already snapshots up front.
        var igdbClientSecret = _credentialStore.LoadSecret();

        // One context per scan (its breaker and revalidation budget are per-scan state). Built from the VALUE snapshots
        // above - a background worker never reads live settings.
        var resolutionContext = resolutionContextOverride ?? new ResolutionContext
        {
            Providers = CatalogProviders.Create(igdbClientId, igdbClientSecret, steamGridDbApiKey),
            LauncherArt = SteamLauncherArt.Fetch,
            LegacyCacheRoot = Path.Combine(AppPaths.DataDir, "CoverArtCache"),
            Budget = new ResolutionBudget(25),
        };

        return Task.Run(() =>
        {
            Logger.Info("Scan started.");
            var results = new List<GameEntry>();

            results.AddRange(RunSources(
            [
                ("Steam", SteamScanner.Scan),
                ("Epic", EpicScanner.Scan),
                ("GOG", GogScanner.Scan),
                ("Xbox", XboxScanner.Scan),
                ("EA", EaScanner.Scan),
                ("Ubisoft Connect", UbisoftScanner.Scan),
                ("Battle.net", BattleNetScanner.Scan),
                ("Rockstar Games Launcher", RockstarScanner.Scan),
                ("Amazon Games", AmazonGamesScanner.Scan),
                ("Manual folders", () => ManualFolderScanner.Scan(watchedFolders)),
            ], progress, ct));

            var (deduped, mergedGameIds) = DeduplicateByInstallLocation(results);

            if (results.Count != deduped.Count)
                Logger.Info($"De-duplicated {results.Count - deduped.Count} overlapping entr(y/ies).");

            var newDateAdded = new Dictionary<string, DateTime>();
            var artworkResults = new Dictionary<string, ArtworkApplyResult>();

            var identified = 0;
            foreach (var game in deduped)
            {
                progress?.Report(new ScanProgress(ScanPhase.IdentifyingGames, identified++, deduped.Count, game.Name));

                // Cover art fetching below is the slowest part of a scan (network round-trips per
                // game) - checking here, not just between sources above, means a superseded scan
                // actually stops promptly instead of grinding through the rest of the library first.
                ct.ThrowIfCancellationRequested();

                overridesSnapshot.TryGetValue(game.Id, out var over);
                var dateAdded = over?.DateAdded;
                if (dateAdded is null)
                {
                    dateAdded = DateTime.UtcNow;
                    newDateAdded[game.Id] = dateAdded.Value; // merged into the live settings by the caller
                }

                if (over is not null && !string.IsNullOrWhiteSpace(over.CustomName))
                    game.Name = over.CustomName;
                game.Hidden = over?.Hidden ?? false;
                game.Favorite = over?.Favorite ?? false;
                game.DateAdded = dateAdded.Value;

                var asOfRevision = over?.ArtworkRevision ?? 0;
                artworkResults[game.Id] = ResolveGameUnit(game, over, asOfRevision, identityGenerations, resolutionContext, ct,
                    coverDisplayed: displayedCoverIds?.Contains(game.Id) ?? false);
                game.PlatformIcon = PlatformIconService.GetIcon(game.Source);
            }

            progress?.Report(new ScanProgress(ScanPhase.IdentifyingGames, deduped.Count, deduped.Count));

            // Closes the one gap the per-item check above can't: cancellation arriving during the
            // last item's (synchronous, blocking) cover-art fetch has nowhere left to be observed
            // before the method would otherwise return normally with a "successful" but stale result.
            ct.ThrowIfCancellationRequested();

            foreach (var group in deduped.GroupBy(g => g.Source))
                Logger.Info($"{group.Key}: {string.Join(", ", group.Select(g => $"'{g.Name}'"))}");

            var coverArtCount = deduped.Count(g => g.IsCoverArt);
            Logger.Info($"Cover art: {coverArtCount}/{deduped.Count} game(s) got real box art, "
                + $"{deduped.Count - coverArtCount} fell back to the exe icon.");
            Logger.Info($"Scan finished: {deduped.Count} game(s) total.");

            // watchedFolders holds this scan's private copies, already healed in place by
            // ManualFolderScanner -> WatchedFolderResolver above - pairing each with the path it had
            // before healing is how LibraryViewModel finds the matching live object to update.
            var healedFolders = new List<HealedWatchedFolder>();
            for (var i = 0; i < watchedFolders.Count; i++)
            {
                healedFolders.Add(new HealedWatchedFolder(
                    originalPaths[i], watchedFolders[i].VolumeSerialNumber, watchedFolders[i].RelativePath, watchedFolders[i].Path));
            }

            // Final ordering doesn't matter here - LibraryViewModel re-sorts per the user's chosen SortOption.
            return new ScanResult(deduped, newDateAdded, healedFolders, mergedGameIds, artworkResults);
        }, ct);
    }

    /// <summary>One game's automatic unit, run off the UI thread against private snapshots. Everything it needs was captured
    /// BEFORE the slow work (the override snapshot, the identity generation, the fingerprint inside the query); the UI-thread
    /// commit re-checks all of it against live state. A user-pinned cover is never replaced (it is displayed as before) but
    /// identity is still resolved for it. An unexpected failure yields a unit that changes NOTHING - never a cleared cover.</summary>
    internal static ArtworkApplyResult ResolveGameUnit(GameEntry game, GameOverride? over, long asOfRevision,
        IReadOnlyDictionary<string, long>? identityGenerations, ResolutionContext context, CancellationToken ct, bool coverDisplayed = false)
    {
        var query = IdentityQuery.From(game); // built from DetectedTitle, never from the (possibly custom) display name
        var generation = identityGenerations?.GetValueOrDefault(game.Id) ?? 0;
        var identityRevision = over?.IdentityRevision ?? 0;
        var pinned = over?.Artwork is { IsUserSelected: true };

        if (pinned)
            CoverArtService.ApplyStoredSafely(game, over!.Artwork!); // never throws; the pinned cover is authoritative

        UnitOutput output;
        try
        {
            output = AutomaticResolver.Run(new UnitInput
            {
                GameId = game.Id,
                Query = query,
                Prior = over?.Identity,
                CurrentArtwork = over?.Artwork,
                CurrentArtworkDisplayed = coverDisplayed,
                ArtworkPinned = pinned,
            }, context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a superseded scan is not a per-game failure
        }
        catch (Exception ex)
        {
            Logger.Warn($"Automatic identity/cover resolution failed unexpectedly for '{game.Name}' - leaving it unchanged.", ex);
            output = new UnitOutput
            {
                NewRecord = over?.Identity is { IsQuarantined: true } ? null : (over?.Identity?.Clone() ?? new GameIdentityRecord()),
                Artwork = ArtworkHalf.None,
            };
        }

        var showedArt = false;
        if (!pinned)
        {
            // Provisional pixels only: the commit publishes them if - and only if - the halves validate. Otherwise it
            // replaces them, so nothing is ever shown that the authorization predicate does not allow.
            if (output.Artwork is { Kind: ArtworkHalfKind.Set, Image: { } image })
            {
                game.Icon = image;
                game.IsCoverArt = true;
                showedArt = true;
            }
            else
            {
                CoverArtService.ApplyIconFallbackSafely(game);
            }
        }

        return new ArtworkApplyResult(asOfRevision, output.Artwork.Selection, new AutomaticUnitResult(output, query, identityRevision, generation, showedArt));
    }

    /// <summary>
    /// De-duplicates by install location and reports which ids got merged away. Two entries count as
    /// the same install when their InstallDir paths are identical - the original rule - OR when a
    /// Manual entry's path is nested under (or exactly equal to) a launcher-detected entry's own root -
    /// a real, confirmed case: EA's "A Way Out" install root is G:\EASports\AWayOut, but its executable
    /// sits three levels deeper at ...\Haze1\Binaries\Win64\AWayOut.exe, so a manually-watched folder
    /// covering the same drive discovers that deep folder as its own, unrelated-looking "game" (see
    /// ManualFolderScanner - it assigns identity from the exe's own containing folder, not the true
    /// root), with its own generated Id, and even guesses the wrong display name from EA's internal
    /// "Haze1" codename subfolder along the way. Without recognizing the nesting, both a correct
    /// EA-sourced entry and a wrong Manual one would appear side by side as two separate games.
    ///
    /// The nesting check only ever fires when exactly one side is GameSource.Manual: two launcher-
    /// verified entries (a base game and a separately registered DLC/expansion installed as its own
    /// subfolder, say) must never be silently merged just because one path happens to sit inside the
    /// other - only a generic, less-trustworthy Manual scan result gets reconciled away like this, and
    /// only into a launcher's own verified install. See FindMergeTarget for exactly how the specific
    /// owner is chosen when MULTIPLE launcher entries could all plausibly be an ancestor (a base game
    /// AND its own expansion, say) - this does NOT simply take the first one found.
    ///
    /// The returned dictionary (loser id -> surviving id) lets the caller migrate the loser's
    /// Favorite/Hidden/CustomName/DateAdded override before it becomes orphaned - see
    /// LibraryViewModel.RefreshAsync.
    /// </summary>
    internal static (List<GameEntry> Deduped, Dictionary<string, string> MergedGameIds) DeduplicateByInstallLocation(
        List<GameEntry> results)
    {
        var ordered = results.OrderBy(g => g.Source == GameSource.Manual ? 1 : 0).ToList();
        var kept = new List<GameEntry>();
        var mergedGameIds = new Dictionary<string, string>();

        foreach (var candidate in ordered)
        {
            var match = FindMergeTarget(kept, candidate);
            if (match is null)
            {
                kept.Add(candidate);
                continue;
            }

            if (!string.Equals(match.Id, candidate.Id, StringComparison.Ordinal))
                mergedGameIds[candidate.Id] = match.Id;
        }

        return (kept, mergedGameIds);
    }

    /// <summary>
    /// Finds the already-`kept` entry (if any) that `candidate` should merge into, or null if it's
    /// genuinely a new, separate game. Two rules, in order:
    ///
    ///  1. EXACT InstallDir match - the original, unconditional rule (a manually-watched folder pointed
    ///     directly at a launcher's own library folder, say). An identical install location IS the same
    ///     game by definition; no further corroboration is needed or sought.
    ///
    ///  2. A nested Manual entry, resolved by TWO further checks together - neither is sufficient alone:
    ///       a) MOST SPECIFIC ancestor, not the first one found. Real, confirmed bug this replaces: with
    ///          a base game at C:\Games\Base and its own expansion at C:\Games\Base\Expansion, a Manual
    ///          entry discovered at C:\Games\Base\Expansion\Bin has BOTH as valid ancestor candidates -
    ///          picking whichever happened to be scanned/kept first (list order, which callers have no
    ///          control over) could easily merge the expansion's own nested content into the unrelated
    ///          BASE game instead. Only the longest (closest-nested) ancestor path is even considered.
    ///       b) VERIFIED-EXECUTABLE corroboration, checked per-candidate (not only as a final check
    ///          against whichever ancestor happened to be most specific by path length - see below for
    ///          why). The default, and by far the common, rule is EXACT executable equality - both
    ///          scanners resolved to the identical file. Sharing a folder is NOT, on its own, proof of
    ///          anything: a real, confirmed false-merge counterexample shows why - a watched folder
    ///          covering Base\Bin directly, which genuinely contains BOTH a launcher-verified game's own
    ///          Base.exe AND a completely separate, unrelated Indie.exe as siblings, would merge Indie
    ///          into Base purely because they happen to sit in the same directory, if folder equality
    ///          alone were trusted. See IsVerifiedSameExecutable for the one narrow, specifically-scoped
    ///          exception to exact equality this allows (EA's "Friend's Pass" naming convention) and why
    ///          it does not reopen that hole.
    ///
    ///          Because the executable check is evaluated per-candidate rather than only against a
    ///          pre-selected "most specific by length" winner, a shorter ancestor path that DOES verify
    ///          is never shadowed by a longer one that merely happens to be more deeply nested but
    ///          doesn't actually verify.
    /// </summary>
    private static GameEntry? FindMergeTarget(List<GameEntry> kept, GameEntry candidate)
    {
        var candidatePath = NormalizeDir(candidate.InstallDir);

        var exactMatch = kept.FirstOrDefault(k =>
            string.Equals(NormalizeDir(k.InstallDir), candidatePath, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
            return exactMatch;

        GameEntry? bestAncestor = null;
        var bestAncestorPathLength = -1;

        foreach (var existing in kept)
        {
            // Nesting only ever merges a Manual entry into a non-Manual one - exactly one side must be
            // Manual (an XOR, not "either/both"), so two entries of the same "Manual-ness" never merge
            // via nesting with each other.
            if ((existing.Source == GameSource.Manual) == (candidate.Source == GameSource.Manual))
                continue;

            var (launcherEntry, manualEntry) = existing.Source == GameSource.Manual
                ? (candidate, existing)
                : (existing, candidate);
            var launcherPath = NormalizeDir(launcherEntry.InstallDir);
            var manualPath = NormalizeDir(manualEntry.InstallDir);

            if (!IsAncestorDir(launcherPath, manualPath))
                continue;

            if (!IsVerifiedSameExecutable(launcherEntry.ExecutablePath, manualEntry.ExecutablePath, launcherEntry.Source))
                continue;

            if (launcherPath.Length > bestAncestorPathLength)
            {
                bestAncestor = existing;
                bestAncestorPathLength = launcherPath.Length;
            }
        }

        return bestAncestor;
    }

    /// <summary>Whether `manualExe` can be trusted as referring to the same executable as `launcherExe` -
    /// EXACT equality is the default and by far the common case (both scanners resolved to the identical
    /// file), available to every source. The one narrow exception is EA's confirmed "Friend's Pass"
    /// naming convention: the SAME folder legitimately contains two builds of the SAME game - the real
    /// one and a free-trial-for-a-friend one - differing only by a "_friend" suffix on the filename
    /// (AWayOut.exe / AWayOut_friend.exe). That exception is scoped as narrowly as the real case that
    /// motivated it in every dimension, not just the filename shape: `launcherSource` must be
    /// GameSource.Ea (a real, confirmed gap in an earlier version of this method checked only the
    /// filenames, so a Steam/Xbox/Ubisoft/etc. game whose folder happened to also contain an unrelated
    /// "Foo_friend.exe" would have been accepted as a match too - Friend's Pass is an EA-specific
    /// program, not a general naming convention any source could plausibly use), the base filename (no
    /// extension) must match exactly except for that specific suffix, AND it must be in the SAME folder -
    /// not "any two files in the same folder," which was itself a separate, confirmed false-merge bug
    /// (see FindMergeTarget's own remarks for the Base.exe/Indie.exe counterexample this replaced). A
    /// completely unrelated exe sharing a folder with a launcher's own game - Indie.exe next to Base.exe -
    /// matches neither rule and is correctly left unverified, for every source including EA.</summary>
    private static bool IsVerifiedSameExecutable(string launcherExe, string manualExe, GameSource launcherSource)
    {
        if (string.Equals(launcherExe, manualExe, StringComparison.OrdinalIgnoreCase))
            return true;

        if (launcherSource != GameSource.Ea)
            return false;

        var launcherDir = Path.GetDirectoryName(launcherExe);
        var manualDir = Path.GetDirectoryName(manualExe);
        if (launcherDir is null || manualDir is null
            || !string.Equals(NormalizeDir(launcherDir), NormalizeDir(manualDir), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var launcherBaseName = Path.GetFileNameWithoutExtension(launcherExe);
        var manualBaseName = Path.GetFileNameWithoutExtension(manualExe);
        return string.Equals(
            manualBaseName, launcherBaseName + GameExeFinder.EaFriendsPassExcludePattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDir(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsAncestorDir(string ancestor, string descendant) =>
        descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Isolates one game's cover-art enrichment the same way SafeScan isolates one source's scan below -
    // CoverArtService.Apply and the individual cover art providers already handle their own EXPECTED
    // failure modes (HTTP errors, a malformed API response shape - see SteamGridDbCoverArtProvider) and
    // fall back to the exe icon on their own; this is the last-resort backstop for anything they don't
    // anticipate, so one bad item can't abort enrichment for every other game still waiting in this
    // loop, or the scan as a whole.
    /// <summary>`applyCoverArt`/`getIcon` default to the real CoverArtService.Apply/IconService.GetIcon -
    /// overridable (internal, same pattern as SafeScan's own `scan` parameter) so tests can force both
    /// the primary attempt AND the fallback to throw deterministically, without depending on a real
    /// HTTP failure or a real corrupt exe to trigger it.
    ///
    /// `existingSelection` short-circuits straight to loading a previously user-selected image (via
    /// `applyStoredArtworkSafely`, itself already fully crash-isolated - see CoverArtService.
    /// ApplyStoredSafely) instead of ever running the automatic matcher - an explicit selection is
    /// authoritative and must never be silently replaced by a fresh automatic guess just because a scan
    /// happened to run. Returns null in that case: there is nothing NEW to persist, the existing
    /// selection stands unchanged. Returns the automatic match's identity/evidence otherwise (null if
    /// none was found), for the caller to record as automatic metadata.</summary>
    internal static ArtworkSelection? SafeApplyCoverArt(
        GameEntry game, string? steamGridDbApiKey, ArtworkSelection? existingSelection,
        string? igdbClientId = null, string? igdbClientSecret = null, CancellationToken ct = default,
        Func<GameEntry, string?, ArtworkSelection?>? applyCoverArt = null,
        Action<GameEntry, ArtworkSelection>? applyStoredArtworkSafely = null,
        Func<GameEntry, BitmapImage?>? getIcon = null)
    {
        // applyCoverArt's own seam type is deliberately left at CoverArtService.Apply's OLD two-argument
        // shape (game, steamGridDbApiKey) - widening it to also carry the IGDB credentials/ct would break
        // every existing test that already supplies a two-argument override here (none of them are
        // testing IGDB specifically; CoverArtServiceTests/IgdbCoverArtProviderTests cover that
        // separately). The real, production default below closes over igdbClientId/igdbClientSecret/ct
        // itself instead, so the live path still gets them without changing the seam's shape. ct lets a
        // superseded scan interrupt an in-flight IGDB cover-image download rather than letting it run to
        // completion regardless - see CoverArtService.Apply/IgdbCoverArtProvider.GetCoverArt's own remarks.
        applyCoverArt ??= (g, key) => CoverArtService.Apply(g, key, igdbClientId, igdbClientSecret, ct: ct);
        applyStoredArtworkSafely ??= (g, s) => CoverArtService.ApplyStoredSafely(g, s);
        getIcon ??= IconService.GetIcon;

        if (existingSelection is { IsUserSelected: true })
        {
            applyStoredArtworkSafely(game, existingSelection); // never throws
            return null;
        }

        try
        {
            return applyCoverArt(game, steamGridDbApiKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The scan itself was superseded - NOT a per-game enrichment failure. Falling back to the exe
            // icon here (the catch below) would swallow the cancellation and let the loop go on to start
            // network work for the next game; rethrowing lets ScanAllAsync's own cancellation handling
            // (the same one its ct.ThrowIfCancellationRequested() checks feed) end the scan promptly.
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Cover art enrichment failed unexpectedly for '{game.Name}' - falling back to the exe icon.", ex);

            // The fallback call itself is not exempt from failing - CoverArtService.Apply's own normal
            // fallback path calls this exact same icon lookup, so if icon extraction/decoding was what
            // threw in the first place, retrying it here can throw again too. ApplyIconFallbackSafely is
            // isolated separately so a second failure still can't escape this method and abort enrichment
            // for every other game still waiting in the caller's loop - if both attempts fail, this
            // simply leaves Icon null and IsCoverArt false, and the UI's own built-in glyph covers the rest.
            CoverArtService.ApplyIconFallbackSafely(game, getIcon);
            return null;
        }
    }

    // Isolates one source's failure (e.g. a Steam/manual library on a now-unplugged external drive)
    // so it can't wipe out games already found from every other source.
    /// <summary>Runs each launcher source in order, telling `progress` which one is about to run, and stops promptly if the scan is superseded.
    /// One failing source never takes the others down (SafeScan).</summary>
    internal static List<GameEntry> RunSources(IReadOnlyList<(string Name, Func<List<GameEntry>> Scan)> sources, IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<GameEntry>();
        for (var i = 0; i < sources.Count; i++)
        {
            progress?.Report(new ScanProgress(ScanPhase.LookingForGames, i, sources.Count, sources[i].Name));
            results.AddRange(SafeScan(sources[i].Name, sources[i].Scan));
            ct.ThrowIfCancellationRequested();
        }

        progress?.Report(new ScanProgress(ScanPhase.LookingForGames, sources.Count, sources.Count));
        return results;
    }

    private static List<GameEntry> SafeScan(string sourceName, Func<List<GameEntry>> scan)
    {
        try
        {
            var found = scan();
            Logger.Info($"{sourceName}: found {found.Count} game(s).");
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"{sourceName}: scan failed, skipping this source for now.", ex);
            return [];
        }
    }
}
