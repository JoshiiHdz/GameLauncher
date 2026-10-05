using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The same game installed through two launchers: a note on each card, and a "Duplicates" view that lists them side by side.</summary>
public partial class LibraryViewModel
{
    [ObservableProperty]
    private bool _isDuplicatesViewSelected;

    /// <summary>True while any game is installed through two launchers - the sidebar's Duplicates entry only exists then.</summary>
    [ObservableProperty]
    private bool _hasDuplicates;

    /// <summary>How many games are part of a duplicate pair or group.</summary>
    [ObservableProperty]
    private int _duplicateGameCount;

    /// <summary>Re-marks every game. Cheap enough to run on each filter pass; properties only raise when their value really changes.
    /// Returns true when the Duplicates view had nothing left and was left, which re-runs ApplyFilter itself - so the caller must
    /// stop (the same contract as SyncCollections).</summary>
    private bool SyncDuplicates()
    {
        var groups = DuplicateDetector.Find(_allGames);
        var inGroup = new Dictionary<GameEntry, IReadOnlyList<GameEntry>>();
        foreach (var group in groups)
        {
            foreach (var game in group)
                inGroup[game] = group;
        }

        foreach (var game in _allGames)
        {
            var note = inGroup.TryGetValue(game, out var group) ? DuplicateDetector.NoteFor(game, group) : "";
            game.DuplicateNote = note;
            game.HasDuplicate = note.Length > 0;
        }

        DuplicateGameCount = inGroup.Count;
        HasDuplicates = inGroup.Count > 0;

        // Nothing left to show: leave the view rather than sit on an empty one with no way back in the sidebar.
        if (!HasDuplicates && SelectedView == LibraryView.Duplicates)
        {
            SelectedView = LibraryView.All;
            return true;
        }

        return false;
    }
}
