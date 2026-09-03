using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Tooltip text for the game card's hide/unhide button - same command and button either
/// way (LibraryViewModel.ToggleHiddenCommand just flips the bool), only the label changes.</summary>
public sealed class HiddenToTooltipConverter : IValueConverter
{
    public static readonly HiddenToTooltipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Unhide" : "Hide";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
