using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The Ctrl+K palette: type to find a game or a command, arrow keys to move, Enter to choose. It only picks - the chosen entry is
/// run by the library after the palette has closed, so what it opens (a dialog, a game) is never stuck behind it.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    internal const int MaxResults = 8;

    private readonly IReadOnlyList<PaletteItem> _items;

    public CommandPaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        _items = items;
        Refresh();
    }

    public ObservableCollection<PaletteItem> Results { get; } = new();

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>What was chosen, or null when the palette was dismissed.</summary>
    public PaletteItem? Chosen { get; private set; }

    /// <summary>Raised once something has been chosen, so the window can close itself.</summary>
    public event Action? CloseRequested;

    public bool HasNoResults => Results.Count == 0;

    public string EmptyText => string.IsNullOrWhiteSpace(Query) ? "Nothing to suggest yet - start typing." : "Nothing matches that.";

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Results.Clear();
        foreach (var item in PaletteSearch.Search(_items, Query, MaxResults))
            Results.Add(item);

        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Moves the highlight, wrapping at the ends.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
            return;

        SelectedIndex = ((Math.Max(SelectedIndex, 0) + delta) % Results.Count + Results.Count) % Results.Count;
    }

    /// <summary>Chooses the highlighted entry (or the one given, for a click) and asks the window to close.</summary>
    public void Choose(PaletteItem? item = null)
    {
        item ??= SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;
        if (item is null)
            return;

        Chosen = item;
        CloseRequested?.Invoke();
    }
}
