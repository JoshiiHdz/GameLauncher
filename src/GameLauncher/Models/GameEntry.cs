using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

public sealed partial class GameEntry : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string ExecutablePath { get; init; }
    public required string InstallDir { get; init; }
    public required GameSource Source { get; init; }

    public string? LaunchUri { get; init; }
    public BitmapImage? Icon { get; set; }

    /// <summary>True when Icon is real portrait box art (fills the card edge-to-edge); false when
    /// it's a fallback exe icon (small, centered, on a plate) - the UI renders these differently.</summary>
    public bool IsCoverArt { get; set; }

    public bool Hidden { get; set; }
    public DateTime DateAdded { get; set; }

    [ObservableProperty]
    private bool _favorite;
}
