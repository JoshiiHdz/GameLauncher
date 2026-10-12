using System.ComponentModel;

namespace GameLauncher.Services;

/// <summary>Which theme is active, as something XAML can bind to from anywhere (<c>{Binding IsTile, Source={x:Static services:ThemeState.Instance}}</c>),
/// so a page or dialog can arrange itself per theme without knowing which window it is in. Updated by <see cref="ThemeManager"/>.</summary>
public sealed class ThemeState : INotifyPropertyChanged
{
    public static ThemeState Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThemeId Current { get; private set; } = ThemeId.Axis;

    public bool IsAxis => Current == ThemeId.Axis;

    public bool IsTile => Current == ThemeId.ConsoleTile;

    public bool IsRibbon => Current == ThemeId.ConsoleRibbon;

    public bool IsConsole => Current != ThemeId.Axis;

    /// <summary>Keyboard shortcuts, command keys, the Esc way back, the command palette's commands and the system-wide hotkey work in every theme: the themes differ in
    /// how they look and are driven, not in what they can do. (They once were Axis-only; a pad has none of them, and nothing in a controller layout depends on them.)</summary>
    public bool ShortcutsEnabled => true;

    // Text that names a key only where keys work.
    public string SearchHint => ShortcutsEnabled ? "Search your library...  (Ctrl+K for commands)" : "Search your games...";

    public string BackTooltip => ShortcutsEnabled ? "Back to your library (Esc)" : "Back to your library";

    public string CloseTooltip => ShortcutsEnabled ? "Close (Esc)" : "Close";

    public string MenuCloseTooltip => ShortcutsEnabled ? "Close menu (Esc)" : "Close menu";

    public string SearchTooltip => ShortcutsEnabled ? "Search (Ctrl+K)" : "Search";

    /// <summary>The window is in the small (760 x 480) size.</summary>
    public bool IsSmall { get; private set; }

    internal void UpdateSmall(bool small)
    {
        if (IsSmall == small)
            return;

        IsSmall = small;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSmall)));
    }

    internal void Update(ThemeId id)
    {
        if (Current == id)
            return;

        Current = id;
        foreach (var name in new[]
                 {
                     nameof(Current), nameof(IsAxis), nameof(IsTile), nameof(IsRibbon), nameof(IsConsole), nameof(ShortcutsEnabled), nameof(SearchHint), nameof(BackTooltip),
                     nameof(CloseTooltip), nameof(MenuCloseTooltip), nameof(SearchTooltip),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
