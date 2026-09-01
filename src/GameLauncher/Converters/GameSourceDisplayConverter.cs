using System.Globalization;
using System.Windows.Data;
using GameLauncher.Models;

namespace GameLauncher.Converters;

/// <summary>GameSource.ToString() reads fine for Steam/Epic/Manual but wrong for acronyms
/// (Gog -> "Gog", Ea -> "Ea") - this fixes those for display.</summary>
public sealed class GameSourceDisplayConverter : IValueConverter
{
    public static readonly GameSourceDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            GameSource.Gog => "GOG",
            GameSource.Ea => "EA",
            GameSource source => source.ToString(),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
