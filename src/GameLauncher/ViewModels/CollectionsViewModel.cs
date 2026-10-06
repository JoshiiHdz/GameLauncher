using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameLauncher.ViewModels;

/// <summary>One tick-box row in the Collections dialog.</summary>
public sealed partial class CollectionChoice : ObservableObject
{
    public CollectionChoice(string name, bool isMember, int count = 0)
    {
        Name = name;
        _isMember = isMember;
        CountText = count == 1 ? "1 game" : $"{count} games";
    }

    public string Name { get; }

    /// <summary>"2 games", for the right-hand side of the row.</summary>
    public string CountText { get; }

    [ObservableProperty]
    private bool _isMember;
}

/// <summary>The "Collections..." dialog for one game: tick the collections it belongs to, or type a new one. Holds no
/// persistence - the library applies ChosenNames() only when the dialog was saved.</summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    public CollectionsViewModel(string gameName, IEnumerable<string> existing, IEnumerable<string> memberOf, IReadOnlyDictionary<string, int>? counts = null)
    {
        GameName = gameName;
        var member = new HashSet<string>(memberOf, StringComparer.OrdinalIgnoreCase);
        foreach (var name in existing)
            Choices.Add(new CollectionChoice(name, member.Contains(name), counts is not null && counts.TryGetValue(name, out var count) ? count : 0));
    }

    public string GameName { get; }

    public ObservableCollection<CollectionChoice> Choices { get; } = new();

    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNewCommand))]
    private string _newName = "";

    /// <summary>Set by the window's Save button; a closed-or-cancelled dialog leaves it false and changes nothing.</summary>
    public bool Saved { get; set; }

    private bool CanAddNew() => LibraryViewModel.NormalizeCollectionName(NewName) is not null;

    /// <summary>Adds the typed name as a ticked row (or ticks the existing one with the same name).</summary>
    [RelayCommand(CanExecute = nameof(CanAddNew))]
    private void AddNew()
    {
        var name = LibraryViewModel.NormalizeCollectionName(NewName);
        if (name is null)
            return;

        var existing = Choices.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            existing.IsMember = true;
        else
            Choices.Add(new CollectionChoice(name, true));

        NewName = "";
        OnPropertyChanged(nameof(HasChoices));
    }

    /// <summary>The names to save: every ticked row, plus a typed-but-not-yet-added name, so pressing Save straight after typing works.</summary>
    public IReadOnlyList<string> ChosenNames()
    {
        var names = Choices.Where(c => c.IsMember).Select(c => c.Name).ToList();
        if (LibraryViewModel.NormalizeCollectionName(NewName) is { } pending
            && !names.Contains(pending, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(pending);
        }

        return names;
    }
}
