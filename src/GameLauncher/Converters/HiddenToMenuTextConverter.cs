using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Menu text for the card's Hidden toggle - same command either way
/// (LibraryViewModel.ToggleHiddenCommand just flips the bool), only the label changes. Distinct from
/// HiddenToTooltipConverter's terse "Hide"/"Unhide", which is sized for an icon button's tooltip
/// rather than a menu row that has to say what it acts on.</summary>
public sealed class HiddenToMenuTextConverter : IValueConverter
{
    public static readonly HiddenToMenuTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Unhide" : "Hide";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
