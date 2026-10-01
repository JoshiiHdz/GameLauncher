using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Menu text for the card's Favorite toggle - same command either way
/// (LibraryViewModel.ToggleFavoriteCommand just flips the bool), only the label changes. Mirrors
/// HiddenToTooltipConverter's shape; this one needs its own converter rather than reusing
/// BoolToStarConverter, which produces a glyph for the card's own star badge, not menu text.</summary>
public sealed class FavoriteToMenuTextConverter : IValueConverter
{
    public static readonly FavoriteToMenuTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Remove from favorites" : "Add to favorites";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
