using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>The words on the filters' hidden-games button: what pressing it does.</summary>
public sealed class HiddenGamesButtonTextConverter : IValueConverter
{
    public static readonly HiddenGamesButtonTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Hide hidden games" : "Show hidden games";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
