using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>One row of the big-screen carousel ("Jump back in", "Favorites", "All games").</summary>
public sealed record BigShelf(string Name, IReadOnlyList<GameEntry> Games);

/// <summary>Big-screen mode: a full-screen, couch-distance view of the library driven by a controller or the arrow keys. Left/Right move
/// along a shelf, Up/Down (or the shoulder buttons) change shelf, A/Enter plays, Y/F favorites, B/Esc leaves. It only chooses - the library
/// launches the chosen game once the window has closed, the same way the command palette hands back its choice.</summary>
public sealed partial class BigScreenViewModel : ObservableObject
{
    private readonly IReadOnlyList<BigShelf> _shelves;
    private readonly int[] _positions;

    public BigScreenViewModel(IReadOnlyList<BigShelf> shelves)
    {
        _shelves = shelves;
        _positions = new int[shelves.Count];
        ShowShelf(0);
    }

    /// <summary>The games on the current shelf.</summary>
    public ObservableCollection<GameEntry> Games { get; } = new();

    [ObservableProperty]
    private int _shelfIndex;

    [ObservableProperty]
    private string _shelfName = "";

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private GameEntry? _selectedGame;

    [ObservableProperty]
    private string _shelfPositionText = "";

    public bool HasGames => Games.Count > 0;

    /// <summary>The game to play, set when the user accepts; null when they left without choosing.</summary>
    public GameEntry? Chosen { get; private set; }

    /// <summary>Raised when the window should close (a game was chosen, or the user backed out).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the user asks to favorite the highlighted game; the library does the toggling.</summary>
    public event Action<GameEntry>? FavoriteRequested;

    /// <summary>Which game is highlighted is remembered per shelf, so changing shelf and coming back lands where you were.</summary>
    partial void OnSelectedIndexChanged(int value)
    {
        if (_shelves.Count > 0 && value >= 0)
            _positions[ShelfIndex] = value;
        RefreshSelection();
    }

    /// <summary>Also run after a shelf change: two shelves can land on the same index, which raises no change of its own.</summary>
    private void RefreshSelection()
    {
        SelectedGame = SelectedIndex >= 0 && SelectedIndex < Games.Count ? Games[SelectedIndex] : null;
        ShelfPositionText = Games.Count == 0 ? "" : $"{SelectedIndex + 1} of {Games.Count}";
    }

    private void ShowShelf(int index)
    {
        Games.Clear();
        if (_shelves.Count == 0)
        {
            ShelfName = "";
            SelectedIndex = -1;
            OnPropertyChanged(nameof(HasGames));
            return;
        }

        ShelfIndex = index;
        ShelfName = _shelves[index].Name;
        foreach (var game in _shelves[index].Games)
            Games.Add(game);

        OnPropertyChanged(nameof(HasGames));
        SelectedIndex = Games.Count == 0 ? -1 : Math.Clamp(_positions[index], 0, Games.Count - 1);
        RefreshSelection();
    }

    /// <summary>Moves along the shelf, stopping at the ends (a wrap would hide where the shelf begins).</summary>
    public void Move(int delta)
    {
        if (Games.Count == 0)
            return;

        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Games.Count - 1);
    }

    /// <summary>Changes shelf, wrapping round.</summary>
    public void MoveShelf(int delta)
    {
        if (_shelves.Count < 2)
            return;

        ShowShelf(((ShelfIndex + delta) % _shelves.Count + _shelves.Count) % _shelves.Count);
    }

    public void Accept()
    {
        if (SelectedGame is null)
            return;

        Chosen = SelectedGame;
        CloseRequested?.Invoke();
    }

    public void Back() => CloseRequested?.Invoke();

    public void ToggleFavorite()
    {
        if (SelectedGame is { } game)
            FavoriteRequested?.Invoke(game);
    }

    /// <summary>Controller buttons map onto the same actions as the keys.</summary>
    public void Handle(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.Left: Move(-1); break;
            case GamepadButton.Right: Move(1); break;
            case GamepadButton.Up:
            case GamepadButton.PreviousShelf: MoveShelf(-1); break;
            case GamepadButton.Down:
            case GamepadButton.NextShelf: MoveShelf(1); break;
            case GamepadButton.Accept: Accept(); break;
            case GamepadButton.Back: Back(); break;
            case GamepadButton.Favorite: ToggleFavorite(); break;
        }
    }
}
