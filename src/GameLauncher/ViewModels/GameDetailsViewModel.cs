using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Converters;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>The game details page: cover, facts about the install and play time, collections, and the user's own notes. Notes are
/// edited here and saved by the library when the page closes, so nothing in this class touches settings.</summary>
public sealed partial class GameDetailsViewModel : ObservableObject, IDisposable
{
    internal const int MaxNotesLength = 4000;

    private readonly string _originalNotes;

    public GameDetailsViewModel(GameEntry game, string? notes)
    {
        Game = game;
        _originalNotes = notes ?? "";
        _notes = _originalNotes;
        game.PropertyChanged += OnGamePropertyChanged;
    }

    public GameEntry Game { get; }

    [ObservableProperty]
    private string _notes;

    public int NotesLimit => MaxNotesLength;

    public bool NotesChanged => !string.Equals(Notes, _originalNotes, StringComparison.Ordinal);

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(NotesChanged));

    /// <summary>Set by the window's Play button; a page that is closed any other way launches nothing.</summary>
    public bool PlayRequested { get; set; }

    /// <summary>Raised when the user asks to open the install folder; the library does the opening.</summary>
    public event Action? OpenInstallLocationRequested;

    /// <summary>Raised when the user asks for the Collections dialog; the library shows it and updates the game.</summary>
    public event Action? EditCollectionsRequested;

    /// <summary>Raised when the user asks to uninstall; the library hands it to the game's launcher.</summary>
    public event Action? UninstallRequested;

    public void RequestOpenInstallLocation() => OpenInstallLocationRequested?.Invoke();

    public void RequestUninstall() => UninstallRequested?.Invoke();

    public void RequestEditCollections() => EditCollectionsRequested?.Invoke();

    public string SourceText => GameSourceDisplayConverter.Name(Game.Source);

    public string SizeText => Game.HasInstallSize ? Game.InstallSizeDisplay : "Not measured yet";

    public string PlayTimeText => Game.HasPlayTime
        ? $"{PlayTimeFormat.Duration(Game.TotalPlaySeconds)} tracked, last played {Game.LastPlayedDisplay.ToLowerInvariant()}"
        : "Not played yet (only play seen by Axis Game Launcher is tracked)";

    public string AddedText => Game.DateAdded == default ? "Unknown" : Game.DateAdded.ToLocalTime().ToString("d MMM yyyy");

    public string CollectionsText => Game.Collections.Count == 0 ? "None" : string.Join(", ", Game.Collections);

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GameEntry.Collections):
                OnPropertyChanged(nameof(CollectionsText));
                break;
            case nameof(GameEntry.InstallSizeBytes):
            case nameof(GameEntry.InstallSizeDisplay):
                OnPropertyChanged(nameof(SizeText));
                break;
        }
    }

    public void Dispose() => Game.PropertyChanged -= OnGamePropertyChanged;
}
