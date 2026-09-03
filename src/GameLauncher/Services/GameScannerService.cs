using System.IO;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>Output of a scan: the games found, plus any DateAdded values assigned to previously-unseen
/// (or previously-DateAdded-less) games, plus any watched folders that got healed (a first-time volume
/// anchor captured, or a path re-derived after a drive-letter change) during this scan. None of this
/// can be written straight into the live AppSettings from the background scan thread - see
/// ScanAllAsync's remarks - so it all travels back as plain data for LibraryViewModel to merge into
/// the live settings on the UI thread instead, once that scan is confirmed to still be the current one.</summary>
public sealed record ScanResult(
    List<GameEntry> Games,
    Dictionary<string, DateTime> NewDateAddedByGameId,
    List<HealedWatchedFolder> HealedWatchedFolders);

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

            // De-duplicate by install dir (a manually-watched folder may overlap a launcher's library,
            // e.g. someone points a watched folder at their Steam "common" dir). Prefer the launcher-detected
            // entry over the generic manual one since it carries the correct launch URI/exe.
            var deduped = results
                .OrderBy(g => g.Source == GameSource.Manual ? 1 : 0)
                .GroupBy(g => g.InstallDir, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (results.Count != deduped.Count)
                Logger.Info($"De-duplicated {results.Count - deduped.Count} overlapping entr(y/ies).");

            var newDateAdded = new Dictionary<string, DateTime>();

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

                CoverArtService.Apply(game, steamGridDbApiKey);
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
            return new ScanResult(deduped, newDateAdded, healedFolders);
        }, ct);
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
