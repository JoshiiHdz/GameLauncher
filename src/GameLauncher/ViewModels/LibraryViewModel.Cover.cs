using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using Microsoft.Win32;

namespace GameLauncher.ViewModels;

/// <summary>Changing a game's cover, resetting it to the automatic one, and committing artwork changes safely against concurrent scans.</summary>
public partial class LibraryViewModel
{
    // Guards overlapping Apply/Reset for the SAME game - a second request for a game already mid-commit
    // is rejected outright rather than allowed to race the first's revision bump/asset write. This alone
    // does not protect the shared settings.json against two DIFFERENT games committing "concurrently" -
    // that's guaranteed instead by CommitArtworkChange being fully synchronous (no await inside it),
    // which WPF's single UI-thread dispatcher already serializes on its own; see CommitArtworkChange's
    // own remarks.
    private readonly HashSet<string> _artworkOperationsInFlight = new();

    // Test-only seams - production always leaves these null, in which case every artwork code path
    // resolves to the exact same real dependency (ArtworkAssetStore's own default directory,
    // IconService.GetIcon, CoverArtService.Apply) it would use without this indirection at all. Needed
    // because ArtworkAssetStore/CoverArtService have no other injectable seam reachable from
    // LibraryViewModel: without these, artwork tests would have to write into real %AppData%\GameLauncher
    // and Reset tests would depend on whether this checkout happens to have a real embedded SteamGridDB
    // key (default-api-key.txt) - both real, confirmed problems in an earlier version of this test suite.
    internal string? AssetStoreDirOverrideForTest { get; set; }

    internal Func<GameEntry, BitmapImage?>? IconFallbackForTest { get; set; }

    internal Func<GameEntry, string?, ArtworkSelection?>? AutomaticCoverArtLookupForTest { get; set; }

    /// <summary>Test-only: invoked by ApplyLocalCoverImageAsync right after the newly-staged asset file
    /// is written, before CommitArtworkChange applies it - lets a test delete/corrupt that file to prove
    /// CommitArtworkChange's `preparedIcon` path actually displays the already-decoded, in-memory bitmap
    /// from validation rather than re-reading the file it's about to hand this callback the chance to
    /// remove. Always null in production.</summary>
    internal Action<string>? AfterAssetWrittenForTest { get; set; }

    /// <summary>Write counterpart to GetOverride - sets up merge/reconciliation test scenarios directly
    /// (an existing user selection with a specific revision) without needing a full async Apply/Reset
    /// round-trip for every setup step.</summary>
    internal void SetArtworkForTest(string gameId, ArtworkSelection? artwork, long revision)
    {
        if (!_settings.Overrides.TryGetValue(gameId, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[gameId] = over;
        }

        over.Artwork = artwork;
        over.ArtworkRevision = revision;
    }

    /// <summary>Read-only test seam mirroring GetOverride, for AppSettings.ArtworkConflicts.</summary>
    internal IReadOnlyList<ArtworkConflict> ArtworkConflictsForTest => _settings.ArtworkConflicts;

    // ---- Change Cover / Reset to Automatic -----------------------------------------------------------
    //
    // ApplyLocalCoverImageAsync/ResetCoverToAutomaticAsync take a bare gameId and return an outcome enum
    // precisely so they're callable both directly by tests (see LibraryViewModelArtworkTests) and by the
    // two UI-facing commands below, without either caller needing to know about the other.

    /// <summary>Change Cover's validation-only half: reads and decodes a candidate file purely in
    /// memory - nothing on disk or in settings is touched. Split out from the old, single-shot
    /// ApplyLocalCoverImageAsync so the Preview -> Apply/Cancel dialog can validate and decode a
    /// candidate ONCE, show it, and only pass it on to ApplyValidatedCoverImageAsync (the side-effecting
    /// half) if and when the user actually clicks Apply - cancelling or closing the dialog simply
    /// discards the returned ValidatedImage, which is the entire "no persistent change until Apply"
    /// contract; there is nothing else to roll back because nothing was ever written.</summary>
    public Task<ArtworkImageValidator.ValidatedImage?> ValidateLocalCoverImageAsync(string localFilePath, CancellationToken ct = default)
        // Explicitly offloaded, not just awaited - ValidateLocalFileAsync's own internal awaits (a plain
        // bounded file read) resume on whatever context called it, which is the UI thread's dispatcher
        // when this runs from a UI action. Its real work - BitmapDecoder.Create with DelayCreation, plus
        // a full CoverArtDecoder.Decode - is genuine codec work (see ArtworkImageValidator's own
        // remarks), so relying on internal ConfigureAwait behavior alone would NOT guarantee it stays
        // off the UI thread; Task.Run does.
        => Task.Run(() => ArtworkImageValidator.ValidateLocalFileAsync(localFilePath, ct), ct);

    /// <summary>Change Cover's side-effecting half: stages `validated`'s bytes into ArtworkAssetStore and
    /// commits the selection - the same write-then-commit tail ApplyLocalCoverImageAsync always ran, now
    /// reusable by the Preview dialog's Apply step. `gameId`, not a GameEntry reference, is what a caller
    /// must hold across this call - a refresh can replace or merge the target game while staging is
    /// running; see CommitArtworkChange's re-resolution.
    ///
    /// Manages _artworkOperationsInFlight itself - ApplyLocalCoverImageAsync below does NOT call this
    /// method for that reason: it needs the guard held across ITS OWN validate step too (see its own
    /// remarks and ApplyValidatedCoverImageCoreAsync), and HashSet.Add is not reentrant - a second Add
    /// for a gameId already held by the SAME logical call would incorrectly report AlreadyInProgress
    /// against itself.</summary>
    public async Task<ArtworkChangeOutcome> ApplyValidatedCoverImageAsync(string gameId, ArtworkImageValidator.ValidatedImage validated, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>Runs both halves back-to-back, exactly as Change Cover behaved before the Preview
    /// dialog existed - kept for every direct caller that doesn't need a preview step (every existing
    /// test, and any future non-interactive caller). The guard is taken here, ONCE, covering both the
    /// validate and the write/commit steps - the same window the original single-method implementation
    /// held it for - so two overlapping calls for the same gameId are still deterministically resolved
    /// to exactly one Success and the rest AlreadyInProgress, regardless of how validation's own timing
    /// happens to interleave.</summary>
    public async Task<ArtworkChangeOutcome> ApplyLocalCoverImageAsync(string gameId, string localFilePath, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            var validated = await ValidateLocalCoverImageAsync(localFilePath, ct);
            if (validated is null)
                return ArtworkChangeOutcome.InvalidImage;

            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>The actual write+commit work, shared by both guarded entry points above - neither adds
    /// nor removes _artworkOperationsInFlight itself, so it can safely run under whichever of the two
    /// callers' own guard window is already held.</summary>
    private async Task<ArtworkChangeOutcome> ApplyValidatedCoverImageCoreAsync(string gameId, ArtworkImageValidator.ValidatedImage validated, CancellationToken ct,
        ArtworkSelection? catalogProvenance = null, long? expectedArtworkRevision = null)
    {
        // A stale preview is refused BEFORE anything is staged (design 6.7, D3): a newer cover, a Reset or a merge that landed
        // while the user was choosing must not be silently overwritten. CommitArtworkChange re-checks it at the commit itself.
        if (expectedArtworkRevision is { } expected
            && (_settings.Overrides.TryGetValue(gameId, out var current) ? current.ArtworkRevision : 0) != expected)
        {
            return ArtworkChangeOutcome.StaleSelection;
        }

        string assetId;
        try
        {
            assetId = await Task.Run(
                () => ArtworkAssetStore.Write(validated.Bytes, validated.Extension, AssetStoreDirOverrideForTest), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing was ever staged - the previous selection (if any) is simply left exactly as it
            // was; there is nothing to roll back, unlike CommitArtworkChange's SaveFailed case.
            Logger.Warn($"Couldn't stage the selected cover image for game '{gameId}'.", ex);
            return ArtworkChangeOutcome.StorageFailed;
        }

        AfterAssetWrittenForTest?.Invoke(assetId);

        // A pinned cover is a DISPLAY decision. When it came from a catalog, ProviderGameId/ProviderArtworkRef record where
        // the IMAGE came from (provenance) - never which game is installed, and it has no DerivedFrom: identity never
        // authorizes or removes it (I1, I3).
        return CommitArtworkChange(gameId, over => over.Artwork = new ArtworkSelection
        {
            Provider = catalogProvenance?.Provider ?? ArtworkProvider.UserLocalFile,
            RetrievedFrom = catalogProvenance is null ? ArtworkRetrievalMethod.UserSuppliedFile : ArtworkRetrievalMethod.NetworkDownload,
            ProviderGameId = catalogProvenance?.ProviderGameId,
            ProviderArtworkRef = catalogProvenance?.ProviderArtworkRef,
            ProviderTitle = catalogProvenance?.ProviderTitle,
            AssetId = assetId,
            AssetExtension = validated.Extension,
            MatchMethod = catalogProvenance is null ? "UserLocalFile" : "UserChosenCatalogCover",
            IsUserSelected = true,
            SelectedAt = DateTime.UtcNow,
            DerivedFrom = null,
        }, preparedIcon: validated.DecodedImage, expectedRevision: expectedArtworkRevision, ct: ct);
    }

    /// <summary>Choose Cover from a catalog: pins an image the user picked among a catalog entry's covers (already downloaded and
    /// validated) with its provenance. `expectedArtworkRevision` is the revision the dialog captured when it opened: if a newer
    /// cover, a Reset or a merge landed while the user was choosing, this returns StaleSelection and applies NOTHING. Identity is
    /// never read or written here.</summary>
    public async Task<ArtworkChangeOutcome> ApplyCatalogCoverAsync(string gameId, ArtworkImageValidator.ValidatedImage validated,
        ArtworkSelection provenance, long expectedArtworkRevision, CancellationToken ct = default)
    {
        gameId = ResolveAlias(gameId);
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            return await ApplyValidatedCoverImageCoreAsync(gameId, validated, ct, provenance, expectedArtworkRevision);
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    /// <summary>Clears the selection (synchronously, so the card immediately shows the exe icon rather
    /// than stale pixels), THEN kicks off an immediate, single-game automatic lookup - "the next scan
    /// will eventually pick it up" is not an acceptable implementation of Reset on its own; the user
    /// shouldn't have to manually refresh to see it take effect. The lookup's provider/network work runs
    /// off the UI thread against a private scratch GameEntry, never the live bound one (mutating that
    /// from a background thread would be the exact anti-pattern GameScannerService's own scan-snapshot
    /// discipline exists to avoid) - only once back on the UI thread, and only if nothing else has
    /// changed this game's artwork state in the meantime, is the result actually applied.</summary>
    public async Task<ArtworkChangeOutcome> ResetCoverToAutomaticAsync(string gameId, CancellationToken ct = default)
    {
        if (!_artworkOperationsInFlight.Add(gameId))
            return ArtworkChangeOutcome.AlreadyInProgress;

        try
        {
            var outcome = CommitArtworkChange(gameId, over => over.Artwork = null);
            if (outcome != ArtworkChangeOutcome.Success)
                return outcome;

            // Reset is an ARTWORK transaction only (identity untouched, I2). What follows is a SEPARATE, ordinary automatic
            // unit flagged Trigger=Reset (cooldown bypassed, rejections never): with an active identity it fetches by id
            // (zero title searches); with none it is the ordinary full resolution (design 6.6). The test seam below keeps the
            // older name-based lookup path for the tests that predate the identity pipeline.
            if (AutomaticCoverArtLookupForTest is null)
            {
                try
                {
                    await RunAutomaticUnitAsync(gameId, "Reset", force: true, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"'{gameId}': automatic cover lookup after Reset failed unexpectedly.", ex);
                }

                return outcome;
            }

            _settings.Overrides.TryGetValue(gameId, out var overAfterReset);
            var asOfRevision = overAfterReset?.ArtworkRevision ?? 0;

            var game = _allGames.FirstOrDefault(g => g.Id == gameId);
            if (game is null)
                return outcome; // vanished immediately after the reset itself succeeded - nothing more to do

            var apiKey = _settings.SteamGridDbApiKey;
            // Defaults to the real CoverArtService.Apply - overridable so tests can prove the immediate
            // lookup ran (or force a deterministic result) without depending on whether this checkout
            // happens to have a real embedded SteamGridDB key (default-api-key.txt) or reaching the
            // network either way. An explicit lambda, not a bare method group, because CoverArtService.
            // Apply has more optional parameters than AutomaticCoverArtLookupForTest's own two-argument
            // seam type.
            //
            // The IGDB credentials are captured HERE, on the UI thread, BEFORE Task.Run below, as value
            // snapshots the lambda closes over - a real, confirmed bug in an earlier version read
            // _settings.IgdbClientId from INSIDE the lambda, i.e. on the background thread, retaining a
            // live reference to shared mutable state (see GameScannerService.ScanAllAsync's own remarks).
            // Loaded only on the real path: when a test supplies AutomaticCoverArtLookupForTest, no
            // credential file is read at all.
            Func<GameEntry, string?, ArtworkSelection?> applyCoverArt;
            if (AutomaticCoverArtLookupForTest is { } lookupSeam)
            {
                applyCoverArt = lookupSeam;
            }
            else
            {
                var igdbClientId = _settings.IgdbClientId;
                var igdbClientSecret = _credentialStore.LoadSecret();
                applyCoverArt = (g, key) => CoverArtService.Apply(g, key, igdbClientId, igdbClientSecret, ct: ct);
            }
            (BitmapImage? Icon, bool IsCoverArt, ArtworkSelection? Automatic) computed;
            try
            {
                computed = await Task.Run(() =>
                {
                    // Private, unbound scratch copy - CoverArtService.Apply mutates whatever GameEntry
                    // it's given directly, and that must never be the live, UI-bound `game` from a
                    // background thread.
                    var scratch = new GameEntry
                    {
                        Id = game.Id, Name = game.Name, CatalogName = game.CatalogName, ExecutablePath = game.ExecutablePath,
                        InstallDir = game.InstallDir, Source = game.Source, LaunchUri = game.LaunchUri,
                    };
                    var automatic = applyCoverArt(scratch, apiKey);
                    return (scratch.Icon, scratch.IsCoverArt, automatic);
                }, ct);
            }
            catch (Exception ex)
            {
                // Never let a network/provider failure here surface as a Reset failure - the reset
                // itself already succeeded and is durably saved; this lookup is a best-effort
                // improvement layered on top of it.
                Logger.Warn($"'{game.Name}': automatic cover lookup after Reset failed unexpectedly.", ex);
                return outcome;
            }

            CommitAutomaticArtworkIfCurrent(gameId, asOfRevision, computed.Icon, computed.IsCoverArt, computed.Automatic);
            return outcome;
        }
        finally
        {
            _artworkOperationsInFlight.Remove(gameId);
        }
    }

    // Test-only seams for the two UI commands below - production always leaves both null, in which case
    // ChangeCoverAsync resolves to the real OpenFileDialog and the real ChangeCoverDialog. Split into two
    // separate delegates (rather than one seam standing in for "the whole command") so a test can
    // exercise each stage's own cancellation path independently - picker-cancelled vs preview-cancelled
    // are different user actions with the same required outcome (nothing changes), and collapsing them
    // into one seam couldn't tell those two tests apart.
    internal Func<string, string?>? ChangeCoverFilePickerForTest { get; set; }

    internal Func<string, BitmapImage, bool>? ChangeCoverPreviewDialogForTest { get; set; }

    /// <summary>Change Cover's UI entry point. `game` is read only for its Id/Name, captured into local
    /// variables BEFORE any await - a refresh can replace this exact GameEntry instance while the file
    /// dialog or preview window is open (both block on real user input for however long the user takes),
    /// so nothing past this point may dereference `game` again.
    ///
    /// Picking a file only VALIDATES and PREVIEWS it (ValidateLocalCoverImageAsync touches nothing on
    /// disk or in settings) - the existing cover and settings are only ever touched by
    /// ApplyValidatedCoverImageAsync, reached below if and only if the preview dialog's own Apply button
    /// was clicked. Closing the file picker, or closing/cancelling the preview dialog, returns out of
    /// this method with nothing changed either way.
    ///
    /// Wrapped in a single try/catch: CommunityToolkit's generated async commands otherwise let an
    /// unhandled exception propagate back onto the UI thread's SynchronizationContext, which this app's
    /// dispatcher-level handler treats as fatal and shuts down on - turning a recoverable failure (a
    /// locked file, a picker COM hiccup) into a full crash. Every awaited/called step here already
    /// handles its OWN expected failure modes and returns an ArtworkChangeOutcome instead of throwing;
    /// this catch is strictly a last-resort net for whatever gets past that, logged and surfaced as a
    /// StatusText message instead.</summary>
    [RelayCommand]
    private async Task ChangeCoverAsync(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        var gameName = game.Name;

        try
        {
            var pickFile = ChangeCoverFilePickerForTest ?? PickCoverFileFromDisk;
            var path = pickFile(gameName);
            if (path is null)
                return; // picker cancelled/closed - nothing was ever touched

            var validated = await ValidateLocalCoverImageAsync(path);
            if (validated is null)
            {
                StatusText = $"That file couldn't be used as {gameName}'s cover - check its format and dimensions.";
                return;
            }

            var showPreview = ChangeCoverPreviewDialogForTest ?? ShowChangeCoverPreviewDialog;
            var applied = showPreview(gameName, validated.DecodedImage);
            if (!applied)
                return; // preview cancelled/closed - the validated bytes are simply discarded, nothing staged or persisted

            var outcome = await ApplyValidatedCoverImageAsync(gameId, validated);
            StatusText = outcome switch
            {
                ArtworkChangeOutcome.Success => $"Cover updated for {gameName}.",
                ArtworkChangeOutcome.AlreadyInProgress => $"Already updating {gameName}'s cover - try again in a moment.",
                ArtworkChangeOutcome.StorageFailed => "Couldn't save that image to disk.",
                ArtworkChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only.",
                ArtworkChangeOutcome.GameNoLongerExists => $"{gameName} is no longer in your library.",
                ArtworkChangeOutcome.RevisionExhausted => $"{gameName}'s cover has been changed too many times to update again.",
                _ => StatusText,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Change Cover failed unexpectedly for '{gameName}'.", ex);
            StatusText = $"Couldn't change the cover for {gameName} - see the log for details.";
        }
    }

    private static string? PickCoverFileFromDisk(string gameName)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a cover image for {gameName}",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Shows the local-only Preview -> Apply/Cancel dialog and blocks (ShowDialog, same as
    /// the other dialogs) until the user picks one. Returns whether Apply was clicked - the
    /// dialog itself never touches settings or disk; see ChangeCoverDialogViewModel.</summary>
    private static bool ShowChangeCoverPreviewDialog(string gameName, BitmapImage previewImage)
    {
        var dialogViewModel = new ChangeCoverDialogViewModel(gameName, previewImage);
        AppShell.ShowModal("Change Cover", new ChangeCoverDialog(dialogViewModel));
        return dialogViewModel.Applied;
    }

    /// <summary>Reset's UI entry point - see ChangeCoverAsync's remarks on why `game` is only ever read
    /// for its Id/Name up front, and on why the whole body is wrapped in a single catch-all.</summary>
    [RelayCommand]
    private async Task ResetCoverAsync(GameEntry? game)
    {
        if (game is null)
            return;

        var gameId = game.Id;
        var gameName = game.Name;

        try
        {
            var outcome = await ResetCoverToAutomaticAsync(gameId);
            StatusText = outcome switch
            {
                ArtworkChangeOutcome.Success => $"Cover reset to automatic for {gameName}.",
                ArtworkChangeOutcome.AlreadyInProgress => $"Already updating {gameName}'s cover - try again in a moment.",
                ArtworkChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only.",
                ArtworkChangeOutcome.GameNoLongerExists => $"{gameName} is no longer in your library.",
                ArtworkChangeOutcome.RevisionExhausted => $"{gameName}'s cover has been changed too many times to reset again.",
                _ => StatusText,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Reset Cover failed unexpectedly for '{gameName}'.", ex);
            StatusText = $"Couldn't reset the cover for {gameName} - see the log for details.";
        }
    }

    /// <summary>The one place Change Cover/Reset actually mutate live settings - fully synchronous (no
    /// await anywhere in this method's body), which is what lets WPF's single UI-thread dispatcher
    /// serialize it against every other call to this same method for a DIFFERENT game, without needing a
    /// separate lock: there is no yield point inside this method at which the dispatcher could
    /// interleave another commit. A per-game guard (_artworkOperationsInFlight) alone would not protect
    /// the shared settings.json if two DIFFERENT games' commits could genuinely interleave - this is
    /// what actually prevents that.
    ///
    /// Re-resolves `gameId` against the CURRENT _allGames rather than trusting any reference a caller
    /// might have held across its own earlier await - a refresh can replace or merge the target game
    /// while an async prepare phase (image validation, asset write) was running. If the id no longer
    /// exists at all, this aborts WITHOUT creating/resurrecting an override for a dead id.
    ///
    /// `preparedIcon` lets a caller that already decoded and froze the exact image being committed
    /// (ApplyLocalCoverImageAsync's own full-decode validation step - see ArtworkImageValidator.
    /// ValidatedImage.DecodedImage) hand it straight to the display, instead of this method re-reading
    /// the just-written asset file and re-decoding it a second time on the UI thread. Only meaningful
    /// together with a `mutate` that sets a NEW Artwork selection matching those exact bytes - Reset's
    /// own `mutate` clears Artwork instead and never passes this.
    ///
    /// The revision check runs FIRST, before `existing`/`over` are touched at all - see
    /// TryGetNextRevision's own remarks for why a caller must reject an exhausted mutation before
    /// changing or removing anything, not fall back to reusing a stale value afterward.
    ///
    /// `ct` is the caller's own cancellation (the Choose Cover dialog closing). It is checked HERE, as the very first statement, so
    /// there is no await between the check and the mutation/save that follows: a cancellation that arrived during ANY earlier await
    /// (validation, download, asset staging) commits nothing and throws OperationCanceledException. A staged asset may stay on disk
    /// under the deferred-cleanup policy; that is never a reason to save a selection the user walked away from.</summary>
    private ArtworkChangeOutcome CommitArtworkChange(string gameId, Action<GameOverride> mutate, BitmapImage? preparedIcon = null,
        long? expectedRevision = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return ArtworkChangeOutcome.GameNoLongerExists;

        _settings.Overrides.TryGetValue(gameId, out var existing);
        var previousRevision = existing?.ArtworkRevision ?? 0;

        // Re-checked HERE, synchronously, at the commit: the asset write above is async, so a newer change may have landed.
        if (expectedRevision is { } expected && expected != previousRevision)
            return ArtworkChangeOutcome.StaleSelection;

        if (!TryGetNextRevision(previousRevision, out var nextRevision))
        {
            Logger.Error($"'{game.Name}': artwork revision counter exhausted - rejecting this change rather than reusing a non-unique revision.");
            return ArtworkChangeOutcome.RevisionExhausted;
        }

        var hadNoOverride = existing is null;
        var previousArtwork = existing?.Artwork;

        var over = existing ?? new GameOverride();
        mutate(over);
        over.ArtworkRevision = nextRevision;
        if (hadNoOverride)
            _settings.Overrides[gameId] = over;

        if (_settingsService.Save(_settings))
        {
            if (over.Artwork is { } selection)
            {
                if (preparedIcon is not null)
                {
                    game.Icon = preparedIcon;
                    game.IsCoverArt = true;
                }
                else
                    CoverArtService.ApplyStoredSafely(game, selection, IconFallbackForTest, AssetStoreDirOverrideForTest);
            }
            else
                CoverArtService.ApplyIconFallbackSafely(game, IconFallbackForTest);

            return ArtworkChangeOutcome.Success;
        }

        // Settings failed to write - roll back exactly what was there before. Nothing durable changed,
        // so nothing displayed should change either; the newly staged asset file (if any) is simply left
        // orphaned on disk, same as every other deferred-cleanup case.
        if (hadNoOverride)
            _settings.Overrides.Remove(gameId);
        else
        {
            over.Artwork = previousArtwork;
            over.ArtworkRevision = previousRevision;
        }

        return ArtworkChangeOutcome.SaveFailed;
    }

    /// <summary>Commits an automatic lookup's result ONLY if the live ArtworkRevision still equals what
    /// was captured when the lookup started - the same optimistic-concurrency guard ApplyScanResultAsync's
    /// reconciliation uses, reused here for the identical reason: if a newer Change Cover, Reset, or
    /// scan already changed this game's artwork state while this lookup was running, the result is stale
    /// and must not overwrite something newer. Deliberately does NOT bump ArtworkRevision itself - this
    /// records what a CURRENT-revision lookup found, not a new user action; bumping would immediately
    /// invalidate the very check that just confirmed the result is current.
    ///
    /// Metadata is synced unconditionally once currency is confirmed - success OR no-match - mirroring
    /// ReconcileArtwork's own "current" branch. A real, confirmed gap in an earlier version only synced
    /// on success: if a CONCURRENT scan at this exact same revision published its own automatic metadata
    /// while this lookup was still running, and this lookup itself then found no match, the card would
    /// correctly fall back to the exe icon here but over.Artwork would still describe the scan's earlier
    /// (no-longer-displayed) match - a metadata/display mismatch. Clearing it here too keeps them in sync
    /// regardless of which of the two concurrent writers happens to finish first.</summary>
    private void CommitAutomaticArtworkIfCurrent(string gameId, long asOfRevision, BitmapImage? icon, bool isCoverArt, ArtworkSelection? automatic)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == gameId);
        if (game is null)
            return;

        _settings.Overrides.TryGetValue(gameId, out var over);
        if ((over?.ArtworkRevision ?? 0) != asOfRevision)
            return; // stale - something else already changed this game's artwork state

        game.Icon = icon;
        game.IsCoverArt = isCoverArt;

        if (over is null)
        {
            if (automatic is null)
                return; // nothing to persist, and nothing was recorded before - no override needed at all
            over = new GameOverride { ArtworkRevision = asOfRevision };
            _settings.Overrides[gameId] = over;
        }

        over.Artwork = automatic; // replace or clear - always matches what's actually displayed above
        _settingsService.Save(_settings); // best-effort - game.Icon is already correct regardless of whether this persists
    }
}
