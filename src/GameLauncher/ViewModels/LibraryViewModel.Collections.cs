using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Collections: the user's own groups ("Co-op", "Backlog"). A game's membership is saved on its override and a collection
/// exists exactly while some game is in it, so the sidebar list is always derived - there is no separate list to fall out of step
/// when a game is removed, merged away, or uninstalled.</summary>
public partial class LibraryViewModel
{
    internal const int MaxCollectionNameLength = 40;

    /// <summary>Ready-made categories, always offered in the Collections dialog so nobody has to type them. They appear in the
    /// sidebar like any other collection - but only once a game is in them.</summary>
    internal static readonly string[] PresetCollections =
        ["Playing", "Backlog", "Finished", "Co-op", "Multiplayer", "Single-player", "Competitive", "Party"];

    public ObservableCollection<CollectionItem> Collections { get; } = new();

    [ObservableProperty]
    private bool _hasCollections;

    /// <summary>The collection narrowing the grid, or null. Clicking a collection toggles it; the rail's view buttons clear it.</summary>
    [ObservableProperty]
    private string? _selectedCollection;

    partial void OnSelectedCollectionChanged(string? value)
    {
        foreach (var item in Collections)
            item.IsSelected = string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase);
        ApplyFilter();
    }

    [RelayCommand]
    private void SelectCollection(string? name)
    {
        ClosePage();
        SelectedCollection = string.Equals(SelectedCollection, name, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>Trimmed, inner whitespace collapsed, capped; null when nothing is left. The one place a typed name becomes a stored one.</summary>
    internal static string? NormalizeCollectionName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var name = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length > MaxCollectionNameLength ? name[..MaxCollectionNameLength].TrimEnd() : name;
    }

    /// <summary>Union of two membership lists, case-insensitively, keeping the first spelling seen.</summary>
    internal static List<string> MergeCollectionNames(IEnumerable<string>? first, IEnumerable<string>? second) =>
        (first ?? []).Concat(second ?? []).Select(NormalizeCollectionName).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool InCollection(GameEntry game, string name) =>
        game.Collections.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every collection name in use, sorted.</summary>
    internal IReadOnlyList<string> AllCollectionNames() =>
        MergeCollectionNames(_allGames.SelectMany(g => g.Collections), null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What the "Collections..." dialog offers: the presets in their fixed order, then the user's own, sorted.</summary>
    internal IReadOnlyList<string> DialogCollectionNames() =>
        PresetCollections.Concat(AllCollectionNames().Where(n => !PresetCollections.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();

    /// <summary>Refreshes the sidebar list and counts from the games. Returns true if the selected collection had no games left and
    /// was cleared - which re-runs ApplyFilter itself, so the caller must stop.</summary>
    private bool SyncCollections()
    {
        var counts = _allGames.Where(g => !g.Hidden)
            .SelectMany(g => g.Collections.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();

        if (SelectedCollection is { } selected && counts.All(c => !string.Equals(c.Name, selected, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedCollection = null;
            return true;
        }

        var same = counts.Count == Collections.Count
            && counts.Zip(Collections).All(p => p.First.Name == p.Second.Name);
        if (same)
        {
            for (var i = 0; i < counts.Count; i++)
                Collections[i].Count = counts[i].Count;
        }
        else
        {
            Collections.Clear();
            foreach (var (name, count) in counts)
                Collections.Add(new CollectionItem(name, count) { IsSelected = string.Equals(name, SelectedCollection, StringComparison.OrdinalIgnoreCase) });
        }

        HasCollections = Collections.Count > 0;
        return false;
    }

    /// <summary>Replaces a game's collections with `names` (normalised, de-duplicated) and saves.</summary>
    internal void SetGameCollections(GameEntry game, IEnumerable<string> names)
    {
        // A typed name that differs only in case from an existing collection or a preset joins that one rather than starting a twin.
        var known = DialogCollectionNames();
        var cleaned = names.Select(NormalizeCollectionName).OfType<string>()
            .Select(n => known.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase)) ?? n)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!_settings.Overrides.TryGetValue(game.Id, out var over))
        {
            over = new GameOverride();
            _settings.Overrides[game.Id] = over;
        }

        over.Collections = cleaned;
        game.Collections = cleaned;
        _settingsService.Save(_settings);
        Logger.Info($"Collections: '{game.Name}' is now in [{string.Join(", ", cleaned)}].");
        ApplyFilter();
    }

    /// <summary>Removes a collection from every game; the games themselves are untouched.</summary>
    [RelayCommand]
    private void DeleteCollection(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        foreach (var over in _settings.Overrides.Values)
            over.Collections = over.Collections.Where(c => !string.Equals(c, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var game in _allGames)
            game.Collections = game.Collections.Where(c => !string.Equals(c, name, StringComparison.OrdinalIgnoreCase)).ToList();

        _settingsService.Save(_settings);
        Logger.Info($"Collections: deleted '{name}'.");
        StatusText = $"Deleted the collection \"{name}\" - its games are still in your library.";
        ApplyFilter();
    }

    /// <summary>Test seam: stands in for the dialog. Receives the dialog's view model and returns the chosen names, or null for Cancel.</summary>
    internal Func<CollectionsViewModel, IReadOnlyList<string>?>? CollectionsDialogForTest { get; set; }

    [RelayCommand]
    private void EditCollections(GameEntry? game)
    {
        if (game is null)
            return;

        var (gameId, gameName) = (game.Id, game.Name);
        try
        {
            var dialog = new CollectionsViewModel(gameName, DialogCollectionNames(), game.Collections,
                Collections.ToDictionary(c => c.Name, c => c.Count, StringComparer.OrdinalIgnoreCase));
            IReadOnlyList<string>? chosen;
            if (CollectionsDialogForTest is { } seam)
                chosen = seam(dialog);
            else
            {
                AppShell.ShowModal("Collections", new CollectionsDialog(dialog));
                chosen = dialog.Saved ? dialog.ChosenNames() : null;
            }

            // The game is looked up again by id: a refresh can replace the entry while the dialog is open.
            if (chosen is not null && _allGames.FirstOrDefault(g => g.Id == gameId) is { } current)
                SetGameCollections(current, chosen);
        }
        catch (Exception ex)
        {
            Logger.Error($"Collections dialog failed for '{gameName}'.", ex);
            StatusText = $"Couldn't open Collections for {gameName} - see the log for details.";
        }
    }
}
