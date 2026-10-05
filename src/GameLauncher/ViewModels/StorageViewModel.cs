using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>The "What's using this drive?" dialog for one drive. Install sizes arrive one by one while the library measures them in the
/// background, so this listens to the games and re-ranks as each lands; Dispose (the window closing) stops listening.</summary>
public sealed partial class StorageViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<GameEntry> _games;
    private readonly DriveSpaceInfo? _drive;

    public StorageViewModel(string driveLetter, DriveSpaceInfo? drive, IReadOnlyList<GameEntry> gamesOnDrive)
    {
        DriveLetter = driveLetter;
        _drive = drive;
        _games = gamesOnDrive;
        foreach (var game in _games)
            game.PropertyChanged += OnGamePropertyChanged;
        _snapshot = Build();
    }

    public string DriveLetter { get; }

    /// <summary>Raised when the user asks to open a game's install folder; the library does the opening.</summary>
    public event Action<GameEntry>? OpenInstallLocationRequested;

    public void RequestOpenInstallLocation(GameEntry game) => OpenInstallLocationRequested?.Invoke(game);

    public string Title => $"What's using {DriveLetter}";

    /// <summary>"120 GB used of 931 GB (811 GB free)" - or a plain note when the drive could not be read.</summary>
    public string DriveSummary => _drive?.SummaryText ?? "Drive details are unavailable right now.";

    public bool HasWarning => _drive?.HasWarning == true;

    public string WarningText => _drive?.WarningText ?? "";

    [ObservableProperty]
    private StorageSnapshot _snapshot;

    private StorageSnapshot Build() => StorageBreakdown.Build(_games,
        _drive is null ? null : _drive.TotalBytes - _drive.FreeBytes, _drive?.TotalBytes ?? 0);

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameEntry.InstallSizeBytes))
            Snapshot = Build();
    }

    public void Dispose()
    {
        foreach (var game in _games)
            game.PropertyChanged -= OnGamePropertyChanged;
    }
}
