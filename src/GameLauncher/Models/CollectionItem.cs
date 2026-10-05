using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

/// <summary>One collection in the sidebar: its name, how many (non-hidden) games are in it, and whether it is the active filter.</summary>
public sealed partial class CollectionItem : ObservableObject
{
    public CollectionItem(string name, int count)
    {
        Name = name;
        _count = count;
    }

    public string Name { get; }

    /// <summary>The glyph shown when the sidebar is collapsed to its icon rail.</summary>
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public string ToolTip => $"{Name} - {Count} {(Count == 1 ? "game" : "games")}";
}
