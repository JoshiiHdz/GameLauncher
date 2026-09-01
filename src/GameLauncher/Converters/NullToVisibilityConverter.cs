using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameLauncher.Converters;

/// <summary>Visible when the bound value is null - used to show a placeholder icon behind an
/// Image whose Source came back null (art fetch failed and there was no exe to extract an icon from).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
