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

    private readonly bool _originalRaisePriority;

    public GameDetailsViewModel(GameEntry game, string? notes, bool raisePriority = false)
    {
        Game = game;
        _originalRaisePriority = raisePriority;
        _raiseGamePriority = raisePriority;
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

    /// <summary>Focus play, for this game: run it at High priority while it is open. Saved by the library when the page closes, like the notes.</summary>
    [ObservableProperty]
    private bool _raiseGamePriority;

    public bool RaiseGamePriorityChanged => RaiseGamePriority != _originalRaisePriority;

    /// <summary>Set by the window's Play button; a page that is closed any other way launches nothing.</summary>
    public bool PlayRequested { get; set; }

    /// <summary>Raised when the user asks to open the install folder; the library does the opening.</summary>
    public event Action? OpenInstallLocationRequested;

    /// <summary>Raised when the user asks for the Collections dialog; the library shows it and updates the game.</summary>
    public event Action? EditCollectionsRequested;

    /// <summary>Raised when the user asks to uninstall; the library hands it to the game's launcher.</summary>
    public event Action? UninstallRequested;

    /// <summary>Raised when the user asks for the Play time page (today, the last 7 days and all time); the library opens it.</summary>
    public event Action? PlayTimeRequested;

    public void RequestPlayTime() => PlayTimeRequested?.Invoke();

    public void RequestOpenInstallLocation() => OpenInstallLocationRequested?.Invoke();

    public void RequestUninstall() => UninstallRequested?.Invoke();

    public void RequestEditCollections() => EditCollectionsRequested?.Invoke();

    public string SourceText => LauncherText.Name(Game.Source);

    /// <summary>"Steam · C: · 14 GB · 1.5 h tracked · Today": the facts that fit on one line under the title.</summary>
    public string MetaLine => Meta();

    /// <summary>The pieces of that line for the PlayStation layout, which shows the launcher as a chip and the drive with an icon: the play time, the drive letter, and
    /// (from <see cref="GameEntry.InstallSizeDisplay"/>) the size.</summary>
    public string PlayMetaText => Game.HasPlayTime ? $"{Game.PlayTimeDisplay} · {Game.LastPlayedDisplay}" : "Not played yet";

    public string DriveLetter => System.IO.Path.GetPathRoot(Game.InstallDir)?.TrimEnd('\\') ?? string.Empty;

    public bool HasDriveLetter => DriveLetter.Length > 0;

    private string Meta()
    {
        var parts = new List<string> { SourceText };

        var drive = System.IO.Path.GetPathRoot(Game.InstallDir)?.TrimEnd('\\');
        if (!string.IsNullOrEmpty(drive))
            parts.Add(drive);

        if (Game.HasInstallSize)
            parts.Add(Game.InstallSizeDisplay);

        parts.Add(Game.HasPlayTime ? $"{Game.PlayTimeDisplay} · {Game.LastPlayedDisplay}" : "Not played yet");
        return string.Join(" · ", parts);
    }

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
            case nameof(GameEntry.HasPlayTime):
            case nameof(GameEntry.PlayTimeDisplay):
            case nameof(GameEntry.LastPlayedDisplay):
                OnPropertyChanged(nameof(PlayMetaText));
                break;
            case nameof(GameEntry.Collections):
                OnPropertyChanged(nameof(CollectionsText));
                break;
            case nameof(GameEntry.InstallSizeBytes):
            case nameof(GameEntry.InstallSizeDisplay):
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(MetaLine));
                break;
        }
    }

    public void Dispose() => Game.PropertyChanged -= OnGamePropertyChanged;
}
