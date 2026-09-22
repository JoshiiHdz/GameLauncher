using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Services.CoverArt;
using GameLauncher.Services.Identity;

namespace GameLauncher.ViewModels;

/// <summary>One search result shown in the picker.</summary>
public sealed partial class CandidateItem : ObservableObject
{
    public CandidateItem(CatalogCandidate candidate, string providerName)
    {
        Candidate = candidate;
        ProviderName = providerName;
    }

    public CatalogCandidate Candidate { get; }
    public string ProviderName { get; }
    public string Title => Candidate.Title;
    public string Subtitle => string.IsNullOrWhiteSpace(Candidate.Detail) ? ProviderName : $"{ProviderName} · {Candidate.Detail}";

    [ObservableProperty]
    private BitmapImage? _thumbnail;
}

/// <summary>One cover image a catalog entry offers, shown in Choose Cover.</summary>
public sealed partial class CoverItem : ObservableObject
{
    public CoverItem(ICatalogProvider provider, ActiveEntry entry, CoverChoice choice)
    {
        Provider = provider;
        Entry = entry;
        Choice = choice;
    }

    public ICatalogProvider Provider { get; }
    public ActiveEntry Entry { get; }
    public CoverChoice Choice { get; }
    public string Caption => $"{Provider.DisplayName} · {Entry.Title}";

    [ObservableProperty]
    private BitmapImage? _thumbnail;
}

/// <summary>Backs the Identify Game / Choose Cover window. It holds no game reference, only ids and the state it captured
/// when it OPENED: (IdentityRevision, DecisionRevision, ArtworkRevision). Every operation goes through LibraryViewModel,
/// which re-verifies those revisions against the live state - so an automatic resolution, another dialog, or a merge that
/// landed while this window was open produces StaleSelection (with an honest message) instead of silently overwriting.
///
/// "Identify game" and "Choose cover" are TWO commands composing two independent transactions: picking an IMAGE never
/// confirms an identity, and confirming an identity never replaces a pinned cover.
///
/// Searching uses a per-dialog generation token plus a cancellable source, so the results of a superseded query are
/// discarded and their HTTP work cancelled.</summary>
public sealed partial class IdentifyGameViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly IReadOnlyList<ICatalogProvider> _providers;
    private LibraryViewModel.IdentityDialogState _opened;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private CancellationTokenSource? _coversCts;
    private int _coversGeneration;
    private bool _closed;

    private sealed record SearchOutcome(ICatalogProvider Provider, IReadOnlyList<CatalogCandidate> Results, string? Error);

    /// <summary>Where provider work runs. Default: the thread pool. A test substitutes a scheduler it controls, so it can close the
    /// window at the exact moment work is queued but has not started.</summary>
    internal TaskScheduler WorkScheduler { get; set; } = TaskScheduler.Default;

    /// <summary>Runs `work` on WorkScheduler. Deliberately given NO cancellation token: a task cancelled before it starts never runs
    /// its delegate, so its own handling would be bypassed and Task.WhenAll would throw. The delegate observes the token itself.</summary>
    private Task<T> RunWork<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.DenyChildAttach, WorkScheduler);

    public IdentifyGameViewModel(LibraryViewModel library, IReadOnlyList<ICatalogProvider> providers, string gameId, string gameName)
    {
        _library = library;
        _providers = providers;
        GameId = gameId;
        GameName = gameName;
        _opened = library.GetIdentityDialogState(gameId)
            ?? throw new InvalidOperationException($"Game '{gameId}' is not in the library.");
        SearchText = _opened.Query.SearchTitle;
        Refresh(_opened);
    }

    public string GameId { get; }
    public string GameName { get; }
    public string DetectedTitle => _opened.DetectedTitle;
    public bool HasProviders => _providers.Count > 0;

    public ObservableCollection<CandidateItem> Candidates { get; } = new();
    public ObservableCollection<CoverItem> Covers { get; } = new();

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canClear;
    [ObservableProperty] private bool _hasPinnedCover;
    [ObservableProperty] private CandidateItem? _selectedCandidate;
    [ObservableProperty] private CoverItem? _selectedCover;

    /// <summary>What the dialog is showing about the game right now (a copy captured on open / after each operation).</summary>
    internal LibraryViewModel.IdentityDialogState Opened => _opened;

    private void Refresh(LibraryViewModel.IdentityDialogState state)
    {
        _opened = state;
        StateText = Describe(state);
        CanClear = state.Record is { IsQuarantined: true } || state.Record?.Confirmed is not null
            || state.Record?.Rejected.Count > 0 || state.Record?.ArtworkSources.Count > 0;
        HasPinnedCover = _library.GetOverride(GameId)?.Artwork is { IsUserSelected: true };
        OnPropertyChanged(nameof(DetectedTitle));
    }

    private static string ProviderOf(IdentifierNamespace ns) =>
        ns == IdentifierNamespace.IgdbGame ? "IGDB" : ns == IdentifierNamespace.SteamGridDbGame ? "SteamGridDB" : ns.Value;

    /// <summary>The state in words a user can act on - every unresolved reason says WHY, never just "unknown".</summary>
    internal static string Describe(LibraryViewModel.IdentityDialogState s)
    {
        switch (s.State)
        {
            case IdentityState.UserConfirmed:
                var confirmed = s.Record!.Confirmed!;
                return $"You confirmed this game as “{confirmed.Title}” ({ProviderOf(confirmed.Namespace)}).";
            case IdentityState.AutoResolved:
                var primary = s.Active.Primary!;
                return $"Identified automatically as “{primary.Title}” ({ProviderOf(primary.Namespace)}).";
            case IdentityState.Detected:
                return "Detected, but not identified yet.";
        }

        return s.UnresolvedReason switch
        {
            "NoMatch" => "No confident match was found for this title.",
            "Ambiguous" => "Several games share this name (or it is a multi-title launcher entry), so it was not guessed.",
            "Contradicted" => "The only match disagreed with the launcher's own id, so it was not accepted.",
            "Unavailable" => "The catalog could not be reached; this will be retried on the next scan.",
            "NotConfigured" => "No catalog is configured. Add IGDB credentials or a SteamGridDB key in Settings.",
            "PendingRevalidation" => "Your existing cover is kept while this game is re-verified against the catalog.",
            "Quarantined" => "This game's identity data could not be read. It is preserved untouched; Clear identity starts fresh.",
            _ => "Not identified yet.",
        };
    }

    // ---- Identify game ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_closed)
            return;

        var text = SearchText?.Trim() ?? "";
        _searchCts?.Cancel();
        if (text.Length == 0)
        {
            Candidates.Clear();
            StatusMessage = "Type a title to search.";
            return;
        }

        if (_providers.Count == 0)
        {
            StatusMessage = "No catalog is configured. Add IGDB credentials or a SteamGridDB key in Settings.";
            return;
        }

        var generation = ++_searchGeneration;
        var cts = _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = cts.Token;
        IsSearching = true;
        StatusMessage = "Searching...";

        try
        {
            var searches = _providers.Select(p => RunWork(() =>
            {
                if (ct.IsCancellationRequested)
                    return new SearchOutcome(p, Array.Empty<CatalogCandidate>(), null);

                try
                {
                    return new SearchOutcome(p, p.SearchCandidates(text, ct), null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new SearchOutcome(p, Array.Empty<CatalogCandidate>(), null);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Identify Game: " + p.DisplayName + " search for '" + text + "' failed.", ex);
                    return new SearchOutcome(p, Array.Empty<CatalogCandidate>(), p.DisplayName + " could not be searched");
                }
            })).ToList();

            var completed = await Task.WhenAll(searches);
            if (generation != _searchGeneration || ct.IsCancellationRequested)
                return; // superseded by a newer search, or the window closed: these results are discarded, never shown

            Candidates.Clear();
            foreach (var outcome in completed)
            {
                foreach (var candidate in outcome.Results)
                    Candidates.Add(new CandidateItem(candidate, outcome.Provider.DisplayName));
            }

            var errors = completed.Where(c => c.Error is not null).Select(c => c.Error!).ToList();
            StatusMessage = Candidates.Count == 0
                ? (errors.Count > 0 ? string.Join("; ", errors) + "." : "No results. Try a different spelling.")
                : (errors.Count > 0 ? $"{Candidates.Count} result(s). {string.Join("; ", errors)}." : $"{Candidates.Count} result(s).");

            _ = LoadThumbnailsAsync(generation, ct);
        }
        catch (OperationCanceledException)
        {
            // closed (or superseded) while the work was queued or running: nothing to show, nothing to report
        }
        finally
        {
            if (generation == _searchGeneration || _closed)
                IsSearching = false;
        }
    }

    private async Task LoadThumbnailsAsync(int generation, CancellationToken ct)
    {
        foreach (var item in Candidates.Take(10).ToList())
        {
            var provider = _providers.FirstOrDefault(p => p.Namespace == item.Candidate.Namespace);
            if (provider is null)
                continue;

            try
            {
                var image = await RunWork(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    // IGDB's search response already carries a cover for free (ParseCandidates reads it), so
                    // this is normally just a download. SteamGridDB's autocomplete endpoint returns no image
                    // at all - without this fallback its candidates NEVER show a thumbnail, only the name and
                    // the placeholder icon, no matter how long you wait. ListCovers is the same per-id lookup
                    // Choose Cover already uses; it's one extra bounded call, only for the up to 10 candidates
                    // actually shown, and only for a provider whose search didn't already include an image.
                    var url = item.Candidate.ThumbnailUrl;
                    if (url is null)
                    {
                        var firstCover = provider.ListCovers(item.Candidate.Id, ct).FirstOrDefault();
                        url = firstCover is null ? null : firstCover.ThumbnailUrl ?? firstCover.ImageUrl;
                    }
                    if (url is null)
                        return null;

                    var bytes = provider.DownloadImage(url, ct);
                    return bytes is null ? null : ArtworkImageValidator.ValidateProviderBytes(bytes, url);
                });
                if (generation == _searchGeneration && !ct.IsCancellationRequested && image is not null)
                    item.Thumbnail = image;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn("Identify Game: a thumbnail could not be loaded.", ex);
            }
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_closed)
            return;

        if (SelectedCandidate is not { } item)
        {
            StatusMessage = "Select a game in the list first.";
            return;
        }

        await RunIdentityOperationAsync(async () =>
        {
            var outcome = await _library.ConfirmIdentityAsync(GameId, item.Candidate, _opened.DecisionRevision, _opened.IdentityRevision, _lifetime.Token);
            return (outcome, outcome == IdentityChangeOutcome.Success
                ? $"Identity set to “{item.Title}” ({item.ProviderName})." + (PinnedNote())
                : null);
        });
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (_closed)
            return;

        // "Not this game": the selected candidate, or - with nothing selected - the game it was automatically identified as.
        IdentityKey? key;
        string title;
        if (SelectedCandidate is { } item)
        {
            (key, title) = (new IdentityKey(item.Candidate.Namespace, item.Candidate.Id), item.Title);
        }
        else if (_opened.State == IdentityState.AutoResolved && _opened.Active.Primary is { } primary)
        {
            (key, title) = (new IdentityKey(primary.Namespace, primary.Id), primary.Title);
        }
        else
        {
            StatusMessage = "Select the game that is NOT this one, or use Clear identity.";
            return;
        }

        await RunIdentityOperationAsync(() =>
        {
            var outcome = _library.RejectIdentityCandidate(GameId, key, title, _opened.DecisionRevision, _opened.IdentityRevision);
            return Task.FromResult((outcome, outcome == IdentityChangeOutcome.Success
                ? $"“{title}” will not be picked automatically for this game again." + PinnedNote()
                : (string?)null));
        });
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (_closed)
            return;

        await RunIdentityOperationAsync(async () =>
        {
            var outcome = await _library.ClearIdentityAsync(GameId, _opened.DecisionRevision, _opened.IdentityRevision, _lifetime.Token);
            return (outcome, outcome == IdentityChangeOutcome.Success ? "Back to automatic identification." + PinnedNote() : null);
        });
    }

    private string PinnedNote() => HasPinnedCover ? " Your custom cover was kept." : "";

    private async Task RunIdentityOperationAsync(Func<Task<(IdentityChangeOutcome Outcome, string? SuccessMessage)>> operation)
    {
        IsBusy = true;
        try
        {
            var (outcome, success) = await operation();
            var now = _library.GetIdentityDialogState(GameId);
            var staleNow = outcome == IdentityChangeOutcome.StaleSelection && now is not null ? Describe(now) : null;
            if (now is not null)
                Refresh(now); // always show the LIVE state afterwards

            StatusMessage = outcome switch
            {
                IdentityChangeOutcome.Success => success ?? "Done.",
                IdentityChangeOutcome.NoChange => "Nothing changed: that was already the case.",
                IdentityChangeOutcome.StaleSelection =>
                    $"This game changed while this window was open. Now: {staleNow} Nothing was applied - review and try again.",
                IdentityChangeOutcome.GameNoLongerExists => "This game is no longer in your library.",
                IdentityChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only. Nothing was changed.",
                IdentityChangeOutcome.RevisionExhausted => "This game's identity has been changed too many times to change again.",
                IdentityChangeOutcome.InvalidOperation => "That isn't allowed for this game right now (use Clear identity to start over).",
                _ => "Couldn't complete that.",
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // the window closed while the follow-up lookup ran; the change itself was already committed (or was never made)
        }
        catch (Exception ex)
        {
            Logger.Error($"Identify Game failed unexpectedly for '{GameName}'.", ex);
            StatusMessage = "Something went wrong - see the log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Choose cover -------------------------------------------------------------------------------------------------

    /// <summary>Lists the covers the game's ACTIVE identity (and its artwork sources) offers - never a title guess.</summary>
    [RelayCommand]
    private async Task LoadCoversAsync()
    {
        if (_closed)
            return;

        _coversCts?.Cancel();
        var generation = ++_coversGeneration;
        var cts = _coversCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = cts.Token;
        Covers.Clear();
        var entries = _opened.Active.Entries.Where(e => e.ArtworkEligible).ToList();
        if (entries.Count == 0)
        {
            StatusMessage = "Identify this game first - covers are listed for the game it is identified as.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Loading covers...";
        var errors = new List<string>();
        try
        {
            foreach (var entry in entries)
            {
                var provider = _providers.FirstOrDefault(p => p.Namespace == entry.Namespace);
                if (provider is null)
                    continue; // that catalog isn't configured here

                try
                {
                    var choices = await RunWork(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return provider.ListCovers(entry.Id, ct);
                    });
                    if (generation != _coversGeneration || ct.IsCancellationRequested)
                        return;

                    foreach (var choice in choices)
                        Covers.Add(new CoverItem(provider, entry, choice));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Choose Cover: {provider.DisplayName} could not list covers.", ex);
                    errors.Add($"{provider.DisplayName} could not be reached");
                }
            }

            StatusMessage = Covers.Count > 0
                ? $"{Covers.Count} cover(s). Pick one to use it as a custom cover."
                : (errors.Count > 0 ? string.Join("; ", errors) + "." : "No covers are listed for this game.");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (generation == _coversGeneration || _closed)
                IsBusy = false;
        }

        _ = LoadCoverThumbnailsAsync(ct);
    }

    private async Task LoadCoverThumbnailsAsync(CancellationToken ct)
    {
        foreach (var item in Covers.Take(12).ToList())
        {
            if (item.Choice.ThumbnailUrl is not { } url)
                continue;

            try
            {
                var image = await RunWork(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = item.Provider.DownloadImage(url, ct);
                    return bytes is null ? null : ArtworkImageValidator.ValidateProviderBytes(bytes, url);
                });
                if (!ct.IsCancellationRequested && image is not null)
                    item.Thumbnail = image;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn("Choose Cover: a thumbnail could not be loaded.", ex);
            }
        }
    }

    /// <summary>Downloads the selected cover, validates it like any user-supplied image (a catalog can serve WebP, which cannot be
    /// staged as a custom cover - that is reported plainly), and pins it. Picking an image records provenance only: it does
    /// NOT confirm an identity.</summary>
    [RelayCommand]
    private async Task ApplyCoverAsync()
    {
        if (_closed)
            return;

        if (SelectedCover is not { } item)
        {
            StatusMessage = "Select a cover first.";
            return;
        }

        var ct = _lifetime.Token;
        IsBusy = true;
        try
        {
            var validated = await RunWork(() =>
            {
                ct.ThrowIfCancellationRequested();
                var bytes = item.Provider.DownloadImage(item.Choice.ImageUrl, ct);
                return bytes is null ? null : ArtworkImageValidator.ValidateBytes(bytes, item.Choice.ImageUrl);
            });

            // The window closed while the image downloaded: the user walked away, so nothing is saved.
            if (ct.IsCancellationRequested)
                return;

            if (validated is null)
            {
                StatusMessage = "That cover couldn't be used (it couldn't be downloaded, or it is in a format or size a custom cover can't be).";
                return;
            }

            var provenance = new ArtworkSelection
            {
                Provider = item.Provider.Namespace == IdentifierNamespace.IgdbGame ? ArtworkProvider.Igdb : ArtworkProvider.SteamGridDb,
                ProviderGameId = item.Entry.Id,
                ProviderArtworkRef = item.Choice.Ref,
                ProviderTitle = item.Entry.Title,
            };
            var outcome = await _library.ApplyCatalogCoverAsync(GameId, validated, provenance, _opened.ArtworkRevision, ct);

            var now = _library.GetIdentityDialogState(GameId);
            if (now is not null)
                Refresh(now);

            StatusMessage = outcome switch
            {
                ArtworkChangeOutcome.Success => "Custom cover applied. It will be kept whatever this game is identified as.",
                ArtworkChangeOutcome.StaleSelection => "This game's cover changed while you were choosing. Nothing was replaced - choose again to replace it.",
                ArtworkChangeOutcome.AlreadyInProgress => "A cover change is already in progress for this game - try again in a moment.",
                ArtworkChangeOutcome.StorageFailed => "Couldn't save that image to disk.",
                ArtworkChangeOutcome.SaveFailed => "Couldn't save the change - your settings file may be locked or read-only.",
                ArtworkChangeOutcome.GameNoLongerExists => "This game is no longer in your library.",
                ArtworkChangeOutcome.RevisionExhausted => "This game's cover has been changed too many times to update again.",
                _ => "Couldn't apply that cover.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // closed while downloading: nothing was committed
        }
        catch (Exception ex)
        {
            Logger.Error($"Choose Cover failed unexpectedly for '{GameName}'.", ex);
            StatusMessage = "Something went wrong - see the log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Called when the window closes: stops every in-flight search, listing, download and follow-up lookup, and makes any
    /// command that fires afterwards a no-op.</summary>
    public void Cancel()
    {
        _closed = true;
        _searchGeneration++;
        _coversGeneration++;
        _lifetime.Cancel();
    }
}
