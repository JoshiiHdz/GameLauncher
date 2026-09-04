using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GameLauncher.Models;

namespace GameLauncher.Converters;

/// <summary>
/// A game card's platform badge has three mutually-exclusive layers stacked in the same slot: the
/// launcher's own extracted icon (GameEntry.PlatformIcon), the hardcoded Xbox logo, or the generic
/// fallback glyph (GameSourceToFallbackSymbolConverter) - exactly one should ever be visible. Values
/// bound in: [0] = GameEntry.Source, [1] = GameEntry.PlatformIcon (null when no icon was extracted -
/// always null for Xbox, since its MSIX package icon can never be extracted at all).
/// </summary>
public sealed class PlatformBadgeVisibilityConverter : IMultiValueConverter
{
    public static readonly PlatformBadgeVisibilityConverter ForXboxLogo = new(showForXbox: true);
    public static readonly PlatformBadgeVisibilityConverter ForFallbackSymbol = new(showForXbox: false);

    private readonly bool _showForXbox;

    private PlatformBadgeVisibilityConverter(bool showForXbox) => _showForXbox = showForXbox;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasNoPlatformIcon = values.Length > 1 && values[1] is null;
        var isXbox = values.Length > 0 && values[0] is GameSource.Xbox;
        return hasNoPlatformIcon && isXbox == _showForXbox ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
