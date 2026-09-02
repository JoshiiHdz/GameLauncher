using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Plain bool negation, for IsEnabled-style bindings where the existing bool converters
/// (which all target Visibility) don't fit - e.g. disabling a button while IsLoading is true.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
