using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>
/// A sidebar view button (All games, Favorites, ...) looks selected only while the library itself is showing: with a page such as
/// Settings or Stats open over it, the page's own entry is the selected one. Values bound in: [0] = the view is the current one,
/// [1] = a page is open.
/// </summary>
public sealed class ViewActiveConverter : IMultiValueConverter
{
    public static readonly ViewActiveConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values[0] is true && values[1] is false;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
