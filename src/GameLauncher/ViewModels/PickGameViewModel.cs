using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>The "Pick a game for me" dialog: one suggestion from the games currently on screen, with a way to ask for another. Holds no
/// launching logic - the library plays the game only when the dialog closed with Play.</summary>
public sealed partial class PickGameViewModel : ObservableObject
{
    private readonly IReadOnlyList<GameEntry> _pool;
    private readonly Func<int, int> _nextIndex;

    /// <param name="pool">The games to choose from; must not be empty.</param>
    /// <param name="nextIndex">Returns a random index below its argument (a seam so tests choose deterministically).</param>
    public PickGameViewModel(IReadOnlyList<GameEntry> pool, Func<int, int> nextIndex)
    {
        _pool = pool;
        _nextIndex = nextIndex;
        _game = Choose(null);
    }

    [ObservableProperty]
    private GameEntry _game;

    public string PoolText => _pool.Count == 1 ? "The only game in this view" : $"One of {_pool.Count} games in this view";

    /// <summary>Set by the window's Play button; a dialog that is closed any other way launches nothing.</summary>
    public bool PlayRequested { get; set; }

    private bool CanPickAnother() => _pool.Count > 1;

    /// <summary>Another suggestion, never the one already showing (while there is more than one game to choose from).</summary>
    [RelayCommand(CanExecute = nameof(CanPickAnother))]
    private void PickAnother() => Game = Choose(Game);

    private GameEntry Choose(GameEntry? avoid)
    {
        var candidates = avoid is null || _pool.Count < 2 ? _pool : _pool.Where(g => !ReferenceEquals(g, avoid)).ToList();
        return candidates[Math.Clamp(_nextIndex(candidates.Count), 0, candidates.Count - 1)];
    }
}
