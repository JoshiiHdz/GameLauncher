using System.IO;
using System.Windows.Media.Imaging;
using GameLauncher.Models;

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
public sealed record ArtworkApplyResult(long AsOfRevision, ArtworkSelection? AutomaticResult);

/// <summary>A watched folder as it looked after WatchedFolderResolver ran on this scan's private copy
/// of it. OriginalPath is the Path the live WatchedFolder had when this scan snapshotted it, used to
/// find the matching live object again on the UI thread (the healed Path itself may have changed).</summary>
public sealed record HealedWatchedFolder(string OriginalPath, uint? VolumeSerialNumber, string? RelativePath, string HealedPath);

public sealed class GameScannerService
{
    /// <summary>
    /// Always scans every launcher, regardless of its Detect toggle - the sidebar needs to know
    /// whether a source has any games at all (to decide whether its row shows up) independent of
    /// whether the user currently has it switched on, and toggling a source should only filter the
    /// library view, not force a rescan. LibraryViewModel.ApplyFilter applies the Detect toggles.
    /// </summary>
    public Task<ScanResult> ScanAllAsync(AppSettings settings, CancellationToken ct = default)
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
                    }
                    : null,
            });

        // Snapshotted for the same reason: CoverArtService.Apply used to receive the live AppSettings
        // and read SteamGridDbApiKey straight off it from the background thread, retaining a reference
        // to shared mutable state on the worker for no reason - it only ever needed this one value.
        var steamGridDbApiKey = settings.SteamGridDbApiKey;

        return Task.Run(() =>
        {
            Logger.Info("Scan started.");
            var results = new List<GameEntry>();

            results.AddRange(SafeScan("Steam", SteamScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Epic", EpicScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("GOG", GogScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Xbox", XboxScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("EA", EaScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Ubisoft Connect", UbisoftScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Battle.net", BattleNetScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Rockstar Games Launcher", RockstarScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Amazon Games", AmazonGamesScanner.Scan));
            ct.ThrowIfCancellationRequested();
            results.AddRange(SafeScan("Manual folders", () => ManualFolderScanner.Scan(watchedFolders)));
            ct.ThrowIfCancellationRequested();

            var (deduped, mergedGameIds) = DeduplicateByInstallLocation(results);

            if (results.Count != deduped.Count)
                Logger.Info($"De-duplicated {results.Count - deduped.Count} overlapping entr(y/ies).");

            var newDateAdded = new Dictionary<string, DateTime>();
            var artworkResults = new Dictionary<string, ArtworkApplyResult>();

            foreach (var game in deduped)
            {
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
                var automaticResult = SafeApplyCoverArt(game, steamGridDbApiKey, over?.Artwork);
                artworkResults[game.Id] = new ArtworkApplyResult(asOfRevision, automaticResult);
                game.PlatformIcon = PlatformIconService.GetIcon(game.Source);
            }

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
        Func<GameEntry, string?, ArtworkSelection?>? applyCoverArt = null,
        Action<GameEntry, ArtworkSelection>? applyStoredArtworkSafely = null,
        Func<GameEntry, BitmapImage?>? getIcon = null)
    {
        applyCoverArt ??= CoverArtService.Apply;
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
