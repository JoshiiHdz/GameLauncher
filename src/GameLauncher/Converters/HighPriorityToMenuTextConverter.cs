using System.Globalization;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Menu text for a game's High priority switch: says what choosing it does (the state it is in now decides which).</summary>
public sealed class HighPriorityToMenuTextConverter : IValueConverter
{
    public static readonly HighPriorityToMenuTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Turn off high priority" : "Turn on high priority";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
