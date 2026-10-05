using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.Models;

/// <summary>
/// One launcher in the sidebar: the same on/off switch the view model exposes as DetectSteam,
/// DetectEpic, ... but as a uniform item, so the expanded sidebar (name + switch) and the collapsed
/// one (icon only) are two templates over one list instead of nine hand-copied rows each.
/// IsEnabled reads and writes straight through to the view model's own Detect property - nothing is
/// stored here - and the view model calls Refresh whenever those change, so the two never diverge.
/// </summary>
public sealed class SourceToggleItem : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private ImageSource? _colorIcon;
    private ImageSource? _greyIcon;
    private bool _iconsResolved;
    private bool _hasGames;

    public SourceToggleItem(GameSource source, string name, string letter, Func<bool> get, Action<bool> set)
    {
        Source = source;
        Name = name;
        Letter = letter;
        _get = get;
        _set = set;
    }

    public GameSource Source { get; }
    public string Name { get; }

    /// <summary>Fallback glyph when this PC has no extractable launcher icon.</summary>
    public string Letter { get; }

    /// <summary>Only launchers a scan actually found games for are listed - see LibraryViewModel.ApplyFilter.</summary>
    public bool HasGames => _hasGames;

    public bool IsEnabled
    {
        get => _get();
        set
        {
            if (_get() == value)
                return;
            _set(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayIcon));
        }
    }

    public ImageSource? ColorIcon
    {
        get { ResolveIcons(); return _colorIcon; }
    }

    public bool HasIcon => ColorIcon is not null;

    /// <summary>Games added by hand belong to no launcher, so there is no launcher icon to extract: the row draws a folder glyph.</summary>
    public bool UsesGlyph => Source == GameSource.Manual;

    /// <summary>The one-letter fallback badge: only for a launcher whose own icon could not be found.</summary>
    public bool ShowLetter => !HasIcon && !UsesGlyph;

    /// <summary>Full colour while the launcher is on, a grey copy while it is off - what the collapsed
    /// sidebar shows, since it has no switch to read the state from.</summary>
    public ImageSource? DisplayIcon
    {
        get
        {
            ResolveIcons();
            return IsEnabled ? _colorIcon : _greyIcon;
        }
    }

    public string ToolTip => IsEnabled ? $"{Name} - shown (click to hide)" : $"{Name} - hidden (click to show)";

    /// <summary>Re-reads state owned by the view model and tells the view.</summary>
    public void Refresh(bool hasGames)
    {
        if (_hasGames != hasGames)
        {
            _hasGames = hasGames;
            OnPropertyChanged(nameof(HasGames));
        }

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(DisplayIcon));
        OnPropertyChanged(nameof(ToolTip));
    }

    private void ResolveIcons()
    {
        if (_iconsResolved)
            return;
        _iconsResolved = true;

        // Xbox's own icon can never be extracted (ACL-locked MSIX package), so it always uses the
        // bundled logo - see PlatformIconService.
        BitmapSource? icon = Source == GameSource.Xbox ? LoadXboxLogo() : PlatformIconService.GetIcon(Source);
        if (icon is null)
            return;

        _colorIcon = icon;
        _greyIcon = GreyscaleIcon.From(icon);
    }

    private static BitmapSource? LoadXboxLogo()
    {
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/GameLauncher;component/Assets/XboxLogo.png", UriKind.Absolute));
            if (image.CanFreeze)
                image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }
}
