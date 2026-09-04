using System.Globalization;
using System.Windows.Data;
using GameLauncher.Models;
using Wpf.Ui.Controls;

namespace GameLauncher.Converters;

/// <summary>
/// Picks the glyph shown when a platform badge has no extracted launcher icon (GameEntry.PlatformIcon
/// / the sidebar's per-source icon came back null). Xbox never reaches here - MainWindow.xaml's
/// PlatformBadgeVisibilityConverter.ForFallbackSymbol keeps this SymbolIcon collapsed for Xbox games,
/// showing the hardcoded Xbox logo (Assets\XboxLogo.png) in that slot instead, since its MSIX package
/// icon is ACL-locked and can never be extracted. A folder-scanned game with no detected launcher
/// (GameSource.Manual) gets a plain gamepad glyph. Everything else (Steam/Epic/GOG/EA whose launcher
/// just isn't installed on this PC) keeps the generic question-mark - that's a different, temporary
/// case, not a permanent one.
/// </summary>
public sealed class GameSourceToFallbackSymbolConverter : IValueConverter
{
    public static readonly GameSourceToFallbackSymbolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            GameSource.Manual => SymbolRegular.Games24,
            _ => SymbolRegular.Question24,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
